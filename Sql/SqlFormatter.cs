using System.Text;
using System.Text.RegularExpressions;

namespace MySmdb;

/// <summary>
/// Formateador de SQL: palabras clave en mayúsculas, una cláusula por línea (SELECT, FROM, WHERE, JOIN...),
/// columnas del SELECT y del SET una por línea, AND/OR del WHERE en líneas propias y subconsultas sangradas.
/// Solo cambia espacios y saltos de línea (y mayúsculas de palabras clave): cadenas, identificadores
/// entre comillas y comentarios se conservan tal cual.
/// </summary>
public static class SqlFormatter
{
    private const string Indent = "    ";

    private enum Kind { Word, Quoted, String, Number, LineComment, BlockComment, Symbol }

    private sealed record Token(Kind Kind, string Text)
    {
        public bool Is(string word) => Kind == Kind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex DelimiterDirective = new(@"^\s*(DELIMITER\s|GO\s*$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Los scripts con DELIMITER (procedimientos, triggers de MySQL) o con lotes GO (SQL Server) no se formatean.</summary>
    public static bool CanFormat(string sql) => !DelimiterDirective.IsMatch(sql);

    // ---------- Separación en piezas ----------

    private static List<Token> Tokenize(string sql, bool mysql)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            int start = i;

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if ((mysql && c == '#') || (c == '-' && next == '-' && (!mysql || i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]))))
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                tokens.Add(new Token(Kind.LineComment, sql[start..i].TrimEnd()));
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                tokens.Add(new Token(Kind.BlockComment, sql[start..i]));
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                i++;
                while (i < sql.Length)
                {
                    if (mysql && sql[i] == '\\' && c != '`') { i += 2; continue; }
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) { i += 2; continue; }   // comilla duplicada
                        i++;
                        break;
                    }
                    i++;
                }
                i = Math.Min(i, sql.Length);
                tokens.Add(new Token(c == '\'' ? Kind.String : Kind.Quoted, sql[start..i]));
                continue;
            }
            // [identificador] de SQL Server (SQLite también lo admite); "]]" es un corchete dentro del nombre.
            if (c == '[' && !mysql)
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == ']')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == ']') { i += 2; continue; }
                        i++;
                        break;
                    }
                    if (sql[i] == '\n') break;   // un corchete suelto no se traga el resto del script
                    i++;
                }
                tokens.Add(new Token(Kind.Quoted, sql[start..i]));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.' ||
                       ((sql[i] == '+' || sql[i] == '-') && (sql[i - 1] == 'e' || sql[i - 1] == 'E')))) i++;
                tokens.Add(new Token(Kind.Number, sql[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '@' or '$')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '$')) i++;
                tokens.Add(new Token(Kind.Word, sql[start..i]));
                continue;
            }

            foreach (string op in new[] { "<=>", "->>", "<=", ">=", "<>", "!=", ":=", "||", "&&", "->", "<<", ">>" })
            {
                if (string.CompareOrdinal(sql, i, op, 0, op.Length) == 0) { i += op.Length; break; }
            }
            if (i == start) i++;
            tokens.Add(new Token(Kind.Symbol, sql[start..i]));
        }
        return tokens;
    }

    // ---------- Formato ----------

    private sealed class Frame
    {
        public int Indent;              // sangría de las cláusulas de este nivel
        public string Clause = "";      // última cláusula vista en este nivel (SELECT, WHERE...)
        public bool Subquery;           // paréntesis de una subconsulta
        public bool DefinitionList;     // paréntesis de CREATE TABLE (...): un elemento por línea
        public int OpenLineIndent;      // sangría de la línea donde se abrió
    }

    public static string Format(string sql, bool mysql)
    {
        var tokens = Tokenize(sql, mysql);
        var output = new StringBuilder();
        bool lineStart = true;
        int lineIndent = 0;
        var frames = new Stack<Frame>();
        frames.Push(new Frame());
        Token? previous = null;
        bool pendingListItem = false;   // tras SELECT/SET: el primer elemento va en una línea nueva
        bool statementIsCreate = false;

        // Empieza una línea nueva con la sangría indicada (si ya se está al inicio de una, solo ajusta la sangría).
        void NewLine(int indent)
        {
            if (!lineStart)
            {
                while (output.Length > 0 && output[^1] == ' ') output.Length--;   // sin espacios al final
                output.Append('\n');
                lineStart = true;
            }
            lineIndent = indent;
        }

        void Write(string text, bool space)
        {
            if (lineStart)
            {
                for (int n = 0; n < lineIndent; n++) output.Append(Indent);
                lineStart = false;
            }
            else if (space)
            {
                output.Append(' ');
            }
            output.Append(text);
        }

        bool IsClauseKeyword(Token t) => t.Kind == Kind.Word && (t.Is("SELECT") || t.Is("FROM") || t.Is("WHERE") ||
            t.Is("HAVING") || t.Is("LIMIT") || t.Is("UNION") || t.Is("VALUES") || t.Is("SET") || t.Is("UPDATE") ||
            t.Is("INSERT") || t.Is("DELETE") || t.Is("WITH") || t.Is("GROUP") || t.Is("ORDER") || t.Is("JOIN") ||
            t.Is("INNER") || t.Is("LEFT") || t.Is("RIGHT") || t.Is("CROSS") || t.Is("FULL") || t.Is("NATURAL") || t.Is("REPLACE"));

        for (int index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var frame = frames.Peek();
            Token? nextToken = index + 1 < tokens.Count ? tokens[index + 1] : null;
            bool afterDot = previous is { Kind: Kind.Symbol, Text: "." };

            // Comentarios: se respetan; el de línea termina la línea.
            if (token.Kind == Kind.LineComment)
            {
                Write(token.Text, space: true);
                NewLine(frame.Indent + (pendingListItem || frame.Clause is "SELECT" or "SET" ? 1 : 0));
                previous = token;
                continue;
            }
            if (token.Kind == Kind.BlockComment)
            {
                Write(token.Text, space: !lineStart);
                previous = token;
                continue;
            }

            // Primer elemento de una lista de varios (SELECT a, b / SET / VALUES): en línea propia, sangrado.
            if (pendingListItem && !(token.Is("DISTINCT") || token.Is("ALL")))
            {
                NewLine(frame.Indent + 1);
                pendingListItem = false;
            }

            if (token.Kind == Kind.Symbol)
            {
                switch (token.Text)
                {
                    case ";":
                        Write(";", space: false);
                        NewLine(0);
                        output.Append('\n');   // línea en blanco entre sentencias
                        frames.Clear();
                        frames.Push(new Frame());
                        pendingListItem = false;
                        statementIsCreate = false;
                        previous = null;
                        continue;

                    case ",":
                        Write(",", space: false);
                        if (frame.DefinitionList || (frames.Count == 1 || frame.Subquery) && frame.Clause is "SELECT" or "SET" or "VALUES")
                            NewLine(frame.Indent + (frame.DefinitionList ? 0 : 1));
                        previous = token;
                        continue;

                    case "(":
                    {
                        bool subquery = nextToken is { } n && (n.Is("SELECT") || n.Is("WITH"));
                        bool definitions = !subquery && statementIsCreate && frames.Count == 1
                            && previous is { Kind: Kind.Word or Kind.Quoted } && !SqlKeywords.IsKeyword(previous.Text);
                        // Pegado a funciones y tipos (COUNT(*), VARCHAR(50)); separado tras palabras clave (IN (...)),
                        // tras la tabla de un INSERT y en CREATE TABLE t (...).
                        bool attached = previous is { Kind: Kind.Word or Kind.Quoted }
                            && (!SqlKeywords.Keywords.Contains(previous.Text) || SqlKeywords.Functions.Contains(previous.Text)
                                || SqlKeywords.SizedTypes.Contains(previous.Text)
                                || previous.Is("VALUES") && frame.Clause == "SET")   // ON DUPLICATE KEY UPDATE b = VALUES(b)
                            && frame.Clause != "INSERT";
                        bool space = definitions || previous is not null && !attached && previous is not { Kind: Kind.Symbol, Text: "(" };
                        Write("(", space);
                        var inner = new Frame
                        {
                            Subquery = subquery,
                            DefinitionList = definitions,
                            OpenLineIndent = lineIndent,
                            Indent = subquery || definitions ? lineIndent + 1 : frame.Indent,
                        };
                        frames.Push(inner);
                        if (definitions) NewLine(inner.Indent);
                        previous = token;
                        continue;
                    }

                    case ")":
                        if (frames.Count > 1)
                        {
                            var closing = frames.Pop();
                            if (closing.Subquery || closing.DefinitionList) NewLine(closing.OpenLineIndent);
                        }
                        Write(")", space: false);
                        pendingListItem = false;
                        previous = token;
                        continue;

                    case ".":
                        Write(".", space: false);
                        previous = token;
                        continue;
                }

                // Operadores. El signo de un número negativo va pegado: = -1, (-1).
                bool unary = token.Text is "-" or "+" && (previous is null || previous.Kind == Kind.Symbol && previous.Text != ")"
                    || previous.Kind == Kind.Word && SqlKeywords.Keywords.Contains(previous.Text));
                Write(token.Text, space: !(previous is { Kind: Kind.Symbol, Text: "(" }));
                if (unary) { previous = new Token(Kind.Symbol, "unary"); continue; }
                previous = token;
                continue;
            }

            // Palabras, identificadores, cadenas y números.
            string text = token.Kind == Kind.Word && !afterDot && SqlKeywords.IsKeyword(token.Text) && nextToken is not { Kind: Kind.Symbol, Text: "." }
                ? token.Text.ToUpperInvariant()
                : token.Text;

            if (token.Kind == Kind.Word && !afterDot && previous == null && token.Is("CREATE")) statementIsCreate = true;

            // INSERT ... ON DUPLICATE KEY UPDATE a = 1, b = 2: línea propia, y su lista como la de un SET.
            if (!afterDot && token.Is("ON") && nextToken is { } duplicate && duplicate.Is("DUPLICATE") && frames.Count == 1)
            {
                NewLine(frame.Indent);
                frame.Clause = "DUPLICATE";
                Write(text, space: false);
                previous = token;
                continue;
            }
            if (!afterDot && token.Is("UPDATE") && previous is { } key && key.Is("KEY") && frame.Clause == "DUPLICATE")
            {
                Write(text, space: true);
                frame.Clause = "SET";
                pendingListItem = HasMultipleItems(tokens, index + 1);
                previous = token;
                continue;
            }

            // Cláusula nueva: línea propia, a la sangría de su nivel. Solo en el nivel principal y en subconsultas,
            // no dentro de paréntesis de funciones (GROUP_CONCAT(x ORDER BY y)) ni de CREATE TABLE (...).
            if (!afterDot && IsClauseKeyword(token) && !IsContinuation(previous, token)
                && (frames.Count == 1 || frame.Subquery) && !frame.DefinitionList)
            {
                // FOR UPDATE, ON DELETE, ON UPDATE...: siguen en la misma línea.
                bool inline = previous is { } p && (p.Is("KEY") || p.Is("FOR") || p.Is("ON"));
                // LEFT(...), RIGHT(...), REPLACE(...) y VALUES(col) como funciones, no como JOIN, sentencia o cláusula.
                bool function = nextToken is { Kind: Kind.Symbol, Text: "(" }
                    && (token.Is("LEFT") || token.Is("RIGHT") || token.Is("REPLACE")
                        || token.Is("VALUES") && previous is { Kind: Kind.Symbol } && previous.Text != ")");
                if (!inline && !function)
                {
                    NewLine(frame.Indent);
                    frame.Clause = ClauseName(token);
                    Write(text, space: false);
                    // Con un solo elemento (SELECT a / SELECT *) se queda en la misma línea.
                    pendingListItem = frame.Clause is "SELECT" or "SET" or "VALUES" && HasMultipleItems(tokens, index + 1);
                    previous = token;
                    continue;
                }
            }

            // AND / OR del WHERE, ON o HAVING: una condición por línea.
            if (!afterDot && (token.Is("AND") || token.Is("OR")) && frame.Clause is "WHERE" or "HAVING" or "JOIN"
                && !IsBetweenAnd(tokens, index))
            {
                NewLine(frame.Indent + 1);
                Write(text, space: false);
                previous = token;
                continue;
            }

            bool spaceBefore = !(previous is { Kind: Kind.Symbol, Text: "(" or "." or "unary" });
            Write(text, spaceBefore);
            previous = token;
        }

        while (output.Length > 0 && char.IsWhiteSpace(output[^1])) output.Length--;
        return output.ToString();
    }

    /// <summary>¿La lista que empieza aquí (columnas de un SELECT, asignaciones de un SET...) tiene más de un elemento?</summary>
    private static bool HasMultipleItems(List<Token> tokens, int start)
    {
        int depth = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == Kind.Symbol)
            {
                if (t.Text == "(") depth++;
                else if (t.Text == ")" && --depth < 0) return false;
                else if (t.Text == ";") return false;
                else if (t.Text == "," && depth == 0) return true;
            }
            else if (depth == 0 && (t.Is("FROM") || t.Is("WHERE") || t.Is("GROUP") || t.Is("ORDER") || t.Is("HAVING")
                     || t.Is("LIMIT") || t.Is("UNION") || t.Is("INTO") || t.Is("ON") || t.Is("SELECT")))
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Segunda palabra de una cláusula de varias: GROUP BY, LEFT OUTER JOIN, INSERT INTO, UNION ALL...</summary>
    private static bool IsContinuation(Token? previous, Token token) =>
        previous is { Kind: Kind.Word } p && (
            (token.Is("JOIN") && (p.Is("INNER") || p.Is("LEFT") || p.Is("RIGHT") || p.Is("OUTER") || p.Is("CROSS") || p.Is("FULL") || p.Is("NATURAL")))
            || (token.Is("INNER") && p.Is("NATURAL"))
            || (token.Is("FROM") && p.Is("DELETE")));

    private static string ClauseName(Token token)
    {
        string word = token.Text.ToUpperInvariant();
        return word is "INNER" or "LEFT" or "RIGHT" or "CROSS" or "FULL" or "NATURAL" or "JOIN" ? "JOIN" : word;
    }

    /// <summary>El AND de "BETWEEN x AND y" no separa condiciones.</summary>
    private static bool IsBetweenAnd(List<Token> tokens, int andIndex)
    {
        int depth = 0;
        for (int i = andIndex - 1; i >= 0 && i >= andIndex - 8; i--)
        {
            var t = tokens[i];
            if (t.Kind == Kind.Symbol && t.Text == ")") depth++;
            else if (t.Kind == Kind.Symbol && t.Text == "(") depth--;
            if (depth != 0) continue;
            if (t.Is("BETWEEN")) return true;
            if (t.Is("AND") || t.Is("OR") || t.Is("WHERE") || t.Is("ON") || t.Is("HAVING")) return false;
        }
        return false;
    }
}
