using System.Data.Common;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MySmdb;

public sealed record BackupOptions(IReadOnlyList<string> Tables, bool Structure, bool Data, bool DropFirst, bool OtherObjects);

/// <summary>Avance de una copia o restauración: qué se está haciendo y cuánto va.</summary>
public sealed record BackupProgress(string Step, int Done, int Total);

public sealed record BackupSummary(int Tables, long Rows, int OtherObjects);

public sealed record RestoreSummary(int Executed, int Total, string? Error, int ErrorLine);

/// <summary>
/// Copia de seguridad de una base a un archivo .sql (estructura y datos) y restauración desde un .sql.
/// El archivo usa la misma sintaxis que el editor (DELIMITER para rutinas), así que también puede abrirse y ejecutarse a mano.
/// </summary>
public static class DatabaseBackup
{
    private const int InsertBatch = 200;

    // CREATE DEFINER=`usuario`@`host` ...: el usuario que creó el objeto puede no existir en otro servidor.
    private static readonly Regex Definer = new(@"\s+DEFINER\s*=\s*(`[^`]*`|\S+?)@(`[^`]*`|\S+)", RegexOptions.IgnoreCase);

    public static async Task<BackupSummary> ExportAsync(ConnectionProfile profile, string database, BackupOptions options, string path,
        IProgress<BackupProgress> progress, CancellationToken token)
    {
        if (profile.Kind == DbKind.SqlServer) return await ExportSqlServerAsync(profile, database, options, path, progress, token);

        bool mysql = profile.Kind == DbKind.MySql;
        long rows = 0;
        int others = 0;

        await using var conn = await Task.Run(() => profile.CreateConnection(database), token);
        await conn.OpenAsync(token);
        // Una foto coherente de los datos aunque otros sigan escribiendo mientras tanto (InnoDB); no bloquea a nadie.
        if (mysql) await ExecuteAsync(conn, "START TRANSACTION WITH CONSISTENT SNAPSHOT", token);

        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync($"-- Copia de seguridad de {Db.QuoteId(database)} ({profile.Name})");
        await writer.WriteLineAsync($"-- Generada por {App.Name} el {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync(mysql ? "SET NAMES utf8mb4;\nSET FOREIGN_KEY_CHECKS = 0;" : "PRAGMA foreign_keys = OFF;\nBEGIN TRANSACTION;");

        for (int i = 0; i < options.Tables.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string table = options.Tables[i];
            string quoted = Db.QuoteId(table);
            progress.Report(new BackupProgress($"Tabla {table}", i, options.Tables.Count));
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"-- ---------- Tabla {quoted} ----------");

            if (options.Structure)
            {
                if (options.DropFirst) await writer.WriteLineAsync($"DROP TABLE IF EXISTS {quoted};");
                if (mysql)
                {
                    await writer.WriteLineAsync(await ScalarAsync(conn, $"SHOW CREATE TABLE {quoted}", 1, token) + ";");
                }
                else
                {
                    await writer.WriteLineAsync(await ScalarAsync(conn, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @p0", 0, token, table) + ";");
                    foreach (string index in await ColumnAsync(conn, "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = @p0 AND sql IS NOT NULL", token, table))
                        await writer.WriteLineAsync(index + ";");
                }
            }

            if (options.Data)
                rows += await WriteRowsAsync(conn, writer, quoted, profile.Kind, token);
        }

        if (options.OtherObjects)
        {
            progress.Report(new BackupProgress("Vistas, rutinas y triggers", options.Tables.Count, options.Tables.Count));
            others = await WriteOtherObjectsAsync(profile, database, conn, writer, options, token);
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync(mysql ? "SET FOREIGN_KEY_CHECKS = 1;" : "COMMIT;\nPRAGMA foreign_keys = ON;");
        if (mysql) await ExecuteAsync(conn, "ROLLBACK", CancellationToken.None);
        return new BackupSummary(options.Tables.Count, rows, others);
    }

    /// <summary>
    /// SQL Server: no tiene SHOW CREATE, así que la estructura se reconstruye desde el catálogo. El archivo va por
    /// lotes (GO). Las claves foráneas se escriben al final, cuando ya existen todas las tablas y sus datos.
    /// </summary>
    private static async Task<BackupSummary> ExportSqlServerAsync(ConnectionProfile profile, string database, BackupOptions options, string path,
        IProgress<BackupProgress> progress, CancellationToken token)
    {
        long rows = 0;
        int others = 0;
        var foreignKeys = new List<string>();

        await using var conn = await Task.Run(() => profile.CreateConnection(database), token);
        await conn.OpenAsync(token);

        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync($"-- Copia de seguridad de {SqlServerCatalog.Quote(database)} ({profile.Name})");
        await writer.WriteLineAsync($"-- Generada por {App.Name} el {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("SET NOCOUNT ON;\nGO");

        if (options.Structure && options.DropFirst)
        {
            // Antes de borrar una tabla hay que quitar las claves foráneas que apuntan a ella.
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("-- ---------- Claves foráneas que apuntan a las tablas de la copia ----------");
            foreach (string table in options.Tables)
            {
                string literal = "N'" + SqlServerCatalog.QuoteFull(table).Replace("'", "''") + "'";
                await writer.WriteLineAsync(
                    "DECLARE @sql nvarchar(max) = N'';\n" +
                    "SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) " +
                    "+ N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'\n" +
                    $"FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID({literal});\n" +
                    "EXEC sp_executesql @sql;\nGO");
            }
        }

        for (int i = 0; i < options.Tables.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string table = options.Tables[i];
            string quoted = SqlServerCatalog.QuoteFull(table);
            string literal = "N'" + quoted.Replace("'", "''") + "'";
            progress.Report(new BackupProgress($"Tabla {table}", i, options.Tables.Count));
            await writer.WriteLineAsync();
            await writer.WriteLineAsync($"-- ---------- Tabla {quoted} ----------");

            if (options.Structure)
            {
                var script = await SqlServerCatalog.ScriptTableAsync(profile, database, table);
                string schema = SqlServerCatalog.Split(table).Schema;
                if (!schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync($"IF SCHEMA_ID(N'{schema.Replace("'", "''")}') IS NULL EXEC(N'CREATE SCHEMA {SqlServerCatalog.Quote(schema).Replace("'", "''")}');\nGO");
                if (options.DropFirst) await writer.WriteLineAsync($"IF OBJECT_ID({literal}, N'U') IS NOT NULL DROP TABLE {quoted};\nGO");
                await writer.WriteLineAsync(script.CreateTable + "\nGO");
                if (script.Indexes.Count > 0) await writer.WriteLineAsync(string.Join("\n", script.Indexes) + "\nGO");
                foreignKeys.AddRange(script.ForeignKeys);
            }

            if (options.Data)
            {
                // Las columnas calculadas y las de tipo rowversion no admiten valores; las de identidad, solo con IDENTITY_INSERT.
                var columns = new List<string>();
                bool identity = false;
                await using (var cmd = Command(conn,
                    "SELECT name, is_identity FROM sys.columns WHERE object_id = OBJECT_ID(@p0) AND is_computed = 0 AND system_type_id <> 189 ORDER BY column_id",
                    new[] { quoted }))
                await using (var reader = await cmd.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        columns.Add(SqlServerCatalog.Quote(reader.GetString(0)));
                        identity |= reader.GetBoolean(1);
                    }
                }
                if (columns.Count == 0) continue;

                if (identity) await writer.WriteLineAsync($"SET IDENTITY_INSERT {quoted} ON;\nGO");
                rows += await WriteRowsAsync(conn, writer, quoted, DbKind.SqlServer, token, string.Join(", ", columns));
                if (identity) await writer.WriteLineAsync($"SET IDENTITY_INSERT {quoted} OFF;\nGO");
            }
        }

        if (foreignKeys.Count > 0)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("-- ---------- Claves foráneas ----------");
            await writer.WriteLineAsync(string.Join("\n", foreignKeys) + "\nGO");
        }

        if (options.OtherObjects)
        {
            progress.Report(new BackupProgress("Vistas, rutinas y triggers", options.Tables.Count, options.Tables.Count));
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("-- ---------- Vistas, rutinas y triggers ----------");

            // Cada CREATE va solo en su lote. Orden: funciones, vistas, procedimientos y triggers.
            async Task WriteAsync(string name, string keyword)
            {
                string quoted = SqlServerCatalog.QuoteFull(name);
                string definition;
                try { definition = await SqlServerCatalog.GetDefinitionAsync(profile, database, name); }
                catch (InvalidOperationException ex)
                {
                    await writer.WriteLineAsync($"-- {ex.Message}");
                    return;
                }
                if (options.DropFirst)
                    await writer.WriteLineAsync($"IF OBJECT_ID(N'{quoted.Replace("'", "''")}') IS NOT NULL DROP {keyword} {quoted};\nGO");
                await writer.WriteLineAsync(definition + "\nGO");
                others++;
            }

            var routines = await Db.ListRoutinesAsync(profile, database);
            foreach (var (name, _) in routines.Where(r => r.IsFunction)) await WriteAsync(name, "FUNCTION");
            foreach (var (name, isView) in await Db.ListTablesAsync(profile, database))
                if (isView) await WriteAsync(name, "VIEW");
            foreach (var (name, _) in routines.Where(r => !r.IsFunction)) await WriteAsync(name, "PROCEDURE");
            foreach (var (name, _) in await Db.ListTriggersAsync(profile, database)) await WriteAsync(name, "TRIGGER");
        }

        return new BackupSummary(options.Tables.Count, rows, others);
    }

    /// <summary>Datos de una tabla como INSERT de varias filas, leídos en streaming (sin cargar la tabla en memoria).</summary>
    /// <param name="columnList">SQL Server: las columnas a copiar (ya entre corchetes); cada INSERT va en su propio lote.</param>
    private static async Task<long> WriteRowsAsync(DbConnection conn, StreamWriter writer, string quotedTable, DbKind kind, CancellationToken token,
        string? columnList = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {columnList ?? "*"} FROM {quotedTable}";
        cmd.CommandTimeout = 0;
        await using var reader = await cmd.ExecuteReaderAsync(token);
        string header = $"INSERT INTO {quotedTable} ({string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => Db.QuoteId(kind, reader.GetName(i))))}) VALUES";
        string end = kind == DbKind.SqlServer ? ";\nGO" : ";";

        long count = 0;
        var values = new object?[reader.FieldCount];
        while (await reader.ReadAsync(token))
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? null : SafeValue(reader, i);

            await writer.WriteLineAsync(count % InsertBatch == 0 ? (count > 0 ? end + "\n" : "") + header : ",");
            await writer.WriteAsync("    (" + string.Join(", ", values.Select(v => ResultExporter.SqlLiteral(v, kind))) + ")");
            count++;
        }
        if (count > 0) await writer.WriteLineAsync(end);
        return count;
    }

    private static object? SafeValue(DbDataReader reader, int index)
    {
        try { return reader.GetValue(index); }
        catch { return reader.GetString(index); }   // valores que .NET no representa (fechas fuera de rango)
    }

    private static async Task<int> WriteOtherObjectsAsync(ConnectionProfile profile, string database, DbConnection conn, StreamWriter writer,
        BackupOptions options, CancellationToken token)
    {
        int count = 0;
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("-- ---------- Vistas, rutinas y triggers ----------");

        if (profile.Kind == DbKind.Sqlite)
        {
            foreach (string type in new[] { "view", "trigger" })
            {
                var names = await ColumnAsync(conn, $"SELECT name FROM sqlite_master WHERE type = '{type}' ORDER BY name", token);
                var scripts = await ColumnAsync(conn, $"SELECT sql FROM sqlite_master WHERE type = '{type}' ORDER BY name", token);
                for (int i = 0; i < scripts.Count; i++)
                {
                    if (options.DropFirst) await writer.WriteLineAsync($"DROP {type.ToUpperInvariant()} IF EXISTS {Db.QuoteId(names[i])};");
                    await writer.WriteLineAsync(scripts[i] + ";");
                    count++;
                }
            }
            return count;
        }

        foreach (var (name, isView) in await Db.ListTablesAsync(profile, database))
        {
            if (!isView) continue;
            if (options.DropFirst) await writer.WriteLineAsync($"DROP VIEW IF EXISTS {Db.QuoteId(name)};");
            await writer.WriteLineAsync(Definer.Replace(await ScalarAsync(conn, $"SHOW CREATE VIEW {Db.QuoteId(name)}", 1, token), "") + ";");
            count++;
        }
        foreach (var (name, isFunction) in await Db.ListRoutinesAsync(profile, database))
        {
            string keyword = isFunction ? "FUNCTION" : "PROCEDURE";
            if (options.DropFirst) await writer.WriteLineAsync($"DROP {keyword} IF EXISTS {Db.QuoteId(name)};");
            await WriteDelimitedAsync(writer, await ScalarAsync(conn, $"SHOW CREATE {keyword} {Db.QuoteId(name)}", 2, token));
            count++;
        }
        foreach (var (name, _) in await Db.ListTriggersAsync(profile, database))
        {
            if (options.DropFirst) await writer.WriteLineAsync($"DROP TRIGGER IF EXISTS {Db.QuoteId(name)};");
            await WriteDelimitedAsync(writer, await ScalarAsync(conn, $"SHOW CREATE TRIGGER {Db.QuoteId(name)}", 2, token));
            count++;
        }
        return count;
    }

    private static async Task WriteDelimitedAsync(StreamWriter writer, string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            await writer.WriteLineAsync("-- (definición no disponible: faltan privilegios sobre el objeto)");
            return;
        }
        await writer.WriteLineAsync($"DELIMITER $$\n{Definer.Replace(body, "")}$$\nDELIMITER ;");
    }

    // ---------- Restauración ----------

    /// <summary>Sentencias de un archivo .sql, listas para ejecutar (y para decir cuántas son antes de confirmar).</summary>
    public static List<SqlStatement> ReadScript(string path, DbKind kind) =>
        SqlSplitter.Split(File.ReadAllText(path), kind);

    /// <summary>Ejecuta las sentencias en orden y se detiene en el primer error.</summary>
    public static async Task<RestoreSummary> RestoreAsync(ConnectionProfile profile, string database, IReadOnlyList<SqlStatement> statements,
        IProgress<BackupProgress> progress, CancellationToken token)
    {
        await using var conn = await Task.Run(() => profile.CreateConnection(database), token);
        await conn.OpenAsync(token);
        for (int i = 0; i < statements.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i % 20 == 0) progress.Report(new BackupProgress("Ejecutando sentencias", i, statements.Count));
            try
            {
                await ExecuteAsync(conn, statements[i].Text, token);
            }
            catch (DbException ex) when (!token.IsCancellationRequested)
            {
                return new RestoreSummary(i, statements.Count, ex.Message, statements[i].Line);
            }
        }
        return new RestoreSummary(statements.Count, statements.Count, null, 0);
    }

    // ---------- Utilidades ----------

    private static async Task ExecuteAsync(DbConnection conn, string sql, CancellationToken token)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task<string> ScalarAsync(DbConnection conn, string sql, int column, CancellationToken token, params string[] args)
    {
        await using var cmd = Command(conn, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) && !reader.IsDBNull(column) ? Convert.ToString(reader.GetValue(column)) ?? "" : "";
    }

    private static async Task<List<string>> ColumnAsync(DbConnection conn, string sql, CancellationToken token, params string[] args)
    {
        var values = new List<string>();
        await using var cmd = Command(conn, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            values.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0)) ?? "");
        return values;
    }

    private static DbCommand Command(DbConnection conn, string sql, string[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@p" + i;
            parameter.Value = args[i];
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }
}
