using System.Text.RegularExpressions;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace MySmdb;

/// <summary>Tablas de una base y sus columnas (nombre y tipo), para el autocompletado.</summary>
public sealed class SchemaInfo
{
    public Dictionary<string, List<(string Name, string Type)>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Motor de la base: decide las comillas de los nombres que las necesitan (`nombre` o [nombre]).</summary>
    public DbKind Kind { get; init; }

    /// <summary>SQL Server: esquema de las tablas que no están en dbo (tabla → esquema); al insertarlas se antepone.</summary>
    public Dictionary<string, string> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Esquemas cargados por conexión y base, con una sola consulta cada uno. Se recargan pasados unos minutos
/// o al ejecutar sentencias que cambian la estructura (CREATE, ALTER, DROP...).
/// </summary>
public static class SchemaCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, (DateTime Loaded, Task<SchemaInfo> Task)> Entries = new();

    private static string Key(ConnectionProfile profile, string? database) => $"{profile.Name}|{database}";

    /// <summary>El esquema si ya está cargado; si no, empieza a cargarlo y devuelve null (se usará la próxima vez).</summary>
    public static SchemaInfo? TryGet(ConnectionProfile profile, string? database)
    {
        if (string.IsNullOrEmpty(database)) return null;
        string key = Key(profile, database);
        if (!Entries.TryGetValue(key, out var entry) || DateTime.UtcNow - entry.Loaded > MaxAge || entry.Task.IsFaulted)
        {
            entry = (DateTime.UtcNow, LoadAsync(profile, database));
            Entries[key] = entry;
        }
        return entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;
    }

    public static Task<SchemaInfo> GetAsync(ConnectionProfile profile, string database)
    {
        TryGet(profile, database);
        return Entries[Key(profile, database)].Task;
    }

    public static void Invalidate(ConnectionProfile profile)
    {
        foreach (string key in Entries.Keys.Where(k => k.StartsWith(profile.Name + "|", StringComparison.Ordinal)).ToList())
            Entries.Remove(key);
    }

    private static async Task<SchemaInfo> LoadAsync(ConnectionProfile profile, string database)
    {
        var schema = new SchemaInfo { Kind = profile.Kind };
        if (profile.Kind == DbKind.SqlServer)
        {
            // Las tablas se buscan por su nombre sin esquema, que es como se suelen escribir (o tras "dbo.").
            foreach (var row in await SqlServerCatalog.GetCompletionRowsAsync(profile, database))
            {
                if (!schema.Tables.TryGetValue(row[1]!, out var list))
                {
                    schema.Tables[row[1]!] = list = new();
                    if (!string.Equals(row[0], "dbo", StringComparison.OrdinalIgnoreCase)) schema.Schemas[row[1]!] = row[0]!;
                }
                list.Add((row[2]!, row[3] ?? ""));
            }
            return schema;
        }

        var rows = profile.Kind == DbKind.Sqlite
            ? await Db.QueryAsync(profile, null,
                "SELECT m.name, p.name, p.type FROM sqlite_master m JOIN pragma_table_info(m.name) p " +
                "WHERE m.type IN ('table', 'view') ORDER BY m.name, p.cid")
            : await Db.QueryAsync(profile, null,
                "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA = @p0 ORDER BY TABLE_NAME, ORDINAL_POSITION", database);

        foreach (var row in rows)
        {
            if (!schema.Tables.TryGetValue(row[0]!, out var columns))
                schema.Tables[row[0]!] = columns = new();
            columns.Add((row[1]!, row[2] ?? ""));
        }
        return schema;
    }
}

public enum CompletionKind { Keyword, Function, Table, Column }

/// <summary>Una sugerencia de la lista de autocompletado.</summary>
public sealed class SqlCompletionItem : ICompletionData
{
    private static readonly Regex PlainIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_$]*$");

    private readonly DbKind _dialect;
    private readonly string? _schema;

    /// <param name="dialect">Motor, para las comillas de los nombres que las necesitan.</param>
    /// <param name="schema">SQL Server: esquema que se antepone a la tabla (las que no están en dbo).</param>
    public SqlCompletionItem(string text, CompletionKind kind, string? detail = null, DbKind dialect = DbKind.MySql, string? schema = null)
    {
        Text = text;
        Kind = kind;
        _dialect = dialect;
        _schema = schema;
        Description = kind switch
        {
            CompletionKind.Table => "Tabla" + (detail != null ? $" · {detail}" : ""),
            CompletionKind.Column => "Columna" + (detail != null ? $" · {detail}" : ""),
            CompletionKind.Function => "Función",
            _ => "Palabra clave",
        };
        // Primero lo del esquema; las palabras clave, al final.
        Priority = kind switch { CompletionKind.Column => 3, CompletionKind.Table => 2, CompletionKind.Function => 1, _ => 0 };
    }

    public CompletionKind Kind { get; }
    public string Text { get; }
    public object Description { get; }
    public double Priority { get; }
    public ImageSource? Image => null;

    public object Content => Kind switch
    {
        CompletionKind.Table => $"{Text}    ({(_schema != null ? _schema + " · " : "")}tabla)",
        CompletionKind.Column => $"{Text}    (columna)",
        CompletionKind.Function => $"{Text}()",
        _ => Text,
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        // Nombres con espacios, guiones o que coinciden con palabras clave, entre comillas del motor (`nombre` o [nombre]).
        string Quoted(string name) => !PlainIdentifier.IsMatch(name) || SqlKeywords.IsKeyword(name) ? Db.QuoteId(_dialect, name) : name;
        string insert = Kind switch
        {
            CompletionKind.Table => (_schema != null ? Quoted(_schema) + "." : "") + Quoted(Text),
            CompletionKind.Column => Quoted(Text),
            _ => Text,
        };
        if (Kind == CompletionKind.Function) insert += "(";
        textArea.Document.Replace(completionSegment, insert);
    }
}

/// <summary>Qué sugerir según el texto alrededor del cursor.</summary>
public static class SqlCompletion
{
    private const string Identifier = @"(?:`[^`]+`|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_$]*)";

    // "FROM cliente c", "JOIN `db`.`pedido` AS p", "UPDATE cliente", ", producto pr" (listas del FROM).
    private static readonly Regex TableReference = new(
        $@"(?:\b(?:FROM|JOIN|UPDATE|INTO)\s+|,\s*)(?:{Identifier}\.)?(?<table>{Identifier})(?:\s+(?:AS\s+)?(?<alias>{Identifier}))?",
        RegexOptions.IgnoreCase);

    private static string Unquote(string name) => name.Trim('`', '[', ']');

    /// <summary>Tabla o alias → tabla, en la sentencia actual (entre el ';' anterior y el siguiente al cursor).</summary>
    public static Dictionary<string, string> TablesInStatement(string text, int caret, SchemaInfo schema)
    {
        int start = text.LastIndexOf(';', Math.Max(0, Math.Min(caret, text.Length) - 1)) + 1;
        int end = text.IndexOf(';', Math.Min(caret, text.Length));
        string statement = text[start..(end < 0 ? text.Length : end)];

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TableReference.Matches(statement))
        {
            string table = Unquote(match.Groups["table"].Value);
            if (!schema.Tables.ContainsKey(table)) continue;
            map[table] = table;
            if (match.Groups["alias"].Success)
            {
                string alias = Unquote(match.Groups["alias"].Value);
                if (!SqlKeywords.IsKeyword(alias)) map[alias] = table;
            }
        }
        return map;
    }

    /// <summary>
    /// Sugerencias. Tras "algo.": las columnas de esa tabla o alias. Si no: palabras clave, funciones, tablas y
    /// las columnas de las tablas que ya aparecen en la sentencia.
    /// </summary>
    public static List<SqlCompletionItem> Suggestions(string text, int caret, string? qualifier, SchemaInfo? schema)
    {
        var items = new List<SqlCompletionItem>();
        if (qualifier != null)
        {
            if (schema == null) return items;
            var tables = TablesInStatement(text, caret, schema);
            string table = tables.TryGetValue(qualifier, out var real) ? real : qualifier;
            if (schema.Tables.TryGetValue(table, out var columns))
                items.AddRange(columns.Select(c => new SqlCompletionItem(c.Name, CompletionKind.Column, $"{table} · {c.Type}", schema.Kind)));
            return items;
        }

        if (schema != null)
        {
            var inStatement = TablesInStatement(text, caret, schema).Values.Distinct(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string table in inStatement)
                foreach (var column in schema.Tables[table])
                    if (seen.Add(column.Name))
                        items.Add(new SqlCompletionItem(column.Name, CompletionKind.Column, $"{table} · {column.Type}", schema.Kind));
            items.AddRange(schema.Tables.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(t => new SqlCompletionItem(t, CompletionKind.Table, $"{schema.Tables[t].Count} columnas", schema.Kind, schema.Schemas.GetValueOrDefault(t))));
        }
        items.AddRange(SqlKeywords.Functions.OrderBy(f => f).Select(f => new SqlCompletionItem(f, CompletionKind.Function)));
        items.AddRange(SqlKeywords.Keywords.Where(k => !SqlKeywords.Functions.Contains(k)).OrderBy(k => k)
            .Select(k => new SqlCompletionItem(k, CompletionKind.Keyword)));
        return items;
    }

    /// <summary>¿El cursor está dentro de una cadena o de un comentario de línea? (solo mira la línea actual).</summary>
    public static bool InStringOrComment(string line, int column, bool mysql)
    {
        char quote = '\0';
        for (int i = 0; i < column && i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == '\\' && mysql && quote != '`') { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"' or '`') quote = c;
            else if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') return true;
            else if (c == '#' && mysql) return true;
        }
        return quote is '\'' or '"';
    }
}
