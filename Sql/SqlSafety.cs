using System.Text.RegularExpressions;

namespace MySmdb;

/// <summary>Detecta sentencias que conviene confirmar antes de ejecutarlas.</summary>
public static class SqlSafety
{
    private static readonly Regex UpdateOrDelete = new(@"^\s*(UPDATE|DELETE)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Where = new(@"\bWHERE\b", RegexOptions.IgnoreCase);
    private static readonly Regex DropOrTruncate = new(@"^\s*(DROP|TRUNCATE)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Write = new(
        @"^\s*(INSERT|REPLACE|UPDATE|DELETE|MERGE|ALTER|CREATE|DROP|TRUNCATE|RENAME|GRANT|REVOKE|LOAD|CALL|EXEC|EXECUTE)\b", RegexOptions.IgnoreCase);

    // Lo que no es código: cadenas, nombres entre comillas y comentarios. Una palabra WHERE ahí dentro no cuenta.
    private static readonly Regex NotCode = new(
        @"'(?:[^'\\]|\\.|'')*'|""(?:[^""\\]|\\.|"""")*""|`[^`]*`|\[[^\]]*\]|--[^\n]*|/\*.*?\*/", RegexOptions.Singleline);

    /// <summary>La sentencia sin cadenas ni comentarios, para buscar palabras clave solo en el código.</summary>
    private static string CodeOnly(string sql) => NotCode.Replace(sql, " ");

    /// <summary>
    /// Avisos, uno por sentencia.
    /// <paramref name="dangerous"/>: UPDATE/DELETE sin WHERE, DROP y TRUNCATE, en cualquier conexión.
    /// <paramref name="productionWrites"/>: en producción, cualquier sentencia que pueda modificar datos o estructura
    /// (incluidos CALL y EXEC: un procedimiento almacenado puede hacer cualquier cosa).
    /// </summary>
    public static List<string> Review(IEnumerable<SqlStatement> statements, int lineOffset, bool production,
        bool dangerous = true, bool productionWrites = true)
    {
        var warnings = new List<string>();
        foreach (var statement in statements)
        {
            string text = statement.Text;
            string code = CodeOnly(text);
            string? reason =
                dangerous && UpdateOrDelete.IsMatch(code) && !Where.IsMatch(code) ? "sin WHERE: afecta a TODAS las filas"
                : dangerous && DropOrTruncate.IsMatch(code) ? "elimina objetos o datos de forma irreversible"
                : production && productionWrites && Write.IsMatch(code) ? "modifica datos o estructura"
                : null;
            if (reason == null) continue;

            string first = text.Split('\n')[0].Trim();
            if (first.Length > 80) first = first[..77] + "...";
            warnings.Add($"• Línea {statement.Line + lineOffset}: {first}\n   ({reason})");
        }
        return warnings;
    }
}
