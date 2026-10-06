using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MySmdb;

/// <summary>
/// Consultas del explorador para Sybase ASE, sobre sus tablas de sistema (sysobjects, syscolumns, sysindexes...).
/// Como en SQL Server, los objetos se nombran "propietario.nombre" (dbo.clientes).
/// Solo usa funciones presentes desde versiones antiguas del servidor; lo que hay que componer se hace aquí, no en SQL.
/// </summary>
public static class SybaseCatalog
{
    /// <summary>Máximo de columnas por índice o clave foránea que se leen (index_col y fokeyN van una a una).</summary>
    private const int MaxKeys = 16;

    private static readonly Regex Plain = new(@"^[A-Za-z_][A-Za-z0-9_#@$]*$");
    private static readonly string[] SystemDatabases =
        { "master", "model", "tempdb", "sybsystemprocs", "sybsystemdb", "sybsecurity", "sybmgmtdb", "sybpcidb", "dbccdb", "sybdiag" };

    /// <summary>Los nombres normales van tal cual (así el script vale en servidores antiguos); los demás, entre corchetes.</summary>
    public static string Quote(string name) => Plain.IsMatch(name) ? name : "[" + name.Replace("]", "]]") + "]";

    /// <summary>"dbo.clientes" → "dbo.clientes" (o con corchetes en la parte que los necesite).</summary>
    public static string QuoteFull(string name)
    {
        var (owner, obj) = SqlServerCatalog.Split(name);
        return $"{Quote(owner)}.{Quote(obj)}";
    }

    /// <summary>Texto como literal SQL.</summary>
    private static string Lit(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>object_id('propietario.nombre') del objeto.</summary>
    private static string ObjectId(string name)
    {
        var (owner, obj) = SqlServerCatalog.Split(name);
        return $"object_id({Lit(owner + "." + obj)})";
    }

    private static int Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : 0;

    /// <summary>Tipo como se escribe en un CREATE TABLE: varchar(50), numeric(10,2), univarchar(20)...</summary>
    private static string FormatType(string type, int length, int precision, int scale, int nationalSize, int unicodeSize) => type.ToLowerInvariant() switch
    {
        "char" or "varchar" or "binary" or "varbinary" => $"{type}({length})",
        "nchar" or "nvarchar" => $"{type}({length / Math.Max(1, nationalSize)})",
        "unichar" or "univarchar" => $"{type}({length / Math.Max(1, unicodeSize)})",
        "numeric" or "decimal" => $"{type}({precision},{scale})",
        _ => type,
    };

    // syscolumns.status: 8 = admite NULL, 128 = columna de identidad.
    private const int Nullable = 8, Identity = 128;
    // sysindexes.status: 2 = único, 16 = agrupado, 2048 = es la clave primaria. status2: 512 = agrupado en tablas de bloqueo por fila.
    private const int Unique = 2, Clustered = 16, PrimaryKey = 2048, ClusteredDol = 512;

    private sealed record Index(string Name, bool IsUnique, bool IsPrimary, bool IsClustered, List<string> Columns);

    /// <summary>index_col(tabla, índice, 1) ... index_col(tabla, índice, 16): las columnas del índice, una por columna del resultado.</summary>
    private static string IndexColumns(string tableExpression) =>
        string.Join(", ", Enumerable.Range(1, MaxKeys).Select(n => $"index_col({tableExpression}, i.indid, {n})"));

    public static async Task<List<string>> ListDatabasesAsync(ConnectionProfile profile)
    {
        var names = (await Db.QueryAsync(profile, null, "select name from master..sysdatabases order by name")).Select(r => r[0]!.Trim()).ToList();
        // Las del usuario primero; las del sistema, al final.
        return names.OrderBy(n => SystemDatabases.Contains(n, StringComparer.OrdinalIgnoreCase) || n.StartsWith("tempdb", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<List<(string Name, bool IsView)>> ListTablesAsync(ConnectionProfile profile, string database) =>
        (await Db.QueryAsync(profile, database,
            "select u.name, o.name, o.type from sysobjects o, sysusers u where u.uid = o.uid and o.type in ('U', 'V') order by u.name, o.name"))
        .Select(r => ($"{r[0]!.Trim()}.{r[1]!.Trim()}", r[2]!.Trim() == "V")).ToList();

    private static async Task<List<Index>> IndexesAsync(ConnectionProfile profile, string database, string table)
    {
        string name = Lit(string.Join(".", SqlServerCatalog.Split(table).Schema, SqlServerCatalog.Split(table).Name));
        var rows = await Db.QueryAsync(profile, database,
            $"select i.name, i.status, i.status2, {IndexColumns(name)} from sysindexes i " +
            $"where i.id = {ObjectId(table)} and i.indid > 0 and i.indid < 255 order by i.indid");
        return rows.Select(r =>
        {
            int status = Int(r[1]), status2 = Int(r[2]);
            var columns = r.Skip(3).TakeWhile(c => !string.IsNullOrEmpty(c)).Select(c => c!.Trim()).ToList();
            return new Index(r[0]!.Trim(), (status & Unique) != 0, (status & PrimaryKey) != 0, (status & Clustered) != 0 || (status2 & ClusteredDol) != 0, columns);
        }).Where(i => i.Columns.Count > 0).ToList();
    }

    public static async Task<List<ColumnInfo>> GetColumnsAsync(ConnectionProfile profile, string database, string table)
    {
        var rows = await Db.QueryAsync(profile, database,
            "select c.name, t.name, c.length, c.prec, c.scale, c.status, @@ncharsize, @@unicharsize from syscolumns c, systypes t " +
            $"where c.id = {ObjectId(table)} and t.usertype = c.usertype order by c.colid");
        var primary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var index in (await IndexesAsync(profile, database, table)).Where(i => i.IsPrimary)) primary.UnionWith(index.Columns);
        }
        catch
        {
            // Las vistas no tienen índices; y sin la clave primaria las columnas se muestran igual.
        }
        return rows.Select(r => new ColumnInfo(r[0]!.Trim(), FormatType(r[1]!.Trim(), Int(r[2]), Int(r[3]), Int(r[4]), Int(r[6]), Int(r[7])),
            primary.Contains(r[0]!.Trim()), (Int(r[5]) & Nullable) != 0, (Int(r[5]) & Identity) != 0)).ToList();
    }

    /// <summary>Procedimientos (P) y funciones (SF: SQL, F: Java).</summary>
    public static async Task<List<(string Name, bool IsFunction)>> ListRoutinesAsync(ConnectionProfile profile, string database) =>
        (await Db.QueryAsync(profile, database,
            "select u.name, o.name, o.type from sysobjects o, sysusers u where u.uid = o.uid and o.type in ('P', 'SF', 'F') order by u.name, o.name"))
        .Select(r => ($"{r[0]!.Trim()}.{r[1]!.Trim()}", r[2]!.Trim() != "P")).ToList();

    public static async Task<List<(string Name, string Table)>> ListTriggersAsync(ConnectionProfile profile, string database) =>
        // La tabla del trigger: según la versión, el trigger la guarda en deltrig, o la tabla guarda sus triggers en deltrig/instrig/updtrig.
        (await Db.QueryAsync(profile, database,
            "select distinct u.name, tr.name, tu.name, t.name from sysobjects tr, sysusers u, sysobjects t, sysusers tu " +
            "where tr.type = 'TR' and u.uid = tr.uid and t.type = 'U' and tu.uid = t.uid " +
            "and (t.id = tr.deltrig or t.deltrig = tr.id or t.instrig = tr.id or t.updtrig = tr.id) order by u.name, tr.name"))
        .Select(r => ($"{r[0]!.Trim()}.{r[1]!.Trim()}", $"{r[2]!.Trim()}.{r[3]!.Trim()}")).ToList();

    public static async Task<List<IndexInfo>> ListIndexesAsync(ConnectionProfile profile, string database, string table) =>
        (await IndexesAsync(profile, database, table)).OrderByDescending(i => i.IsPrimary)
        .Select(i => new IndexInfo(i.Name, i.IsUnique, i.IsPrimary, string.Join(", ", i.Columns))).ToList();

    public static async Task<List<(string Table, List<ColumnInfo> Columns)>> GetAllTablesAsync(ConnectionProfile profile, string database)
    {
        // Columnas de la clave primaria de cada tabla (por id de tabla).
        var primary = new Dictionary<string, HashSet<string>>();
        var keys = await Db.QueryAsync(profile, database,
            $"select i.id, {IndexColumns("u.name + '.' + o.name")} from sysindexes i, sysobjects o, sysusers u " +
            $"where o.id = i.id and o.type = 'U' and u.uid = o.uid and i.indid > 0 and i.indid < 255 and i.status & {PrimaryKey} = {PrimaryKey}");
        foreach (var r in keys)
            primary[r[0]!] = new HashSet<string>(r.Skip(1).TakeWhile(c => !string.IsNullOrEmpty(c)).Select(c => c!.Trim()), StringComparer.OrdinalIgnoreCase);

        var tables = new List<(string Table, List<ColumnInfo> Columns)>();
        var rows = await Db.QueryAsync(profile, database,
            "select u.name, o.name, c.name, t.name, c.length, c.prec, c.scale, c.status, @@ncharsize, @@unicharsize, o.id " +
            "from sysobjects o, sysusers u, syscolumns c, systypes t " +
            "where o.type = 'U' and u.uid = o.uid and c.id = o.id and t.usertype = c.usertype order by u.name, o.name, c.colid");
        foreach (var r in rows)
        {
            string table = $"{r[0]!.Trim()}.{r[1]!.Trim()}", column = r[2]!.Trim();
            if (tables.Count == 0 || tables[^1].Table != table) tables.Add((table, new List<ColumnInfo>()));
            bool isKey = primary.TryGetValue(r[10]!, out var columns) && columns.Contains(column);
            tables[^1].Columns.Add(new ColumnInfo(column, FormatType(r[3]!.Trim(), Int(r[4]), Int(r[5]), Int(r[6]), Int(r[8]), Int(r[9])),
                isKey, (Int(r[7]) & Nullable) != 0, (Int(r[7]) & Identity) != 0));
        }
        return tables;
    }

    private sealed record Reference(string Name, string Table, string RefTable, List<(string Column, string RefColumn)> Columns);

    /// <summary>Claves foráneas (sysreferences) de toda la base o de una tabla. Las que apuntan a otra base se omiten.</summary>
    private static async Task<List<Reference>> ReferencesAsync(ConnectionProfile profile, string database, string? table)
    {
        string pairs = string.Join(", ", Enumerable.Range(1, MaxKeys).Select(n => $"col_name(f.tableid, f.fokey{n}), col_name(f.reftabid, f.refkey{n})"));
        var rows = await Db.QueryAsync(profile, database,
            $"select object_name(f.constrid), tu.name, t.name, ru.name, r.name, f.keycnt, {pairs} " +
            "from sysreferences f, sysobjects t, sysusers tu, sysobjects r, sysusers ru " +
            "where t.id = f.tableid and tu.uid = t.uid and r.id = f.reftabid and ru.uid = r.uid and f.pmrydbname is null" +
            (table != null ? $" and f.tableid = {ObjectId(table)}" : "") + " order by tu.name, t.name, f.constrid");
        return rows.Select(r =>
        {
            int count = Math.Clamp(Int(r[5]), 0, MaxKeys);
            var columns = Enumerable.Range(0, count).Where(n => r[6 + n * 2] != null && r[7 + n * 2] != null)
                .Select(n => (r[6 + n * 2]!.Trim(), r[7 + n * 2]!.Trim())).ToList();
            return new Reference((r[0] ?? "").Trim(), $"{r[1]!.Trim()}.{r[2]!.Trim()}", $"{r[3]!.Trim()}.{r[4]!.Trim()}", columns);
        }).ToList();
    }

    public static async Task<List<ForeignKey>> ListForeignKeysAsync(ConnectionProfile profile, string database) =>
        (await ReferencesAsync(profile, database, null))
        .SelectMany(f => f.Columns.Select(c => new ForeignKey(f.Table, c.Column, f.RefTable, c.RefColumn))).ToList();

    /// <summary>Para el autocompletado: (propietario, tabla o vista, columna, tipo) de toda la base.</summary>
    public static async Task<List<string?[]>> GetCompletionRowsAsync(ConnectionProfile profile, string database) =>
        (await Db.QueryAsync(profile, database,
            "select u.name, o.name, c.name, t.name, c.length, c.prec, c.scale, @@ncharsize, @@unicharsize " +
            "from sysobjects o, sysusers u, syscolumns c, systypes t " +
            "where o.type in ('U', 'V') and u.uid = o.uid and c.id = o.id and t.usertype = c.usertype order by u.name, o.name, c.colid"))
        .Select(r => new[] { r[0]!.Trim(), r[1]!.Trim(), r[2]!.Trim(), FormatType(r[3]!.Trim(), Int(r[4]), Int(r[5]), Int(r[6]), Int(r[7]), Int(r[8])) })
        .ToList<string?[]>();

    /// <summary>Texto con el que se creó una vista, procedimiento, función o trigger (syscomments lo guarda en trozos).</summary>
    public static async Task<string> GetDefinitionAsync(ConnectionProfile profile, string database, string name)
    {
        var rows = await Db.QueryAsync(profile, database, $"select text from syscomments where id = {ObjectId(name)} order by colid2, colid");
        string text = string.Concat(rows.Select(r => r[0] ?? "")).Trim();
        return text.Length > 0
            ? text
            : throw new InvalidOperationException($"El servidor no devolvió la definición de {name}: su texto está oculto (sp_hidetext) o faltan permisos.");
    }

    /// <summary>
    /// Script de una tabla reconstruido desde el catálogo: columnas (identidad, valores por defecto), clave primaria,
    /// índices y claves foráneas. No incluye restricciones CHECK, reglas ni columnas calculadas.
    /// </summary>
    public static async Task<string> ScriptTableAsync(ConnectionProfile profile, string database, string table)
    {
        string target = QuoteFull(table);
        var columns = await GetColumnsAsync(profile, database, table);
        if (columns.Count == 0) throw new InvalidOperationException($"No se encontró la tabla {table}.");

        // Valores por defecto declarados en la columna: su texto guardado empieza por DEFAULT.
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultRows = await Db.QueryAsync(profile, database,
            $"select c.name, m.text from syscolumns c, syscomments m where c.id = {ObjectId(table)} and c.cdefault <> 0 and m.id = c.cdefault order by c.colid, m.colid2, m.colid");
        foreach (var r in defaultRows)
            defaults[r[0]!.Trim()] = defaults.GetValueOrDefault(r[0]!.Trim(), "") + (r[1] ?? "");

        var lines = new List<string>();
        foreach (var column in columns)
        {
            var line = new StringBuilder("    ").Append(Quote(column.Name)).Append(' ').Append(column.Type);
            if (defaults.TryGetValue(column.Name, out string? text) && text.TrimStart().StartsWith("DEFAULT", StringComparison.OrdinalIgnoreCase))
                line.Append(' ').Append(Regex.Replace(text.Trim(), @"\s+", " "));
            // Una columna de identidad no lleva NULL / NOT NULL.
            line.Append(column.AutoIncrement ? " IDENTITY" : column.Nullable ? " NULL" : " NOT NULL");
            lines.Add(line.ToString());
        }

        var indexes = await IndexesAsync(profile, database, table);
        var indexScripts = new List<string>();
        foreach (var index in indexes)
        {
            string keys = string.Join(", ", index.Columns.Select(Quote));
            string kind = index.IsClustered ? "CLUSTERED" : "NONCLUSTERED";
            if (index.IsPrimary) lines.Add($"    CONSTRAINT {Quote(index.Name)} PRIMARY KEY {kind} ({keys})");
            else indexScripts.Add($"CREATE {(index.IsUnique ? "UNIQUE " : "")}{kind} INDEX {Quote(index.Name)} ON {target} ({keys})");
        }

        var foreignKeys = (await ReferencesAsync(profile, database, table)).Where(f => f.Columns.Count > 0).Select(f =>
            $"ALTER TABLE {target} ADD {(f.Name.Length > 0 ? $"CONSTRAINT {Quote(f.Name)} " : "")}FOREIGN KEY ({string.Join(", ", f.Columns.Select(c => Quote(c.Column)))}) " +
            $"REFERENCES {QuoteFull(f.RefTable)} ({string.Join(", ", f.Columns.Select(c => Quote(c.RefColumn)))})").ToList();

        var parts = new List<string> { $"CREATE TABLE {target} (\n{string.Join(",\n", lines)}\n)" };
        if (indexScripts.Count > 0) parts.Add(string.Join("\nGO\n", indexScripts));
        if (foreignKeys.Count > 0) parts.Add(string.Join("\nGO\n", foreignKeys));
        return string.Join("\nGO\n\n", parts) + "\nGO";
    }
}
