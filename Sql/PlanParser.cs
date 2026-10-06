using System.Globalization;
using System.Text.RegularExpressions;

namespace MySmdb;

public enum PlanNodeKind { Other, FullScan, Index, Join, SortOrTemp }

/// <summary>Un paso del plan de ejecución, con los pasos de los que se alimenta.</summary>
public sealed class PlanNode
{
    public string Operation { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? Cost { get; init; }
    public double? Rows { get; init; }
    /// <summary>Filas y tiempo reales (solo con EXPLAIN ANALYZE).</summary>
    public string? Actual { get; init; }
    public PlanNodeKind Kind { get; init; }
    public List<PlanNode> Children { get; } = new();
}

/// <summary>Convierte la salida de EXPLAIN (árbol de MySQL, tabla clásica o SQLite) en un árbol de pasos.</summary>
public static class PlanParser
{
    private static readonly Regex Metrics = new(@"\s*\((?:cost=[^)]*|actual time=[^)]*|never executed)\)");
    private static readonly Regex CostValue = new(@"cost=([\d.eE+]+)");
    private static readonly Regex RowsValue = new(@"\(cost=[^)]*?rows=([\d.eE+]+)");
    private static readonly Regex ActualValue = new(@"actual time=[\d.]+\.\.([\d.]+) rows=([\d.eE+]+) loops=(\d+)");

    private static double? Number(Match match, int group = 1) =>
        match.Success && double.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    /// <summary>
    /// EXPLAIN FORMAT=TREE de MySQL: cada paso empieza por "->" y su sangría indica de quién depende.
    /// "-> Nested loop inner join  (cost=9.03 rows=70)"
    /// </summary>
    public static PlanNode? FromMySqlTree(string text)
    {
        var roots = new List<PlanNode>();
        var stack = new Stack<(int Indent, PlanNode Node)>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0 || line[..arrow].Trim().Length > 0) continue;

            string body = line[(arrow + 2)..].Trim();
            string title = Metrics.Replace(body, "").Trim();
            var actual = ActualValue.Match(body);
            var (operation, detail) = SplitOperation(title);
            var node = new PlanNode
            {
                Operation = operation,
                Detail = detail,
                Cost = Number(CostValue.Match(body)),
                Rows = Number(RowsValue.Match(body)),
                Actual = actual.Success
                    ? $"real: {actual.Groups[2].Value} filas, {actual.Groups[1].Value} ms" + (actual.Groups[3].Value != "1" ? $" × {actual.Groups[3].Value}" : "")
                    : body.Contains("never executed") ? "no llegó a ejecutarse" : null,
                Kind = Classify(title),
            };

            while (stack.Count > 0 && stack.Peek().Indent >= arrow) stack.Pop();
            if (stack.Count > 0) stack.Peek().Node.Children.Add(node);
            else roots.Add(node);
            stack.Push((arrow, node));
        }
        return SingleRoot(roots);
    }

    /// <summary>EXPLAIN QUERY PLAN de SQLite: filas (id, parent, notused, detail).</summary>
    public static PlanNode? FromSqlite(ResultSet result)
    {
        var nodes = new Dictionary<long, PlanNode>();
        var roots = new List<PlanNode>();
        foreach (var row in result.Rows)
        {
            string detail = CellText.Format(row[3]);
            var (operation, rest) = SplitOperation(detail);
            var node = new PlanNode { Operation = operation, Detail = rest, Kind = Classify(detail) };
            nodes[Convert.ToInt64(row[0])] = node;
            if (nodes.TryGetValue(Convert.ToInt64(row[1]), out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }
        return SingleRoot(roots);
    }

    /// <summary>
    /// EXPLAIN clásico (MariaDB, MySQL antiguos): una fila por tabla, en el orden en que se combinan.
    /// Se dibuja como una cadena: cada tabla se une al resultado de las anteriores.
    /// </summary>
    public static PlanNode? FromClassicTable(ResultSet result)
    {
        int Column(string name) => Array.FindIndex(result.Columns, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        int table = Column("table"), type = Column("type"), key = Column("key"), rows = Column("rows"), extra = Column("Extra"), select = Column("select_type");
        if (table < 0 || type < 0) return null;

        string Cell(object?[] row, int index) => index < 0 || row[index] == null ? "" : CellText.Format(row[index]);
        PlanNode? root = null, last = null;
        foreach (var row in result.Rows)
        {
            string access = Cell(row, type);
            var node = new PlanNode
            {
                Operation = $"{Cell(row, select)} {Cell(row, table)}".Trim(),
                Detail = string.Join(" · ", new[] { "acceso: " + access, Cell(row, key).Length > 0 ? "índice: " + Cell(row, key) : "", Cell(row, extra) }.Where(s => s.Length > 0)),
                Rows = double.TryParse(Cell(row, rows), NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : null,
                Kind = access.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? PlanNodeKind.FullScan
                    : access.Length > 0 ? PlanNodeKind.Index : PlanNodeKind.Other,
            };
            if (root == null) root = node;
            else last!.Children.Add(node);
            last = node;
        }
        return root;
    }

    /// <summary>
    /// SET SHOWPLAN_ALL de SQL Server: una fila por sentencia (Parent = 0) y una por cada operador, enlazadas
    /// por NodeId y Parent. Los operadores traen filas y costo estimados.
    /// </summary>
    public static PlanNode? FromSqlServer(ResultSet result)
    {
        int Column(string name) => Array.FindIndex(result.Columns, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        int text = Column("StmtText"), statement = Column("StmtId"), id = Column("NodeId"), parent = Column("Parent"),
            physical = Column("PhysicalOp"), logical = Column("LogicalOp"), argument = Column("Argument"),
            rows = Column("EstimateRows"), cost = Column("TotalSubtreeCost"), type = Column("Type");
        if (id < 0 || parent < 0 || physical < 0) return null;

        string Cell(object?[] row, int index) => index < 0 || row[index] == null ? "" : CellText.Format(row[index]).Trim();
        double? Value(object?[] row, int index) => index >= 0 && row[index] != null ? Convert.ToDouble(row[index], CultureInfo.InvariantCulture) : null;
        string Shorten(string value, int max) => value.Length > max ? value[..(max - 1)] + "…" : value;

        var nodes = new Dictionary<(string, long), PlanNode>();
        var roots = new List<PlanNode>();
        foreach (var row in result.Rows)
        {
            string operation = Cell(row, physical), logicalOperation = Cell(row, logical);
            long parentId = Convert.ToInt64(row[parent] ?? 0L);
            PlanNode node;
            if (parentId == 0 && operation.Length == 0)
            {
                // La sentencia: su tipo (SELECT, INSERT...) y su primera línea.
                node = new PlanNode
                {
                    Operation = Cell(row, type) is { Length: > 0 } kind ? kind : "Sentencia",
                    Detail = Shorten(Cell(row, text).ReplaceLineEndings(" "), 140),
                    Cost = Value(row, cost),
                    Rows = Value(row, rows),
                };
                roots.Add(node);
            }
            else
            {
                string detail = string.Join(" · ", new[]
                {
                    logicalOperation.Length > 0 && !logicalOperation.Equals(operation, StringComparison.OrdinalIgnoreCase) ? logicalOperation : "",
                    Shorten(Cell(row, argument), 160),
                }.Where(s => s.Length > 0));
                node = new PlanNode { Operation = operation, Detail = detail, Cost = Value(row, cost), Rows = Value(row, rows), Kind = ClassifySqlServer(operation, logicalOperation) };
                if (nodes.TryGetValue((Cell(row, statement), parentId), out var above)) above.Children.Add(node);
                else roots.Add(node);
            }
            nodes[(Cell(row, statement), Convert.ToInt64(row[id] ?? 0L))] = node;
        }

        // Las sentencias sin operadores (SET, DECLARE...) no aportan nada al diagrama.
        var withPlan = roots.Where(r => r.Children.Count > 0).ToList();
        return SingleRoot(withPlan.Count > 0 ? withPlan : roots);
    }

    private static PlanNodeKind ClassifySqlServer(string physical, string logical)
    {
        bool Has(string text) => physical.Contains(text, StringComparison.OrdinalIgnoreCase);

        // Recorrer el índice agrupado entero es recorrer toda la tabla.
        if (Has("Table Scan") || Has("Clustered Index Scan")) return PlanNodeKind.FullScan;
        if (Has("Seek") || Has("Lookup") || Has("Index Scan")) return PlanNodeKind.Index;
        if (Has("Nested Loops") || Has("Merge Join") || (Has("Hash Match") && logical.Contains("Join", StringComparison.OrdinalIgnoreCase))) return PlanNodeKind.Join;
        if (Has("Sort") || Has("Spool") || Has("Aggregate") || Has("Hash Match") || Has("Top")) return PlanNodeKind.SortOrTemp;
        return PlanNodeKind.Other;
    }

    private static PlanNode? SingleRoot(List<PlanNode> roots)
    {
        if (roots.Count == 0) return null;
        if (roots.Count == 1) return roots[0];
        var root = new PlanNode { Operation = "Consulta" };
        root.Children.AddRange(roots);
        return root;
    }

    /// <summary>"Index scan on b using name" → ("Index scan", "on b using name"); "Sort: n DESC" → ("Sort", "n DESC").</summary>
    private static (string Operation, string Detail) SplitOperation(string title)
    {
        int colon = title.IndexOf(": ", StringComparison.Ordinal);
        int on = title.IndexOf(" on ", StringComparison.Ordinal);
        if (colon > 0 && (on < 0 || colon < on)) return (title[..colon], title[(colon + 2)..]);
        if (on > 0) return (title[..on], title[(on + 1)..]);

        // SQLite: "SCAN cliente", "SEARCH d USING COVERING INDEX ix (saldo>?)", "USE TEMP B-TREE FOR ORDER BY".
        foreach (string verb in new[] { "SCAN ", "SEARCH ", "USE TEMP B-TREE ", "CO-ROUTINE ", "MATERIALIZE ", "LIST SUBQUERY ", "SCALAR SUBQUERY " })
            if (title.StartsWith(verb, StringComparison.Ordinal))
                return (verb.Trim(), title[verb.Length..]);
        return (title, "");
    }

    /// <summary>Tipo de paso, para colorearlo: lo importante es ver de un vistazo los recorridos completos de tabla.</summary>
    private static PlanNodeKind Classify(string title)
    {
        bool Has(string text) => title.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Has("Table scan") && !Has("<temporary>") && !Has("<subquery")) return PlanNodeKind.FullScan;
        if (title.StartsWith("SCAN ", StringComparison.Ordinal) && !Has("USING")) return PlanNodeKind.FullScan;
        if (Has("index lookup") || Has("index scan") || Has("index range scan") || Has("Covering index") || Has("Constant row")
            || title.StartsWith("SEARCH ", StringComparison.Ordinal) || title.StartsWith("SCAN ", StringComparison.Ordinal))
            return PlanNodeKind.Index;
        if (Has("join") || Has("Nested loop")) return PlanNodeKind.Join;
        if (Has("Sort") || Has("temporary") || Has("TEMP B-TREE") || Has("Materialize") || Has("Aggregate") || Has("Group")) return PlanNodeKind.SortOrTemp;
        return PlanNodeKind.Other;
    }
}
