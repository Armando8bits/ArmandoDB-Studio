namespace MySmdb;

/// <summary>Un objeto de la base que contiene el texto buscado.</summary>
/// <param name="Table">Tabla a la que pertenece un trigger, si se conoce.</param>
/// <param name="Where">Dónde coincide: en el nombre, en una columna o en el código.</param>
/// <param name="Detail">La columna con su tipo, o la línea de código donde aparece el texto.</param>
public sealed record SearchHit(SchemaObject Kind, string Name, string? Table, string Where, string Detail)
{
    public const string InName = "Nombre", InColumn = "Columna", InCode = "Código";

    public string KindText => Kind switch
    {
        SchemaObject.Table => "Tabla",
        SchemaObject.View => "Vista",
        SchemaObject.Procedure => "Procedimiento",
        SchemaObject.Function => "Función",
        SchemaObject.Trigger => "Trigger",
        _ => "Índice",
    };
}

/// <param name="Truncated">Había más coincidencias de las que se devuelven.</param>
public sealed record SearchResult(List<SearchHit> Hits, bool Truncated);

/// <summary>
/// Busca un texto en una base de datos: en los nombres de tablas, vistas, procedimientos, funciones y triggers,
/// en los nombres de columna y en el código de vistas, rutinas y triggers. Solo lee del catálogo.
/// </summary>
public static class SchemaSearch
{
    public const int MaxResults = 500;
    public const int MinLength = 2;

    /// <summary>
    /// Patrón LIKE que busca el texto tal cual: '%', '_' y '[' son comodines en LIKE y se anulan con '!'
    /// (todas las consultas llevan ESCAPE '!').
    /// </summary>
    public static string Pattern(string text) =>
        "%" + text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_").Replace("[", "![") + "%";

    public static async Task<SearchResult> SearchAsync(ConnectionProfile profile, string database, string text,
        bool names = true, bool columns = true, bool code = true)
    {
        text = text.Trim();
        if (text.Length < MinLength) return new SearchResult(new List<SearchHit>(), false);

        // Cada fila: tipo (T, V, P, F, R), nombre, tabla del trigger, y el detalle (columna o código).
        var hits = new List<SearchHit>();
        async Task Collect(string where, Task<List<string?[]>> query)
        {
            foreach (var row in (await query).OrderBy(r => r[1], StringComparer.OrdinalIgnoreCase))
            {
                var kind = (row[0] ?? "").Trim() switch
                {
                    "T" => SchemaObject.Table, "V" => SchemaObject.View, "P" => SchemaObject.Procedure,
                    "F" => SchemaObject.Function, _ => SchemaObject.Trigger,
                };
                string name = (row[1] ?? "").Trim();
                string? table = string.IsNullOrWhiteSpace(row[2]) ? null : row[2]!.Trim();
                string detail = where == SearchHit.InCode ? MatchingLine(row[3] ?? "", text)
                    : where == SearchHit.InColumn ? (row[3] ?? "").Trim()
                    : table != null ? $"en {table}" : "";
                var hit = new SearchHit(kind, name, table, where, detail);
                // Sybase guarda el código en trozos: un objeto puede salir una vez por trozo.
                if (!hits.Contains(hit) && !(where == SearchHit.InCode && hits.Any(h => h.Where == where && h.Kind == kind && h.Name == name)))
                    hits.Add(hit);
            }
        }

        var queries = Queries(profile, database, Pattern(text));
        if (names) await Collect(SearchHit.InName, queries.Names());
        if (columns) await Collect(SearchHit.InColumn, queries.Columns());
        if (code) await Collect(SearchHit.InCode, queries.Code());

        bool truncated = hits.Count > MaxResults;
        if (truncated) hits.RemoveRange(MaxResults, hits.Count - MaxResults);
        return new SearchResult(hits, truncated);
    }

    /// <summary>La línea del código donde aparece el texto, con su número.</summary>
    private static string MatchingLine(string code, string text)
    {
        string[] lines = code.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            string line = lines[i].Trim();
            return $"línea {i + 1}: {(line.Length > 200 ? line[..200] + "..." : line)}";
        }
        return "";
    }

    private sealed record EngineQueries(Func<Task<List<string?[]>>> Names, Func<Task<List<string?[]>>> Columns, Func<Task<List<string?[]>>> Code);

    private static EngineQueries Queries(ConnectionProfile profile, string database, string pattern) => profile.Kind switch
    {
        DbKind.Sqlite => Sqlite(profile, pattern),
        DbKind.SqlServer => SqlServer(profile, database, pattern),
        DbKind.Sybase => Sybase(profile, database, pattern),
        _ => MySql(profile, database, pattern),
    };

    private static EngineQueries MySql(ConnectionProfile profile, string database, string pattern)
    {
        const string routineKind = "CASE WHEN ROUTINE_TYPE = 'FUNCTION' THEN 'F' ELSE 'P' END";
        return new EngineQueries(
            () => Db.QueryAsync(profile, null,
                "SELECT CASE WHEN TABLE_TYPE = 'VIEW' THEN 'V' ELSE 'T' END, TABLE_NAME, NULL, NULL FROM information_schema.TABLES " +
                "WHERE TABLE_SCHEMA = @p0 AND TABLE_NAME LIKE @p1 ESCAPE '!' " +
                $"UNION ALL SELECT {routineKind}, ROUTINE_NAME, NULL, NULL FROM information_schema.ROUTINES " +
                "WHERE ROUTINE_SCHEMA = @p0 AND ROUTINE_NAME LIKE @p1 ESCAPE '!' " +
                "UNION ALL SELECT 'R', TRIGGER_NAME, EVENT_OBJECT_TABLE, NULL FROM information_schema.TRIGGERS " +
                "WHERE TRIGGER_SCHEMA = @p0 AND TRIGGER_NAME LIKE @p1 ESCAPE '!'", database, pattern),
            () => Db.QueryAsync(profile, null,
                "SELECT CASE WHEN t.TABLE_TYPE = 'VIEW' THEN 'V' ELSE 'T' END, c.TABLE_NAME, NULL, CONCAT(c.COLUMN_NAME, '  ', c.COLUMN_TYPE) " +
                "FROM information_schema.COLUMNS c JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME " +
                "WHERE c.TABLE_SCHEMA = @p0 AND c.COLUMN_NAME LIKE @p1 ESCAPE '!'", database, pattern),
            () => Db.QueryAsync(profile, null,
                $"SELECT {routineKind}, ROUTINE_NAME, NULL, ROUTINE_DEFINITION FROM information_schema.ROUTINES " +
                "WHERE ROUTINE_SCHEMA = @p0 AND ROUTINE_DEFINITION LIKE @p1 ESCAPE '!' " +
                "UNION ALL SELECT 'V', TABLE_NAME, NULL, VIEW_DEFINITION FROM information_schema.VIEWS " +
                "WHERE TABLE_SCHEMA = @p0 AND VIEW_DEFINITION LIKE @p1 ESCAPE '!' " +
                "UNION ALL SELECT 'R', TRIGGER_NAME, EVENT_OBJECT_TABLE, ACTION_STATEMENT FROM information_schema.TRIGGERS " +
                "WHERE TRIGGER_SCHEMA = @p0 AND ACTION_STATEMENT LIKE @p1 ESCAPE '!'", database, pattern));
    }

    private static EngineQueries Sqlite(ConnectionProfile profile, string pattern)
    {
        // Las tablas internas (sqlite_sequence...) no son del usuario.
        const string own = "name NOT LIKE 'sqlite!_%' ESCAPE '!'";
        return new EngineQueries(
            () => Db.QueryAsync(profile, null,
                "SELECT CASE type WHEN 'view' THEN 'V' WHEN 'trigger' THEN 'R' ELSE 'T' END, name, CASE WHEN type = 'trigger' THEN tbl_name END, NULL " +
                $"FROM sqlite_master WHERE type IN ('table', 'view', 'trigger') AND {own} AND name LIKE @p0 ESCAPE '!'", pattern),
            () => Db.QueryAsync(profile, null,
                "SELECT CASE m.type WHEN 'view' THEN 'V' ELSE 'T' END, m.name, NULL, p.name || '  ' || p.type " +
                $"FROM sqlite_master m JOIN pragma_table_info(m.name) p WHERE m.type IN ('table', 'view') AND m.{own} AND p.name LIKE @p0 ESCAPE '!'", pattern),
            () => Db.QueryAsync(profile, null,
                "SELECT CASE type WHEN 'view' THEN 'V' ELSE 'R' END, name, CASE WHEN type = 'trigger' THEN tbl_name END, sql " +
                "FROM sqlite_master WHERE type IN ('view', 'trigger') AND sql LIKE @p0 ESCAPE '!'", pattern));
    }

    private static EngineQueries SqlServer(ConnectionProfile profile, string database, string pattern)
    {
        const string kind = "CASE RTRIM(o.type) WHEN 'U' THEN 'T' WHEN 'V' THEN 'V' WHEN 'P' THEN 'P' WHEN 'TR' THEN 'R' ELSE 'F' END";
        // El trigger lleva la tabla a la que pertenece.
        const string objects =
            "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
            "LEFT JOIN sys.objects po ON po.object_id = o.parent_object_id AND o.type = 'TR' " +
            "LEFT JOIN sys.schemas ps ON ps.schema_id = po.schema_id ";
        return new EngineQueries(
            () => Db.QueryAsync(profile, database,
                $"SELECT {kind}, s.name + '.' + o.name, ps.name + '.' + po.name, NULL {objects}" +
                "WHERE o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR') AND o.is_ms_shipped = 0 AND o.name LIKE @p0 ESCAPE '!'", pattern),
            () => Db.QueryAsync(profile, database,
                "SELECT CASE RTRIM(o.type) WHEN 'V' THEN 'V' ELSE 'T' END, s.name + '.' + o.name, NULL, c.name + '  ' + t.name " +
                "FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                "JOIN sys.types t ON t.user_type_id = c.user_type_id " +
                "WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND c.name LIKE @p0 ESCAPE '!'", pattern),
            () => Db.QueryAsync(profile, database,
                $"SELECT {kind}, s.name + '.' + o.name, ps.name + '.' + po.name, m.definition {objects}" +
                "JOIN sys.sql_modules m ON m.object_id = o.object_id " +
                "WHERE o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR') AND o.is_ms_shipped = 0 AND m.definition LIKE @p0 ESCAPE '!'", pattern));
    }

    private static EngineQueries Sybase(ConnectionProfile profile, string database, string pattern)
    {
        // Sin parámetros, como el resto del catálogo de Sybase; y en minúsculas, porque muchos servidores
        // distinguen mayúsculas al comparar.
        string like = $"like '{pattern.ToLowerInvariant().Replace("'", "''")}' escape '!'";
        const string kind = "case rtrim(o.type) when 'U' then 'T' when 'V' then 'V' when 'P' then 'P' when 'TR' then 'R' else 'F' end";
        return new EngineQueries(
            () => Db.QueryAsync(profile, database,
                $"select {kind}, u.name + '.' + o.name, null, null from sysobjects o, sysusers u " +
                $"where u.uid = o.uid and o.type in ('U', 'V', 'P', 'SF', 'F', 'TR') and lower(o.name) {like}"),
            () => Db.QueryAsync(profile, database,
                "select case rtrim(o.type) when 'V' then 'V' else 'T' end, u.name + '.' + o.name, null, c.name + '  ' + t.name " +
                "from syscolumns c, sysobjects o, sysusers u, systypes t " +
                $"where o.id = c.id and u.uid = o.uid and t.usertype = c.usertype and o.type in ('U', 'V') and lower(c.name) {like}"),
            // El código va en trozos de 255 caracteres: un texto partido entre dos trozos no se encuentra.
            () => Db.QueryAsync(profile, database,
                $"select {kind}, u.name + '.' + o.name, null, c.text from syscomments c, sysobjects o, sysusers u " +
                $"where o.id = c.id and u.uid = o.uid and o.type in ('V', 'P', 'SF', 'F', 'TR') and lower(c.text) {like}"));
    }
}
