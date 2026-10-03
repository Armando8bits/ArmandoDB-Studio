using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace MySmdb;

public enum DbKind { MySql, Sqlite }

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

    [JsonIgnore]
    public string DefaultName => Kind == DbKind.Sqlite
        ? $"{Path.GetFileName(FilePath)} ({Path.GetDirectoryName(FilePath)})"
        : $"{User}@{Host}:{Port}";

    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(Alias) ? DefaultName : Alias;

    /// <summary>Línea de detalle para la lista de conexiones guardadas.</summary>
    [JsonIgnore]
    public string Summary => Kind == DbKind.Sqlite ? $"SQLite · {FilePath}" : $"MySQL · {User}@{Host}:{Port}";

    public override string ToString() => Name;

    public DbConnection CreateConnection(string? database)
    {
        if (Kind == DbKind.Sqlite)
        {
            // Sin pool, para que el archivo quede libre al cerrar la pestaña.
            var sqlite = new SqliteConnectionStringBuilder { DataSource = FilePath ?? "", Pooling = false };
            return new SqliteConnection(sqlite.ConnectionString);
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = Port,
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
                p.Password = Unprotect(p.ProtectedPassword);
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

/// <summary>Consultas auxiliares del explorador de objetos; aquí vive lo que cambia entre MySQL y SQLite.</summary>
public static class Db
{
    /// <summary>Consulta auxiliar en una conexión de corta duración.</summary>
    public static async Task<List<string?[]>> QueryAsync(ConnectionProfile profile, string? database, string sql, params string[] args)
    {
        await using var conn = profile.CreateConnection(database);
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
        if (profile.Kind == DbKind.Sqlite)
            return (await QueryAsync(profile, null, "PRAGMA database_list")).Select(r => r[1]!).ToList();
        return (await QueryAsync(profile, null, "SHOW DATABASES")).Select(r => r[0]!).ToList();
    }

    public static async Task<List<(string Name, bool IsView)>> ListTablesAsync(ConnectionProfile profile, string database)
    {
        var rows = profile.Kind == DbKind.Sqlite
            ? await QueryAsync(profile, null,
                "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name")
            : await QueryAsync(profile, null,
                "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = @p0 ORDER BY TABLE_NAME", database);
        return rows.Select(r => (r[0]!, string.Equals(r[1], "VIEW", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Columnas ya formateadas para el árbol: "nombre (tipo, PK, null)".</summary>
    public static async Task<List<string>> ListColumnsAsync(ConnectionProfile profile, string database, string table)
    {
        if (profile.Kind == DbKind.Sqlite)
        {
            var info = await QueryAsync(profile, null, "SELECT name, type, \"notnull\", pk FROM pragma_table_info(@p0)", table);
            return info.Select(r => Describe(r[0], r[1], r[3] != "0", r[2] != "1")).ToList();
        }

        var rows = await QueryAsync(profile, null,
            "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA = @p0 AND TABLE_NAME = @p1 ORDER BY ORDINAL_POSITION", database, table);
        return rows.Select(r => Describe(r[0], r[1], r[3] == "PRI", r[2] == "YES")).ToList();
    }

    private static string Describe(string? name, string? type, bool primaryKey, bool nullable) =>
        $"{name} ({(string.IsNullOrEmpty(type) ? "sin tipo" : type)}{(primaryKey ? ", PK" : "")}{(nullable ? ", null" : ", not null")})";

    public static async Task<string> GetCreateScriptAsync(ConnectionProfile profile, string database, string name, bool isView)
    {
        if (profile.Kind == DbKind.Sqlite)
        {
            var rows = await QueryAsync(profile, null, "SELECT sql FROM sqlite_master WHERE name = @p0", name);
            return rows[0][0] + ";";
        }

        var result = await QueryAsync(profile, database, $"SHOW CREATE {(isView ? "VIEW" : "TABLE")} {QuoteId(database)}.{QuoteId(name)}");
        return result[0][1] + ";";
    }

    public static int ErrorCode(DbException ex) => ex switch
    {
        MySqlException mysql => mysql.Number,
        SqliteException sqlite => sqlite.SqliteErrorCode,
        _ => ex.ErrorCode,
    };

    public static string Prompt(DbKind kind) => kind == DbKind.Sqlite ? "sqlite> " : "mysql> ";

    /// <summary>Los acentos graves valen en MySQL y SQLite los acepta por compatibilidad.</summary>
    public static string QuoteId(string name) => "`" + name.Replace("`", "``") + "`";
}
