using System.Text;
using System.Text.RegularExpressions;

namespace MySmdb;

public record SqlStatement(string Text, int Line);

/// <summary>
/// Divide un script en sentencias, respetando cadenas, comentarios y la directiva
/// DELIMITER (que es del cliente, el servidor no la entiende).
/// </summary>
public static class SqlSplitter
{
    private static readonly Regex TriggerStart = new(@"^\s*CREATE\s+(TEMP\s+|TEMPORARY\s+)?TRIGGER\b", RegexOptions.IgnoreCase);
    private static readonly Regex TriggerEnd = new(@";\s*END\s*$", RegexOptions.IgnoreCase);

    /// <param name="mysql">
    /// true: reglas de MySQL (comentarios con '#', DELIMITER, escapes con '\').
    /// false: reglas de SQLite, donde un CREATE TRIGGER lleva ';' dentro y termina en "; END".
    /// </param>
    public static List<SqlStatement> Split(string sql, bool mysql = true)
    {
        var result = new List<SqlStatement>();
        var sb = new StringBuilder();
        string delimiter = ";";
        int i = 0, line = 1, startLine = 1;
        bool hasContent = false;

        void MarkContent()
        {
            if (hasContent) return;
            hasContent = true;
            startLine = line;
        }

        void Flush()
        {
            string text = sb.ToString().Trim();
            if (hasContent && text.Length > 0)
                result.Add(new SqlStatement(text, startLine));
            sb.Clear();
            hasContent = false;
        }

        // Los comentarios anteriores a la sentencia se descartan: no se envían al servidor.
        void Copy(int end)
        {
            for (; i < end; i++)
            {
                if (sql[i] == '\n') line++;
                if (hasContent) sb.Append(sql[i]);
            }
        }

        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (!hasContent)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (c == '\n') line++;
                    i++;
                    continue;
                }
                if (mysql && IsDelimiterDirective(sql, i))
                {
                    int eol = sql.IndexOf('\n', i);
                    if (eol < 0) eol = sql.Length;
                    string value = sql.Substring(i + 9, eol - i - 9).Trim();
                    if (value.Length > 0) delimiter = value;
                    i = eol;
                    sb.Clear();
                    continue;
                }
            }

            // Comentario de línea: "-- ..." y, solo en MySQL, "# ...". MySQL exige un espacio tras "--".
            if ((mysql && c == '#')
                || (c == '-' && next == '-' && (!mysql || i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]))))
            {
                int eol = sql.IndexOf('\n', i);
                Copy(eol < 0 ? sql.Length : eol);
                continue;
            }

            // Comentario de bloque. /*! ... */ es código ejecutable para MySQL.
            if (c == '/' && next == '*')
            {
                if (i + 2 < sql.Length && sql[i + 2] == '!') MarkContent();
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Copy(end < 0 ? sql.Length : end + 2);
                continue;
            }

            if (c == '\'' || c == '"' || c == '`')
            {
                MarkContent();
                int end = i + 1;
                while (end < sql.Length && sql[end] != c)
                {
                    if (mysql && sql[end] == '\\' && c != '`') end++;
                    end++;
                }
                Copy(Math.Min(end + 1, sql.Length));
                continue;
            }

            if (string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0)
            {
                if (!mysql && hasContent && IsOpenTrigger(sb.ToString()))
                {
                    Copy(i + 1);
                    continue;
                }
                Flush();
                i += delimiter.Length;
                continue;
            }

            MarkContent();
            Copy(i + 1);
        }

        Flush();
        return result;
    }

    private static bool IsOpenTrigger(string statement) =>
        TriggerStart.IsMatch(statement) && !TriggerEnd.IsMatch(statement);

    private static bool IsDelimiterDirective(string sql, int index)
    {
        const string word = "DELIMITER";
        if (string.Compare(sql, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        int after = index + word.Length;
        return after < sql.Length && (sql[after] == ' ' || sql[after] == '\t');
    }
}
