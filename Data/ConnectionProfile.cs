using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace MySmdb;

public enum DbKind { MySql, Sqlite, SqlServer }

public class ConnectionProfile
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DbKind Kind { get; set; } = DbKind.MySql;

    /// <summary>Archivo de la base de datos (solo SQLite).</summary>
    public string? FilePath { get; set; }

    public string Host { get; set; } = "localhost";
    public uint Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string? Database { get; set; }

    /// <summary>Contraseña cifrada con DPAPI (solo la puede leer este usuario de Windows).</summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore] public string Password { get; set; } = "";

    /// <summary>Nombre elegido por el usuario para la conexión guardada (opcional).</summary>
    public string? Alias { get; set; }

    /// <summary>Conexión de producción: se resalta en rojo y pide confirmar cualquier sentencia que modifique.</summary>
    public bool IsProduction { get; set; }

    /// <summary>SQL Server: entrar con el usuario de Windows en lugar de usuario y contraseña.</summary>
    public bool IntegratedSecurity { get; set; }

    /// <summary>
    /// SQL Server: el servidor va con instancia con nombre ("SERVIDOR\INSTANCIA") o es LocalDB
    /// ("(localdb)\MSSQLLocalDB"). En ese caso no se usa el puerto: lo resuelve el propio servidor.
    /// </summary>
    [JsonIgnore] public bool HasInstanceName => Host.Contains('\\') || Host.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase);

    // ----- Túnel SSH (MySQL y SQL Server). Con túnel, Host y Port son los de la base vistos desde el servidor SSH. -----

    public bool UseSsh { get; set; }
    public string? SshHost { get; set; }
    public uint SshPort { get; set; } = 22;
    public string? SshUser { get; set; }
    /// <summary>Archivo de clave privada (OpenSSH o PuTTY convertido a OpenSSH), opcional.</summary>
    public string? SshKeyFile { get; set; }
    /// <summary>Huella SHA-256 del servidor SSH aceptada en la primera conexión.</summary>
    public string? SshHostKey { get; set; }
    public string? ProtectedSshPassword { get; set; }
    public string? ProtectedSshPassphrase { get; set; }
    [JsonIgnore] public string SshPassword { get; set; } = "";
    [JsonIgnore] public string SshPassphrase { get; set; } = "";

    /// <summary>"usuario@servidor:puerto"; en SQL Server, sin puerto si hay instancia y "Windows" si no hay usuario.</summary>
    [JsonIgnore]
    private string ServerLabel =>
        (Kind == DbKind.SqlServer && IntegratedSecurity ? "Windows" : User) + "@" + Host
        + (Kind == DbKind.SqlServer && HasInstanceName ? "" : ":" + Port);

    [JsonIgnore]
    public string DefaultName => Kind == DbKind.Sqlite
        ? $"{Path.GetFileName(FilePath)} ({Path.GetDirectoryName(FilePath)})"
        : UseSsh ? $"{ServerLabel} vía {SshHost}" : ServerLabel;

    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(Alias) ? DefaultName : Alias;

    /// <summary>Línea de detalle para la lista de conexiones guardadas.</summary>
    [JsonIgnore]
    public string Summary => Kind == DbKind.Sqlite
        ? $"SQLite · {FilePath}"
        : $"{(Kind == DbKind.SqlServer ? "SQL Server" : "MySQL")} · {ServerLabel}" + (UseSsh ? $" · SSH {SshUser}@{SshHost}" : "");

    public override string ToString() => Name;

    public DbConnection CreateConnection(string? database)
    {
        if (Kind == DbKind.Sqlite)
        {
            // Sin pool, para que el archivo quede libre al cerrar la pestaña.
            var sqlite = new SqliteConnectionStringBuilder { DataSource = FilePath ?? "", Pooling = false };
            return new SqliteConnection(sqlite.ConnectionString);
        }

        if (Kind == DbKind.SqlServer)
        {
            var sqlServer = new SqlConnectionStringBuilder
            {
                // Con túnel, el extremo local; con instancia con nombre o LocalDB, sin puerto; si no, "servidor,puerto".
                DataSource = UseSsh ? $"127.0.0.1,{SshTunnels.LocalPort(this)}" : HasInstanceName ? Host : $"{Host},{Port}",
                InitialCatalog = database ?? "",
                IntegratedSecurity = IntegratedSecurity,
                // Cada pestaña es una sesión propia, como en SSMS.
                Pooling = false,
                // Cifrado sí, pero sin exigir un certificado de una autoridad: lo normal en servidores internos.
                TrustServerCertificate = true,
                ConnectTimeout = 15,
                ApplicationName = App.Name,
            };
            if (!IntegratedSecurity)
            {
                sqlServer.UserID = User;
                sqlServer.Password = Password;
            }
            return new SqlConnection(sqlServer.ConnectionString);
        }

        // Con túnel, se conecta al extremo local del túnel (lo abre si hace falta).
        var builder = new MySqlConnectionStringBuilder
        {
            Server = UseSsh ? "127.0.0.1" : Host,
            Port = UseSsh ? SshTunnels.LocalPort(this) : Port,
            UserID = User,
            Password = Password,
            Database = database ?? "",
            // Cada pestaña es una sesión propia, como en SSMS.
            Pooling = false,
            // Sin esto, @variable en el SQL se interpreta como parámetro.
            AllowUserVariables = true,
            AllowPublicKeyRetrieval = true,
            ConvertZeroDateTime = true,
            TreatTinyAsBoolean = false,
            ConnectionTimeout = 15,
            DefaultCommandTimeout = 0,
            Keepalive = 30,
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}

public static class ProfileStore
{
    private static readonly string FilePath = Path.Combine(App.DataFolder, "connections.json");

    public static List<ConnectionProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var list = JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(FilePath)) ?? new List<ConnectionProfile>();
            foreach (var p in list)
            {
                p.Password = Unprotect(p.ProtectedPassword);
                p.SshPassword = Unprotect(p.ProtectedSshPassword);
                p.SshPassphrase = Unprotect(p.ProtectedSshPassphrase);
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Guarda el perfil al principio de la lista (el más reciente primero).</summary>
    /// <param name="replaces">Nombre de la conexión que se estaba editando; se sustituye aunque cambie de nombre.</param>
    public static void Save(ConnectionProfile profile, bool rememberPassword, string? replaces = null)
    {
        var list = Load();
        // Reemplaza la del mismo nombre y también la entrada sin nombre del mismo servidor/usuario.
        list.RemoveAll(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)
            || (replaces != null && p.Name.Equals(replaces, StringComparison.OrdinalIgnoreCase))
            || (string.IsNullOrWhiteSpace(p.Alias) && p.DefaultName.Equals(profile.DefaultName, StringComparison.OrdinalIgnoreCase)));
        profile.ProtectedPassword = rememberPassword ? Protect(profile.Password) : null;
        profile.ProtectedSshPassword = rememberPassword && profile.SshPassword.Length > 0 ? Protect(profile.SshPassword) : null;
        profile.ProtectedSshPassphrase = rememberPassword && profile.SshPassphrase.Length > 0 ? Protect(profile.SshPassphrase) : null;
        list.Insert(0, profile);
        Write(list);
    }

    public static void Delete(string name)
    {
        var list = Load();
        list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Write(list);
    }

    private static void Write(List<ConnectionProfile> list)
    {
        // Load() ya descifró las contraseñas; al reescribir se conserva el texto cifrado original.
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }
}

public enum SchemaObject { Table, View, Procedure, Function, Trigger, Index }

public record ColumnInfo(string Name, string Type, bool PrimaryKey, bool Nullable, bool AutoIncrement)
{
    /// <summary>Texto del árbol: "nombre (tipo, PK, null)".</summary>
    public override string ToString() =>
        $"{Name} ({(Type.Length == 0 ? "sin tipo" : Type)}{(PrimaryKey ? ", PK" : "")}{(Nullable ? ", null" : ", not null")})";
}

/// <summary>Una columna de <paramref name="Table"/> que apunta a otra tabla. <paramref name="RefColumn"/> null: a su clave primaria.</summary>
public record ForeignKey(string Table, string Column, string RefTable, string? RefColumn);

public record IndexInfo(string Name, bool Unique, bool Primary, string Columns)
{
    /// <summary>Texto del árbol: "nombre (col1, col2) único".</summary>
    public override string ToString() => $"{Name} ({Columns}){(Primary ? " clave primaria" : Unique ? " único" : "")}";
}

/// <summary>Consultas auxiliares del explorador de objetos; aquí vive lo que cambia entre MySQL y SQLite.</summary>
public static class Db
{
    /// <summary>Consulta auxiliar en una conexión de corta duración.</summary>
    public static async Task<List<string?[]>> QueryAsync(ConnectionProfile profile, string? database, string sql, params string[] args)
    {
        // Fuera del hilo de la interfaz: abrir un túnel SSH puede tardar unos segundos.
        await using var conn = await Task.Run(() => profile.CreateConnection(database));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@p" + i;
            parameter.Value = args[i];
            cmd.Parameters.Add(parameter);
        }

        var rows = new List<string?[]>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new string?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    public static async Task<List<string>> ListDatabasesAsync(ConnectionProfile profile)
    {
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.ListDatabasesAsync(profile);
        if (profile.Kind == DbKind.Sqlite)
            return (await QueryAsync(profile, null, "PRAGMA database_list")).Select(r => r[1]!).ToList();
        return (await QueryAsync(profile, null, "SHOW DATABASES")).Select(r => r[0]!).ToList();
    }

    public static async Task<List<(string Name, bool IsView)>> ListTablesAsync(ConnectionProfile profile, string database)
    {
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.ListTablesAsync(profile, database);
        var rows = profile.Kind == DbKind.Sqlite
            ? await QueryAsync(profile, null,
                "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name")
            : await QueryAsync(profile, null,
                "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = @p0 ORDER BY TABLE_NAME", database);
        return rows.Select(r => (r[0]!, string.Equals(r[1], "VIEW", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static async Task<List<ColumnInfo>> GetColumnsAsync(ConnectionProfile profile, string database, string table)
    {
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.GetColumnsAsync(profile, database, table);
        if (profile.Kind == DbKind.Sqlite)
        {
            var info = await QueryAsync(profile, null, "SELECT name, type, \"notnull\", pk FROM pragma_table_info(@p0)", table);
            // Una única PK de tipo INTEGER es el rowid: se autonumera.
            bool singleIntegerKey = info.Count(r => r[3] != "0") == 1;
            return info.Select(r =>
            {
                bool rowid = singleIntegerKey && r[3] != "0" && string.Equals(r[1], "INTEGER", StringComparison.OrdinalIgnoreCase);
                // El rowid nunca es NULL aunque la tabla no lo declare NOT NULL.
                return new ColumnInfo(r[0]!, r[1] ?? "", r[3] != "0", r[2] != "1" && !rowid, rowid);
            }).ToList();
        }

        var rows = await QueryAsync(profile, null,
            "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, EXTRA FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = @p0 AND TABLE_NAME = @p1 ORDER BY ORDINAL_POSITION", database, table);
        return rows.Select(r => new ColumnInfo(r[0]!, r[1] ?? "", r[3] == "PRI", r[2] == "YES",
            (r[4] ?? "").Contains("auto_increment", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Procedimientos y funciones almacenados (SQLite no tiene).</summary>
    public static async Task<List<(string Name, bool IsFunction)>> ListRoutinesAsync(ConnectionProfile profile, string database)
    {
        if (profile.Kind == DbKind.Sqlite) return new();
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.ListRoutinesAsync(profile, database);
        var rows = await QueryAsync(profile, null,
            "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @p0 ORDER BY ROUTINE_NAME", database);
        return rows.Select(r => (r[0]!, r[1] == "FUNCTION")).ToList();
    }

    public static async Task<List<(string Name, string Table)>> ListTriggersAsync(ConnectionProfile profile, string database)
    {
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.ListTriggersAsync(profile, database);
        var rows = profile.Kind == DbKind.Sqlite
            ? await QueryAsync(profile, null, "SELECT name, tbl_name FROM sqlite_master WHERE type = 'trigger' ORDER BY name")
            : await QueryAsync(profile, null,
                "SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = @p0 ORDER BY TRIGGER_NAME", database);
        return rows.Select(r => (r[0]!, r[1] ?? "")).ToList();
    }

    public static async Task<List<IndexInfo>> ListIndexesAsync(ConnectionProfile profile, string database, string table)
    {
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.ListIndexesAsync(profile, database, table);
        if (profile.Kind == DbKind.Sqlite)
        {
            var info = await QueryAsync(profile, null,
                "SELECT il.name, il.\"unique\", il.origin, (SELECT group_concat(ii.name, ', ') FROM pragma_index_info(il.name) ii) " +
                "FROM pragma_index_list(@p0) il ORDER BY il.name", table);
            return info.Select(r => new IndexInfo(r[0]!, r[1] == "1", r[2] == "pk", r[3] ?? "")).ToList();
        }

        var rows = await QueryAsync(profile, null,
            "SELECT INDEX_NAME, MIN(NON_UNIQUE), GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ', ') " +
            "FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @p0 AND TABLE_NAME = @p1 " +
            "GROUP BY INDEX_NAME ORDER BY INDEX_NAME = 'PRIMARY' DESC, INDEX_NAME", database, table);
        return rows.Select(r => new IndexInfo(r[0]!, r[1] == "0", r[0] == "PRIMARY", r[2] ?? "")).ToList();
    }

    /// <summary>Todas las tablas de la base (sin vistas) con sus columnas, en una sola consulta. Para el diagrama.</summary>
    public static async Task<List<(string Table, List<ColumnInfo> Columns)>> GetAllTablesAsync(ConnectionProfile profile, string database)
    {
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.GetAllTablesAsync(profile, database);

        var tables = new List<(string Table, List<ColumnInfo> Columns)>();
        void Add(string table, ColumnInfo column)
        {
            if (tables.Count == 0 || tables[^1].Table != table) tables.Add((table, new List<ColumnInfo>()));
            tables[^1].Columns.Add(column);
        }

        if (profile.Kind == DbKind.Sqlite)
        {
            var rows = await QueryAsync(profile, null,
                "SELECT m.name, p.name, p.type, p.\"notnull\", p.pk FROM sqlite_master m JOIN pragma_table_info(m.name) p " +
                "WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%' ORDER BY m.name, p.cid");
            foreach (var r in rows)
                Add(r[0]!, new ColumnInfo(r[1]!, r[2] ?? "", r[4] != "0", r[3] != "1" && r[4] == "0", false));
            return tables;
        }

        var columns = await QueryAsync(profile, null,
            "SELECT c.TABLE_NAME, c.COLUMN_NAME, c.COLUMN_TYPE, c.IS_NULLABLE, c.COLUMN_KEY, c.EXTRA " +
            "FROM information_schema.COLUMNS c JOIN information_schema.TABLES t " +
            "ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME " +
            "WHERE c.TABLE_SCHEMA = @p0 AND t.TABLE_TYPE = 'BASE TABLE' ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION", database);
        foreach (var r in columns)
            Add(r[0]!, new ColumnInfo(r[1]!, r[2] ?? "", r[4] == "PRI", r[3] == "YES",
                (r[5] ?? "").Contains("auto_increment", StringComparison.OrdinalIgnoreCase)));
        return tables;
    }

    /// <summary>Claves foráneas declaradas entre tablas de la misma base.</summary>
    public static async Task<List<ForeignKey>> ListForeignKeysAsync(ConnectionProfile profile, string database)
    {
        if (profile.Kind == DbKind.SqlServer) return await SqlServerCatalog.ListForeignKeysAsync(profile, database);
        var rows = profile.Kind == DbKind.Sqlite
            ? await QueryAsync(profile, null,
                "SELECT m.name, f.\"from\", f.\"table\", f.\"to\" FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) f " +
                "WHERE m.type = 'table' ORDER BY m.name, f.id, f.seq")
            : await QueryAsync(profile, null,
                "SELECT TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE " +
                "WHERE TABLE_SCHEMA = @p0 AND REFERENCED_TABLE_SCHEMA = @p0 AND REFERENCED_TABLE_NAME IS NOT NULL " +
                "ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION", database);
        // En SQLite, "to" puede venir vacío: la referencia es a la clave primaria de la otra tabla.
        return rows.Select(r => new ForeignKey(r[0]!, r[1]!, r[2]!, r[3])).ToList();
    }

    /// <summary>Script CREATE de cualquier objeto. Rutinas y triggers van entre DELIMITER para poder volver a ejecutarlos.</summary>
    public static async Task<string> GetCreateScriptAsync(ConnectionProfile profile, string database, SchemaObject kind, string name, string? table = null)
    {
        if (profile.Kind == DbKind.SqlServer)
        {
            switch (kind)
            {
                case SchemaObject.Table:
                    return await SqlServerCatalog.ScriptTableTextAsync(profile, database, name);
                case SchemaObject.Index:
                    var found = (await ListIndexesAsync(profile, database, table!)).FirstOrDefault(i => i.Name == name)
                        ?? throw new InvalidOperationException($"No se encontró el índice {name}.");
                    string keys = string.Join(", ", found.Columns.Split(", ").Select(SqlServerCatalog.Quote));
                    string on = SqlServerCatalog.QuoteFull(table!);
                    return (found.Primary
                        ? $"ALTER TABLE {on} ADD CONSTRAINT {SqlServerCatalog.Quote(name)} PRIMARY KEY ({keys});"
                        : $"CREATE {(found.Unique ? "UNIQUE " : "")}INDEX {SqlServerCatalog.Quote(name)} ON {on} ({keys});") + Environment.NewLine + "GO";
                default:
                    // Vistas, procedimientos, funciones y triggers: su texto original. Cada uno debe ir solo en su lote.
                    return await SqlServerCatalog.GetDefinitionAsync(profile, database, name) + Environment.NewLine + "GO";
            }
        }

        string full = $"{QuoteId(database)}.{QuoteId(name)}";

        if (kind == SchemaObject.Index)
        {
            if (profile.Kind == DbKind.Sqlite)
            {
                var sql = await QueryAsync(profile, null, "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @p0", name);
                if (sql.Count > 0 && sql[0][0] != null) return sql[0][0] + ";";
            }
            var index = (await ListIndexesAsync(profile, database, table!)).FirstOrDefault(i => i.Name == name)
                ?? throw new InvalidOperationException($"No se encontró el índice {name}.");
            string columns = string.Join(", ", index.Columns.Split(", ").Select(QuoteId));
            string target = $"{QuoteId(database)}.{QuoteId(table!)}";
            if (index.Primary) return $"ALTER TABLE {target} ADD PRIMARY KEY ({columns});";
            if (profile.Kind == DbKind.Sqlite)
                return $"-- Índice creado automáticamente por una restricción UNIQUE; equivale a:\nCREATE UNIQUE INDEX {QuoteId(name)} ON {target} ({columns});";
            return $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {QuoteId(name)} ON {target} ({columns});";
        }

        if (profile.Kind == DbKind.Sqlite)
        {
            var rows = await QueryAsync(profile, null, "SELECT sql FROM sqlite_master WHERE name = @p0", name);
            return rows[0][0] + ";";
        }

        switch (kind)
        {
            case SchemaObject.Table:
            case SchemaObject.View:
                var result = await QueryAsync(profile, database, $"SHOW CREATE {(kind == SchemaObject.View ? "VIEW" : "TABLE")} {full}");
                return result[0][1] + ";";

            default:
                string keyword = kind switch { SchemaObject.Procedure => "PROCEDURE", SchemaObject.Function => "FUNCTION", _ => "TRIGGER" };
                var routine = await QueryAsync(profile, database, $"SHOW CREATE {keyword} {full}");
                string body = routine[0][2]
                    ?? throw new InvalidOperationException("El servidor no devolvió la definición: hace falta ser su propietario o tener privilegios sobre ella.");
                return $"DELIMITER $$\n{body}$$\nDELIMITER ;";
        }
    }

    public static int ErrorCode(DbException ex) => ex switch
    {
        MySqlException mysql => mysql.Number,
        SqlException sqlServer => sqlServer.Number,
        SqliteException sqlite => sqlite.SqliteErrorCode,
        _ => ex.ErrorCode,
    };

    public static string Prompt(DbKind kind) => kind switch { DbKind.Sqlite => "sqlite> ", DbKind.SqlServer => "mssql> ", _ => "mysql> " };

    /// <summary>Los acentos graves valen en MySQL y SQLite los acepta por compatibilidad.</summary>
    public static string QuoteId(string name) => "`" + name.Replace("`", "``") + "`";

    /// <summary>Nombre de columna u otro identificador simple, con las comillas del motor: `nombre` o [nombre].</summary>
    public static string QuoteId(DbKind kind, string name) => kind == DbKind.SqlServer ? SqlServerCatalog.Quote(name) : QuoteId(name);

    /// <summary>
    /// Nombre de una tabla, vista o rutina para usarlo en una sentencia: `base`.`tabla` en MySQL, `tabla` en SQLite
    /// y [esquema].[tabla] en SQL Server (sin la base: las pestañas ya trabajan dentro de ella).
    /// </summary>
    public static string FullName(ConnectionProfile profile, string? database, string name) => profile.Kind switch
    {
        DbKind.SqlServer => SqlServerCatalog.QuoteFull(name),
        DbKind.Sqlite => QuoteId(name),
        _ => database == null ? QuoteId(name) : $"{QuoteId(database)}.{QuoteId(name)}",
    };

    /// <summary>Nombre de una tabla dentro de la base ya seleccionada (copias de seguridad): `tabla` o [esquema].[tabla].</summary>
    public static string LocalName(DbKind kind, string name) => kind == DbKind.SqlServer ? SqlServerCatalog.QuoteFull(name) : QuoteId(name);
}
