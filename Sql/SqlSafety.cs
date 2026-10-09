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

    private static readonly Regex With = new(@"^\s*WITH\b", RegexOptions.IgnoreCase);
    // Una expresión de tabla común hasta su paréntesis de apertura: nombre [(columnas)] AS [[NOT] MATERIALIZED] (
    // El nombre puede faltar: si iba entre comillas o corchetes, CodeOnly ya lo quitó.
    private static readonly Regex CteHead = new(
        @"\G\s*(RECURSIVE\b\s*)?[^\s(,]*\s*(\([^()]*\))?\s*AS\b\s*((NOT\s+)?MATERIALIZED\b\s*)?\(", RegexOptions.IgnoreCase);

    /// <summary>
    /// La sentencia que de verdad se ejecuta cuando va precedida de expresiones de tabla comunes:
    /// de "WITH c AS (SELECT ...) DELETE FROM t" devuelve "DELETE FROM t". Lo que hay dentro de las CTE (incluido
    /// su WHERE) no dice nada de la sentencia principal. Si no empieza por WITH o no se entiende, devuelve lo mismo.
    /// </summary>
    private static string MainStatement(string code)
    {
        var with = With.Match(code);
        if (!with.Success) return code;

        int position = with.Length;
        while (true)
        {
            var head = CteHead.Match(code, position);
            if (!head.Success) return code;

            // Hasta el paréntesis que cierra la consulta de la CTE.
            int depth = 1;
            position = head.Index + head.Length;
            while (position < code.Length && depth > 0)
            {
                if (code[position] == '(') depth++;
                else if (code[position] == ')') depth--;
                position++;
            }
            if (depth > 0) return code;

            while (position < code.Length && char.IsWhiteSpace(code[position])) position++;
            if (position < code.Length && code[position] == ',') { position++; continue; }
            return code[position..];
        }
    }

    // ---------- Solo lectura (modo de línea de comandos) ----------

    // Con qué puede empezar una sentencia que solo lee. Lista cerrada: lo que no está aquí, se rechaza.
    private static readonly Regex ReadStart = new(@"^\s*(SELECT|SHOW|DESCRIBE|DESC|EXPLAIN|USE|VALUES)\b", RegexOptions.IgnoreCase);
    // En Transact-SQL, además, lo que solo afecta a la sesión o controla el flujo del lote.
    private static readonly Regex TSqlReadStart = new(@"^\s*(SELECT|WITH|USE|DECLARE|SET|PRINT|IF|WHILE|BEGIN|RETURN)\b", RegexOptions.IgnoreCase);
    // SHOW CREATE TABLE y similares mencionan palabras de escritura sin escribir nada.
    private static readonly Regex Describes = new(@"^\s*(SHOW|DESCRIBE|DESC)\b", RegexOptions.IgnoreCase);
    // Palabras que delatan una escritura en cualquier punto: "IF ... DELETE", "SELECT ... INTO tabla", bloqueos de filas.
    private static readonly Regex WriteAnywhere = new(
        @"\b(INSERT|UPDATE|DELETE|MERGE|REPLACE\s+INTO|EXEC|EXECUTE|CALL|DROP|TRUNCATE|ALTER|CREATE|RENAME|GRANT|REVOKE|INTO|" +
        @"BACKUP|RESTORE|KILL|SHUTDOWN|DBCC|WRITETEXT|UPDATETEXT|LOAD\s+DATA|LOCK\s+(TABLES?|IN)|HANDLER)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Sentencias que no son claramente de solo lectura, una línea por cada una; vacío si todo el script solo lee.
    /// Al revés que <see cref="Review"/>, aquí se admite solo lo conocido (SELECT, SHOW, EXPLAIN...) y se rechaza
    /// el resto. Es una red de seguridad, no una garantía: una función llamada desde un SELECT puede escribir.
    /// La protección real es que la cuenta de la base de datos solo tenga permiso de lectura.
    /// </summary>
    public static List<string> ReadOnlyViolations(IEnumerable<SqlStatement> statements, DbKind kind)
    {
        bool tsql = Db.IsTSql(kind);
        var violations = new List<string>();
        foreach (var statement in statements)
        {
            string code = CodeOnly(statement.Text);
            string main = MainStatement(code);
            bool reads = (tsql ? TSqlReadStart : ReadStart).IsMatch(main)
                && (Describes.IsMatch(main) || !WriteAnywhere.IsMatch(code));
            if (reads) continue;

            string first = statement.Text.Trim().Split('\n')[0].Trim();
            if (first.Length > 80) first = first[..77] + "...";
            violations.Add($"Línea {statement.Line}: {first}");
        }
        return violations;
    }

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
            string code = MainStatement(CodeOnly(text));
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
