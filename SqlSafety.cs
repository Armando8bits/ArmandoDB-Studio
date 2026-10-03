using System.Text.RegularExpressions;

namespace MySmdb;

/// <summary>Detecta sentencias que conviene confirmar antes de ejecutarlas.</summary>
public static class SqlSafety
{
    private static readonly Regex UpdateOrDelete = new(@"^\s*(UPDATE|DELETE)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Where = new(@"\bWHERE\b", RegexOptions.IgnoreCase);
    private static readonly Regex DropOrTruncate = new(@"^\s*(DROP|TRUNCATE)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Write = new(
        @"^\s*(INSERT|REPLACE|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE|RENAME|GRANT|REVOKE|LOAD|CALL)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Avisos, uno por sentencia.
    /// <paramref name="dangerous"/>: UPDATE/DELETE sin WHERE, DROP y TRUNCATE, en cualquier conexión.
    /// <paramref name="productionWrites"/>: en producción, cualquier sentencia que pueda modificar datos o estructura (incluido CALL).
    /// </summary>
    public static List<string> Review(IEnumerable<SqlStatement> statements, int lineOffset, bool production,
        bool dangerous = true, bool productionWrites = true)
    {
        var warnings = new List<string>();
        foreach (var statement in statements)
        {
            string text = statement.Text;
            string? reason =
                dangerous && UpdateOrDelete.IsMatch(text) && !Where.IsMatch(text) ? "sin WHERE: afecta a TODAS las filas"
                : dangerous && DropOrTruncate.IsMatch(text) ? "elimina objetos o datos de forma irreversible"
                : production && productionWrites && Write.IsMatch(text) ? "modifica datos o estructura"
                : null;
            if (reason == null) continue;

            string first = text.Split('\n')[0].Trim();
            if (first.Length > 80) first = first[..77] + "...";
            warnings.Add($"• Línea {statement.Line + lineOffset}: {first}\n   ({reason})");
        }
        return warnings;
    }
}
