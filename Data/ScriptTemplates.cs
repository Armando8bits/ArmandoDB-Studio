namespace MySmdb;

/// <summary>
/// Plantillas de "Generar script como" del explorador, al estilo de SSMS. Los valores a completar
/// van como &lt;columna: tipo&gt;, que no es SQL válido: así no se ejecutan por accidente.
/// </summary>
public static class ScriptTemplates
{
    private static string Placeholder(ColumnInfo column) => $"<{column.Name}: {(column.Type.Length == 0 ? "valor" : column.Type)}>";

    /// <summary>Las primeras 1000 filas: TOP en SQL Server, LIMIT en MySQL y SQLite.</summary>
    public static string SelectTop(DbKind kind, string fullName, string columns = "*") => Db.IsTSql(kind)
        ? $"SELECT TOP 1000 {columns}\nFROM {fullName};\n"
        : $"SELECT {columns}\nFROM {fullName}\nLIMIT 1000;\n";

    public static string Select(DbKind kind, string fullName, IReadOnlyList<ColumnInfo> columns) =>
        SelectTop(kind, fullName, string.Join(",\n       ", columns.Select(c => Db.QuoteId(kind, c.Name))));

    /// <summary>Sin las columnas autonuméricas, que las asigna la base de datos.</summary>
    public static string Insert(DbKind kind, string fullName, IReadOnlyList<ColumnInfo> columns)
    {
        var values = columns.Where(c => !c.AutoIncrement).ToList();
        if (values.Count == 0) values = columns.ToList();
        return $"INSERT INTO {fullName}\n    ({string.Join(", ", values.Select(c => Db.QuoteId(kind, c.Name)))})\nVALUES\n    ({string.Join(", ", values.Select(Placeholder))});\n";
    }

    /// <summary>SET con las columnas que no son clave; WHERE por la clave primaria (o una condición a completar).</summary>
    public static string Update(DbKind kind, string fullName, IReadOnlyList<ColumnInfo> columns)
    {
        var set = columns.Where(c => !c.PrimaryKey).ToList();
        if (set.Count == 0) set = columns.ToList();
        return $"UPDATE {fullName}\nSET {string.Join(",\n    ", set.Select(c => $"{Db.QuoteId(kind, c.Name)} = {Placeholder(c)}"))}\nWHERE {Where(kind, columns)};\n";
    }

    public static string Delete(DbKind kind, string fullName, IReadOnlyList<ColumnInfo> columns) =>
        $"DELETE FROM {fullName}\nWHERE {Where(kind, columns)};\n";

    private static string Where(DbKind kind, IReadOnlyList<ColumnInfo> columns)
    {
        var keys = columns.Where(c => c.PrimaryKey).ToList();
        return keys.Count == 0 ? "<condición>" : string.Join("\n  AND ", keys.Select(c => $"{Db.QuoteId(kind, c.Name)} = {Placeholder(c)}"));
    }
}
