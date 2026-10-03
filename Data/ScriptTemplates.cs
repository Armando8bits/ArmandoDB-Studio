namespace MySmdb;

/// <summary>
/// Plantillas de "Generar script como" del explorador, al estilo de SSMS. Los valores a completar
/// van como &lt;columna: tipo&gt;, que no es SQL válido: así no se ejecutan por accidente.
/// </summary>
public static class ScriptTemplates
{
    private static string Q(string name) => Db.QuoteId(name);
    private static string Placeholder(ColumnInfo column) => $"<{column.Name}: {(column.Type.Length == 0 ? "valor" : column.Type)}>";

    public static string Select(string fullName, IReadOnlyList<ColumnInfo> columns) =>
        $"SELECT {string.Join(",\n       ", columns.Select(c => Q(c.Name)))}\nFROM {fullName}\nLIMIT 1000;\n";

    /// <summary>Sin las columnas autonuméricas, que las asigna la base de datos.</summary>
    public static string Insert(string fullName, IReadOnlyList<ColumnInfo> columns)
    {
        var values = columns.Where(c => !c.AutoIncrement).ToList();
        if (values.Count == 0) values = columns.ToList();
        return $"INSERT INTO {fullName}\n    ({string.Join(", ", values.Select(c => Q(c.Name)))})\nVALUES\n    ({string.Join(", ", values.Select(Placeholder))});\n";
    }

    /// <summary>SET con las columnas que no son clave; WHERE por la clave primaria (o una condición a completar).</summary>
    public static string Update(string fullName, IReadOnlyList<ColumnInfo> columns)
    {
        var set = columns.Where(c => !c.PrimaryKey).ToList();
        if (set.Count == 0) set = columns.ToList();
        return $"UPDATE {fullName}\nSET {string.Join(",\n    ", set.Select(c => $"{Q(c.Name)} = {Placeholder(c)}"))}\nWHERE {Where(columns)};\n";
    }

    public static string Delete(string fullName, IReadOnlyList<ColumnInfo> columns) =>
        $"DELETE FROM {fullName}\nWHERE {Where(columns)};\n";

    private static string Where(IReadOnlyList<ColumnInfo> columns)
    {
        var keys = columns.Where(c => c.PrimaryKey).ToList();
        return keys.Count == 0 ? "<condición>" : string.Join("\n  AND ", keys.Select(c => $"{Q(c.Name)} = {Placeholder(c)}"));
    }
}
