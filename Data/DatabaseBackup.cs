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

    /// <summary>Datos de una tabla como INSERT de varias filas, leídos en streaming (sin cargar la tabla en memoria).</summary>
    private static async Task<long> WriteRowsAsync(DbConnection conn, StreamWriter writer, string quotedTable, DbKind kind, CancellationToken token)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {quotedTable}";
        cmd.CommandTimeout = 0;
        await using var reader = await cmd.ExecuteReaderAsync(token);
        string header = $"INSERT INTO {quotedTable} ({string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(i => Db.QuoteId(reader.GetName(i))))}) VALUES";

        long count = 0;
        var values = new object?[reader.FieldCount];
        while (await reader.ReadAsync(token))
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? null : SafeValue(reader, i);

            await writer.WriteLineAsync(count % InsertBatch == 0 ? (count > 0 ? ";\n" : "") + header : ",");
            await writer.WriteAsync("    (" + string.Join(", ", values.Select(v => ResultExporter.SqlLiteral(v, kind))) + ")");
            count++;
        }
        if (count > 0) await writer.WriteLineAsync(";");
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
        SqlSplitter.Split(File.ReadAllText(path), mysql: kind == DbKind.MySql);

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
