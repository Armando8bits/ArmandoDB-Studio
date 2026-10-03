namespace MySmdb;

/// <summary>Palabras clave y funciones de MySQL/SQLite: para el formateador (mayúsculas) y el autocompletado.</summary>
public static class SqlKeywords
{
    public static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "AS", "ASC", "AUTO_INCREMENT", "AUTOINCREMENT", "BEGIN", "BETWEEN", "BY", "CALL", "CASCADE",
        "CASE", "CHARSET", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONSTRAINT", "CREATE", "CROSS", "DATABASE", "DECLARE", "DEFAULT",
        "DELETE", "DESC", "DESCRIBE", "DISTINCT", "DROP", "DUPLICATE", "ELSE", "END", "ENGINE", "EXISTS", "EXPLAIN", "FALSE",
        "FOREIGN", "FROM", "FULL", "FUNCTION", "GROUP", "HAVING", "IF", "IGNORE", "IN", "INDEX", "INNER", "INSERT", "INTERVAL",
        "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "LIMIT", "NATURAL", "NOT", "NULL", "OFFSET", "ON", "OR", "ORDER", "OUTER",
        "PRIMARY", "PROCEDURE", "RECURSIVE", "REFERENCES", "REGEXP", "RENAME", "REPLACE", "RETURN", "RETURNS", "RIGHT", "ROLLBACK",
        "SELECT", "SET", "SHOW", "TABLE", "TEMPORARY", "THEN", "TO", "TRANSACTION", "TRIGGER", "TRUE", "TRUNCATE", "UNION",
        "UNIQUE", "UNSIGNED", "UPDATE", "USE", "USING", "VALUES", "VIEW", "WHEN", "WHERE", "WITH",
        "AFTER", "BEFORE", "CONFLICT", "DO", "EACH", "FOR", "INSTEAD", "LOCK", "NOTHING", "OF", "OVER", "PARTITION", "ROLLUP",
        "ROW", "SEPARATOR", "SHARE",
        // Tipos de datos
        "BIGINT", "BINARY", "BLOB", "BOOLEAN", "CHAR", "DATE", "DATETIME", "DECIMAL", "DOUBLE", "ENUM", "FLOAT", "INT", "INTEGER",
        "JSON", "LONGTEXT", "MEDIUMINT", "MEDIUMTEXT", "NUMERIC", "REAL", "SMALLINT", "TEXT", "TIME", "TIMESTAMP", "TINYINT",
        "VARBINARY", "VARCHAR", "YEAR",
    };

    /// <summary>Funciones: van en mayúsculas y pegadas a su paréntesis: COUNT(*).</summary>
    public static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "AVG", "CAST", "CEIL", "CEILING", "COALESCE", "CONCAT", "CONCAT_WS", "CONVERT", "COUNT", "CURDATE", "CURRENT_DATE",
        "CURRENT_TIMESTAMP", "DATE_ADD", "DATE_FORMAT", "DATE_SUB", "DATEDIFF", "DAY", "FLOOR", "FORMAT", "GREATEST",
        "GROUP_CONCAT", "HOUR", "IFNULL", "INSTR", "JSON_EXTRACT", "JSON_OBJECT", "LAST_INSERT_ID", "LCASE", "LEAST", "LEFT",
        "LENGTH", "LOWER", "LPAD", "LTRIM", "MAX", "MID", "MIN", "MINUTE", "MOD", "MONTH", "NOW", "NULLIF", "POWER", "RAND",
        "REPLACE", "RIGHT", "ROUND", "ROW_NUMBER", "RPAD", "RTRIM", "SECOND", "STRFTIME", "SUBSTR", "SUBSTRING", "SUM",
        "TIMESTAMPDIFF", "TRIM", "TRUNCATE", "UCASE", "UPPER", "UUID", "YEAR", "IIF", "DATE", "DATETIME", "TIME",
    };

    /// <summary>Tipos que llevan tamaño entre paréntesis: van pegados, VARCHAR(50).</summary>
    public static readonly HashSet<string> SizedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BINARY", "BIGINT", "CHAR", "DECIMAL", "DOUBLE", "ENUM", "FLOAT", "INT", "INTEGER", "MEDIUMINT", "NUMERIC", "REAL",
        "SMALLINT", "TINYINT", "VARBINARY", "VARCHAR",
    };

    public static bool IsKeyword(string word) => Keywords.Contains(word) || Functions.Contains(word);
}
