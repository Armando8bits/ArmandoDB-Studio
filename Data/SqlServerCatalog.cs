using System.Text;

namespace MySmdb;

/// <summary>Las tres partes del script de una tabla de SQL Server; la copia de seguridad las escribe en momentos distintos.</summary>
public sealed record SqlServerTableScript(string CreateTable, IReadOnlyList<string> Indexes, IReadOnlyList<string> ForeignKeys);

/// <summary>
/// Consultas del explorador para SQL Server, sobre las vistas de catálogo (sys.*).
/// Los objetos se nombran siempre "esquema.nombre" (dbo.Clientes), como en SSMS: el esquema forma parte del nombre.
/// </summary>
public static class SqlServerCatalog
{
    /// <summary>Tipo de la columna "c" tal como se escribe en un CREATE TABLE: nvarchar(50), decimal(10,2), varchar(max)...</summary>
    private const string TypeExpression =
        "CASE WHEN t.name IN ('varchar','char','varbinary','binary') THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')' " +
        "WHEN t.name IN ('nvarchar','nchar') THEN t.name + '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')' " +
        "WHEN t.name IN ('decimal','numeric') THEN t.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')' " +
        "WHEN t.name IN ('datetime2','time','datetimeoffset') THEN t.name + '(' + CAST(c.scale AS varchar(10)) + ')' " +
        "ELSE t.name END";

    /// <summary>1 si la columna "c" forma parte de la clave primaria de su tabla.</summary>
    private const string IsPrimaryKey =
        "CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
        "WHERE i.is_primary_key = 1 AND i.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END";

    /// <summary>Lista separada por comas, compatible con versiones sin STRING_AGG (anteriores a 2017).</summary>
    private static string Concat(string select) =>
        $"STUFF(({select} FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '')";

    /// <summary>"dbo.Clientes" → ("dbo", "Clientes"). Sin esquema, se asume dbo.</summary>
    public static (string Schema, string Name) Split(string name)
    {
        int dot = name.IndexOf('.');
        return dot <= 0 ? ("dbo", name) : (name[..dot], name[(dot + 1)..]);
    }

    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>"dbo.Clientes" → "[dbo].[Clientes]".</summary>
    public static string QuoteFull(string name)
    {
        var (schema, obj) = Split(name);
        return $"{Quote(schema)}.{Quote(obj)}";
    }

    private static ColumnInfo Column(string?[] r, int offset) =>
        new(r[offset]!, r[offset + 1] ?? "", r[offset + 3] == "1", r[offset + 2] == "True" || r[offset + 2] == "1", r[offset + 4] == "True" || r[offset + 4] == "1");

    /// <summary>Las bases del usuario primero; las del sistema (master, model, msdb, tempdb), al final.</summary>
    public static async Task<List<string>> ListDatabasesAsync(ConnectionProfile profile) =>
        (await Db.QueryAsync(profile, null,
            "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY CASE WHEN database_id <= 4 THEN 1 ELSE 0 END, name"))
        .Select(r => r[0]!).ToList();

    public static async Task<List<(string Name, bool IsView)>> ListTablesAsync(ConnectionProfile profile, string database) =>
        (await Db.QueryAsync(profile, database,
            "SELECT s.name + '.' + o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            "WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name"))
        .Select(r => (r[0]!, r[1]!.Trim() == "V")).ToList();

    public static async Task<List<ColumnInfo>> GetColumnsAsync(ConnectionProfile profile, string database, string table) =>
        (await Db.QueryAsync(profile, database,
            $"SELECT c.name, {TypeExpression}, c.is_nullable, {IsPrimaryKey}, c.is_identity " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE c.object_id = OBJECT_ID(@p0) ORDER BY c.column_id", QuoteFull(table)))
        .Select(r => Column(r, 0)).ToList();

    public static async Task<List<(string Name, bool IsFunction)>> ListRoutinesAsync(ConnectionProfile profile, string database) =>
        (await Db.QueryAsync(profile, database,
            "SELECT s.name + '.' + o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            "WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name"))
        .Select(r => (r[0]!, r[1]!.Trim() != "P")).ToList();

    public static async Task<List<(string Name, string Table)>> ListTriggersAsync(ConnectionProfile profile, string database) =>
        (await Db.QueryAsync(profile, database,
            "SELECT s.name + '.' + tr.name, s.name + '.' + o.name FROM sys.triggers tr JOIN sys.objects o ON o.object_id = tr.parent_id " +
            "JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE tr.parent_class = 1 AND tr.is_ms_shipped = 0 ORDER BY s.name, tr.name"))
        .Select(r => (r[0]!, r[1] ?? "")).ToList();

    public static async Task<List<IndexInfo>> ListIndexesAsync(ConnectionProfile profile, string database, string table) =>
        (await Db.QueryAsync(profile, database,
            "SELECT i.name, i.is_unique, i.is_primary_key, " +
            Concat("SELECT ', ' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                   "WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 ORDER BY ic.key_ordinal") +
            " FROM sys.indexes i WHERE i.object_id = OBJECT_ID(@p0) AND i.type > 0 AND i.is_hypothetical = 0 " +
            "ORDER BY i.is_primary_key DESC, i.name", QuoteFull(table)))
        .Select(r => new IndexInfo(r[0]!, r[1] == "True", r[2] == "True", r[3] ?? "")).ToList();

    public static async Task<List<(string Table, List<ColumnInfo> Columns)>> GetAllTablesAsync(ConnectionProfile profile, string database)
    {
        var tables = new List<(string Table, List<ColumnInfo> Columns)>();
        var rows = await Db.QueryAsync(profile, database,
            $"SELECT s.name + '.' + o.name, c.name, {TypeExpression}, c.is_nullable, {IsPrimaryKey}, c.is_identity " +
            "FROM sys.tables o JOIN sys.schemas s ON s.schema_id = o.schema_id JOIN sys.columns c ON c.object_id = o.object_id " +
            "JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE o.is_ms_shipped = 0 ORDER BY s.name, o.name, c.column_id");
        foreach (var r in rows)
        {
            if (tables.Count == 0 || tables[^1].Table != r[0]) tables.Add((r[0]!, new List<ColumnInfo>()));
            tables[^1].Columns.Add(Column(r, 1));
        }
        return tables;
    }

    public static async Task<List<ForeignKey>> ListForeignKeysAsync(ConnectionProfile profile, string database) =>
        (await Db.QueryAsync(profile, database,
            "SELECT ps.name + '.' + po.name, pc.name, rs.name + '.' + ro.name, rc.name FROM sys.foreign_key_columns k " +
            "JOIN sys.objects po ON po.object_id = k.parent_object_id JOIN sys.schemas ps ON ps.schema_id = po.schema_id " +
            "JOIN sys.columns pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id " +
            "JOIN sys.objects ro ON ro.object_id = k.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = ro.schema_id " +
            "JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id " +
            "ORDER BY ps.name, po.name, k.constraint_object_id, k.constraint_column_id"))
        .Select(r => new ForeignKey(r[0]!, r[1]!, r[2]!, r[3])).ToList();

    /// <summary>Para el autocompletado: (esquema, tabla o vista, columna, tipo) de toda la base, en una consulta.</summary>
    public static Task<List<string?[]>> GetCompletionRowsAsync(ConnectionProfile profile, string database) =>
        Db.QueryAsync(profile, database,
            $"SELECT s.name, o.name, c.name, {TypeExpression} FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            "JOIN sys.columns c ON c.object_id = o.object_id JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name, c.column_id");

    /// <summary>Texto con el que se creó una vista, procedimiento, función o trigger.</summary>
    public static async Task<string> GetDefinitionAsync(ConnectionProfile profile, string database, string name)
    {
        var rows = await Db.QueryAsync(profile, database, "SELECT OBJECT_DEFINITION(OBJECT_ID(@p0))", QuoteFull(name));
        return rows.Count > 0 && rows[0][0] != null
            ? rows[0][0]!.Trim()
            : throw new InvalidOperationException($"El servidor no devolvió la definición de {name}: está cifrada o faltan permisos (VIEW DEFINITION).");
    }

    /// <summary>
    /// Script de una tabla reconstruido desde el catálogo: columnas (identidad, valores por defecto, calculadas),
    /// clave primaria, restricciones UNIQUE y CHECK; aparte, sus índices y sus claves foráneas.
    /// </summary>
    public static async Task<SqlServerTableScript> ScriptTableAsync(ConnectionProfile profile, string database, string table)
    {
        string target = QuoteFull(table), id = target;

        var columns = await Db.QueryAsync(profile, database,
            $"SELECT c.name, {TypeExpression}, c.is_nullable, c.is_identity, CAST(ic.seed_value AS varchar(40)), CAST(ic.increment_value AS varchar(40)), " +
            "dc.name, dc.definition, cc.definition, cc.is_persisted, t.is_user_defined, st.name, c.is_rowguidcol " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id JOIN sys.schemas st ON st.schema_id = t.schema_id " +
            "LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id " +
            "LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id " +
            "LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id " +
            "WHERE c.object_id = OBJECT_ID(@p0) ORDER BY c.column_id", id);
        if (columns.Count == 0) throw new InvalidOperationException($"No se encontró la tabla {table}.");

        var lines = new List<string>();
        foreach (var c in columns)
        {
            var line = new StringBuilder("    ").Append(Quote(c[0]!)).Append(' ');
            if (c[8] != null)
            {
                line.Append("AS ").Append(c[8]).Append(c[9] == "True" ? " PERSISTED" : "");
            }
            else
            {
                // Los tipos definidos por el usuario llevan su esquema.
                line.Append(c[10] == "True" ? $"{Quote(c[11]!)}.{Quote(c[1]!)}" : c[1]);
                if (c[3] == "True") line.Append($" IDENTITY({c[4] ?? "1"},{c[5] ?? "1"})");
                if (c[12] == "True") line.Append(" ROWGUIDCOL");
                line.Append(c[2] == "True" ? " NULL" : " NOT NULL");
                if (c[7] != null) line.Append($" CONSTRAINT {Quote(c[6]!)} DEFAULT {c[7]}");
            }
            lines.Add(line.ToString());
        }

        // Clave primaria y UNIQUE (restricciones) dentro del CREATE TABLE; el resto de índices, aparte.
        string keyColumns(string includedFilter) => Concat(
            "SELECT ', ' + QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END " +
            "FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
            $"WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = {includedFilter} " +
            "ORDER BY CASE WHEN ic.is_included_column = 1 THEN ic.index_column_id ELSE ic.key_ordinal END");
        var indexes = await Db.QueryAsync(profile, database,
            $"SELECT i.name, i.is_primary_key, i.is_unique_constraint, i.is_unique, i.type_desc, {keyColumns("0")}, {keyColumns("1")}, i.filter_definition " +
            "FROM sys.indexes i WHERE i.object_id = OBJECT_ID(@p0) AND i.type IN (1, 2) AND i.is_hypothetical = 0 " +
            "ORDER BY i.is_primary_key DESC, i.is_unique_constraint DESC, i.name", id);

        var indexScripts = new List<string>();
        foreach (var i in indexes)
        {
            string kind = i[4] == "CLUSTERED" ? "CLUSTERED" : "NONCLUSTERED";
            if (i[1] == "True") lines.Add($"    CONSTRAINT {Quote(i[0]!)} PRIMARY KEY {kind} ({i[5]})");
            else if (i[2] == "True") lines.Add($"    CONSTRAINT {Quote(i[0]!)} UNIQUE {kind} ({i[5]})");
            else indexScripts.Add($"CREATE {(i[3] == "True" ? "UNIQUE " : "")}{kind} INDEX {Quote(i[0]!)} ON {target} ({i[5]})"
                + (i[6] != null ? $" INCLUDE ({i[6]})" : "") + (i[7] != null ? $" WHERE {i[7]}" : "") + ";");
        }

        var checks = await Db.QueryAsync(profile, database,
            "SELECT name, definition FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(@p0) ORDER BY name", id);
        lines.AddRange(checks.Select(k => $"    CONSTRAINT {Quote(k[0]!)} CHECK {k[1]}"));

        string fkColumns(string objectColumn, string columnColumn) => Concat(
            $"SELECT ', ' + QUOTENAME(c.name) FROM sys.foreign_key_columns k JOIN sys.columns c ON c.object_id = k.{objectColumn} AND c.column_id = k.{columnColumn} " +
            "WHERE k.constraint_object_id = f.object_id ORDER BY k.constraint_column_id");
        var foreignKeys = await Db.QueryAsync(profile, database,
            $"SELECT f.name, {fkColumns("parent_object_id", "parent_column_id")}, QUOTENAME(rs.name) + '.' + QUOTENAME(ro.name), " +
            $"{fkColumns("referenced_object_id", "referenced_column_id")}, f.delete_referential_action_desc, f.update_referential_action_desc " +
            "FROM sys.foreign_keys f JOIN sys.objects ro ON ro.object_id = f.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = ro.schema_id " +
            "WHERE f.parent_object_id = OBJECT_ID(@p0) ORDER BY f.name", id);
        string action(string? description) => (description ?? "NO_ACTION").Replace('_', ' ');
        var foreignKeyScripts = foreignKeys.Select(f =>
            $"ALTER TABLE {target} ADD CONSTRAINT {Quote(f[0]!)} FOREIGN KEY ({f[1]}) REFERENCES {f[2]} ({f[3]})"
            + (f[4] != "NO_ACTION" ? $" ON DELETE {action(f[4])}" : "") + (f[5] != "NO_ACTION" ? $" ON UPDATE {action(f[5])}" : "") + ";").ToList();

        return new SqlServerTableScript($"CREATE TABLE {target} (\n{string.Join(",\n", lines)}\n);", indexScripts, foreignKeyScripts);
    }

    /// <summary>Script completo de una tabla, con GO entre partes, listo para ejecutar.</summary>
    public static async Task<string> ScriptTableTextAsync(ConnectionProfile profile, string database, string table)
    {
        var script = await ScriptTableAsync(profile, database, table);
        var parts = new List<string> { script.CreateTable };
        if (script.Indexes.Count > 0) parts.Add(string.Join("\n", script.Indexes));
        if (script.ForeignKeys.Count > 0) parts.Add(string.Join("\n", script.ForeignKeys));
        return string.Join("\nGO\n\n", parts) + "\nGO";
    }
}
