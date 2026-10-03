namespace MySmdb;

/// <summary>
/// Fragmentos de código: se escribe la abreviatura y se pulsa Tab. "|" marca dónde queda el cursor.
/// </summary>
public static class Snippets
{
    public static readonly IReadOnlyList<(string Key, string Template)> All = new[]
    {
        ("sel", "SELECT *\nFROM |\nLIMIT 100;"),
        ("selw", "SELECT *\nFROM |\nWHERE ;"),
        ("cnt", "SELECT COUNT(*)\nFROM |;"),
        ("dist", "SELECT DISTINCT |\nFROM ;"),
        ("ins", "INSERT INTO | ()\nVALUES ();"),
        ("upd", "UPDATE |\nSET \nWHERE ;"),
        ("del", "DELETE FROM |\nWHERE ;"),
        ("ij", "INNER JOIN | ON "),
        ("lj", "LEFT JOIN | ON "),
        ("gb", "GROUP BY |"),
        ("ob", "ORDER BY | DESC"),
        ("ct", "CREATE TABLE | (\n    id INT PRIMARY KEY AUTO_INCREMENT\n);"),
    };

    /// <summary>Plantilla de la abreviatura, adaptada al motor; null si no existe.</summary>
    public static string? Get(string key, DbKind kind)
    {
        foreach (var (k, template) in All)
        {
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return kind == DbKind.Sqlite
                ? template.Replace("INT PRIMARY KEY AUTO_INCREMENT", "INTEGER PRIMARY KEY AUTOINCREMENT")
                : template;
        }
        return null;
    }

    /// <summary>Lista legible para la ayuda.</summary>
    public static string Describe() => string.Join("\n",
        All.Select(s => $"{s.Key,-6} →  {s.Template.Replace("|", "").Replace("\n", " ")}"));
}
