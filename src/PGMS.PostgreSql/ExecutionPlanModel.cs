using System.Globalization;
using System.Text.Json;

namespace PGMS.PostgreSql;

public enum ExecutionPlanIcon { Statement, TableScan, IndexScan, BitmapScan, Join, Sort, Aggregate, Limit, Materialize, Append, Gather, Modify, Subquery, Other }

public sealed record ExecutionPlanDetail(string Label, string Text);

/// <summary>One box of the graphical plan. The statement box is a pseudo operator placed before the server's root node, as in SSMS.</summary>
public sealed class ExecutionPlanOperator
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ObjectName { get; init; }
    public ExecutionPlanIcon Icon { get; init; }
    public double CostPercent { get; init; }
    public double? Seconds { get; init; }
    public double? ActualRows { get; init; }
    public double EstimatedRows { get; init; }
    public bool IsStatement { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<KeyValuePair<string, string>> Summary { get; init; } = [];
    public IReadOnlyList<ExecutionPlanDetail> Details { get; init; } = [];
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    public IReadOnlyList<ExecutionPlanOperator> Children { get; init; } = [];

    /// <summary>Rows that leave this operator; sets the thickness of its arrow.</summary>
    public double FlowRows => ActualRows ?? EstimatedRows;

    /// <summary>Text under the icon, one entry per line.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = new List<string> { Title };
            if (Subtitle is not null) lines.Add("(" + Subtitle + ")");
            if (ObjectName is not null) lines.Add(ObjectName);
            lines.Add("Cost: " + CostPercent.ToString("0", CultureInfo.InvariantCulture) + " %");
            if (IsStatement) return lines;
            if (Seconds is { } seconds) lines.Add(seconds.ToString("0.000", CultureInfo.InvariantCulture) + "s");
            if (ActualRows is { } actual)
            {
                double percent = EstimatedRows > 0 ? 100 * actual / EstimatedRows : 0;
                lines.Add(actual.ToString("0", CultureInfo.InvariantCulture) + " of");
                lines.Add(EstimatedRows.ToString("0", CultureInfo.InvariantCulture) + " (" + percent.ToString("0", CultureInfo.InvariantCulture) + "%)");
            }
            return lines;
        }
    }
}

public sealed record ExecutionPlanStatement(int Number, string QueryText, double BatchPercent, ExecutionPlanOperator Root, QueryPlan Source)
{
    public string Header => $"Query {Number}: Query cost (relative to the batch): {BatchPercent.ToString("0", CultureInfo.InvariantCulture)}%";
}

public enum ExecutionPlanComparison { Contains = 0, Equal = 1, GreaterThan = 2, LessThan = 3 }

/// <summary>Find Node: operators in display order and the test applied to them. The statement box repeats the properties of the first operator, so it only answers for its own "Statement".</summary>
public static class ExecutionPlanSearch
{
    public static IReadOnlyList<ExecutionPlanOperator> Operators(IEnumerable<ExecutionPlanStatement> statements)
    {
        var all = new List<ExecutionPlanOperator>();
        void Visit(ExecutionPlanOperator op) { all.Add(op); foreach (var child in op.Children) Visit(child); }
        foreach (var statement in statements) Visit(statement.Root);
        return all;
    }

    public static IReadOnlyList<string> PropertyNames(IEnumerable<ExecutionPlanOperator> operators) =>
        operators.SelectMany(op => op.Properties.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Numbers compare as numbers when both sides are numeric; otherwise as text, ignoring case.</summary>
    public static bool Matches(ExecutionPlanOperator op, string property, ExecutionPlanComparison comparison, string value)
    {
        if (op.IsStatement && property != "Statement") return false;
        if (!op.Properties.TryGetValue(property, out var actual)) return false;
        if (comparison == ExecutionPlanComparison.Contains) return actual.Contains(value, StringComparison.OrdinalIgnoreCase);
        int order = double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
            ? a.CompareTo(b) : string.Compare(actual, value, StringComparison.OrdinalIgnoreCase);
        return comparison switch { ExecutionPlanComparison.Equal => order == 0, ExecutionPlanComparison.GreaterThan => order > 0, _ => order < 0 };
    }

    /// <summary>The next match after <paramref name="current"/>, wrapping around; the current operator is tried last.</summary>
    public static ExecutionPlanOperator? Next(IReadOnlyList<ExecutionPlanOperator> operators, ExecutionPlanOperator? current, Func<ExecutionPlanOperator, bool> test, bool forward = true)
    {
        int count = operators.Count, start = current is null ? -1 : operators.ToList().IndexOf(current);
        if (start < 0) start = forward ? -1 : count;
        for (int step = 1; step <= count; step++)
        {
            var candidate = operators[((start + (forward ? step : -step)) % count + count) % count];
            if (test(candidate)) return candidate;
        }
        return null;
    }
}

public static class ExecutionPlanBuilder
{
    private const int MaxObjectName = 34;

    /// <summary>Builds every statement of a batch; <paramref name="queryText"/> is used for plans that do not carry their own text.</summary>
    public static IReadOnlyList<ExecutionPlanStatement> Build(IReadOnlyList<QueryPlan> plans, string? queryText = null, CancellationToken token = default)
    {
        var parsed = plans.Select(plan =>
        {
            var root = QueryPlanTree.Parse(plan, token);
            return (Plan: plan, Node: root, Text: ReadQueryText(plan) ?? queryText ?? "", Cost: Number(root, "Total Cost") ?? 0);
        }).ToList();
        double batchCost = parsed.Sum(p => p.Cost);
        return parsed.Select((p, index) => new ExecutionPlanStatement(index + 1, Flatten(p.Text), batchCost > 0 ? 100 * p.Cost / batchCost : 100.0 / parsed.Count, Statement(p.Node, p.Text), p.Plan)).ToList();
    }

    private static string? ReadQueryText(QueryPlan plan)
    {
        using var document = JsonDocument.Parse(plan.Json, new JsonDocumentOptions { MaxDepth = 256 });
        return document.RootElement[0].TryGetProperty("Query Text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    }

    private static string Flatten(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ExecutionPlanOperator Statement(QueryPlanNode root, string text)
    {
        double total = TotalSelfCost(root);
        var properties = new Dictionary<string, string>(root.Properties, StringComparer.Ordinal);
        if (text.Length > 0) properties["Statement"] = text.Length <= 4096 ? text : text[..4096] + " [truncated]";
        return new ExecutionPlanOperator
        {
            Title = StatementKeyword(text), Icon = ExecutionPlanIcon.Statement, IsStatement = true, CostPercent = 0, EstimatedRows = Number(root, "Plan Rows") ?? 0,
            Description = "The statement sent to the server.", Properties = properties,
            Summary = [new("Estimated Subtree Cost", Text(root, "Total Cost")), new("Estimated Number of Rows", Text(root, "Plan Rows"))],
            Details = text.Length > 0 ? [new("Statement", Flatten(text))] : [],
            Children = [Operator(root, total)],
        };
    }

    private static string StatementKeyword(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) i++;
            else if (string.CompareOrdinal(text, i, "--", 0, 2) == 0) { i = text.IndexOf('\n', i); if (i < 0) return "STATEMENT"; }
            else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0) { i = text.IndexOf("*/", i, StringComparison.Ordinal); if (i < 0) return "STATEMENT"; i += 2; }
            else break;
        }
        int end = i;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        string word = text[i..end].ToUpperInvariant();
        return word is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "WITH" or "VALUES" or "TABLE" or "EXECUTE" or "CREATE" ? word : "STATEMENT";
    }

    private static double SelfCost(QueryPlanNode node) => Math.Max(0, (Number(node, "Total Cost") ?? 0) - node.Children.Sum(c => Number(c, "Total Cost") ?? 0));
    private static double TotalSelfCost(QueryPlanNode node) => SelfCost(node) + node.Children.Sum(TotalSelfCost);

    private static ExecutionPlanOperator Operator(QueryPlanNode node, double totalCost)
    {
        string type = node.Properties["Node Type"];
        double self = SelfCost(node), percent = totalCost > 0 ? 100 * self / totalCost : 0;
        double? loops = Number(node, "Actual Loops");
        double? actualRows = loops is { } l && Number(node, "Actual Rows") is { } rows ? rows * l : null;
        double? seconds = loops is { } n && Number(node, "Actual Total Time") is { } milliseconds ? milliseconds * n / 1000 : null;
        double estimated = (Number(node, "Plan Rows") ?? 0) * Math.Max(loops ?? 1, 1);
        string? subtitle = Subtitle(node, type);

        var summary = new List<KeyValuePair<string, string>> { new("Physical Operation", type) };
        void Row(string label, string? value) { if (!string.IsNullOrEmpty(value)) summary.Add(new(label, value)); }
        string Plain(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        Row("Logical Operation", subtitle);
        Row("Actual Number of Rows for All Executions", Plain(actualRows));
        Row("Estimated Number of Rows for All Executions", Plain(estimated));
        Row("Estimated Number of Rows Per Execution", Text(node, "Plan Rows"));
        Row("Number of Executions", Plain(loops));
        Row("Actual Time for All Executions", seconds is { } t ? t.ToString("0.000", CultureInfo.InvariantCulture) + " s" : null);
        Row("Estimated Operator Cost", self.ToString("0.##", CultureInfo.InvariantCulture) + " (" + percent.ToString("0", CultureInfo.InvariantCulture) + "%)");
        Row("Estimated Startup Cost", Text(node, "Startup Cost"));
        Row("Estimated Subtree Cost", Text(node, "Total Cost"));
        Row("Estimated Row Size", Text(node, "Plan Width") is { Length: > 0 } width ? width + " B" : null);
        Row("Shared Blocks Hit", Text(node, "Shared Hit Blocks"));
        Row("Shared Blocks Read", Text(node, "Shared Read Blocks"));
        Row("Workers Launched", Text(node, "Workers Launched"));

        var details = new List<ExecutionPlanDetail>();
        foreach (string name in new[] { "Output", "Index Cond", "Recheck Cond", "Hash Cond", "Merge Cond", "Join Filter", "Filter", "Sort Key", "Group Key" })
            if (node.Properties.TryGetValue(name, out var value)) details.Add(new(name, Readable(value)));

        return new ExecutionPlanOperator
        {
            Title = type, Subtitle = subtitle, ObjectName = ObjectName(node), Icon = IconFor(type), CostPercent = percent, Seconds = seconds, ActualRows = actualRows, EstimatedRows = estimated,
            Description = Describe(type), Summary = summary, Details = details, Properties = node.Properties, Children = node.Children.Select(c => Operator(c, totalCost)).ToList(),
        };
    }

    private static string? Subtitle(QueryPlanNode node, string type)
    {
        if (node.Properties.TryGetValue("Join Type", out var join)) return join + (join.EndsWith("Join", StringComparison.Ordinal) ? "" : " Join");
        if (type == "Aggregate" && node.Properties.TryGetValue("Strategy", out var strategy)) return strategy;
        if (type == "ModifyTable" && node.Properties.TryGetValue("Operation", out var operation)) return operation;
        if (type == "Sort" && node.Properties.TryGetValue("Sort Method", out var method)) return method;
        return null;
    }

    private static string? ObjectName(QueryPlanNode node)
    {
        var p = node.Properties;
        string? name = p.GetValueOrDefault("Relation Name") ?? p.GetValueOrDefault("CTE Name") ?? p.GetValueOrDefault("Function Name");
        if (name is not null && p.TryGetValue("Schema", out var schema)) name = schema + "." + name;
        if (p.TryGetValue("Index Name", out var index)) name = name is null ? index : name + "." + index;
        if (name is null) return null;
        return name.Length <= MaxObjectName ? name : name[..(MaxObjectName - 1)] + "…";
    }

    /// <summary>Lists arrive as raw JSON arrays; shows their items separated by commas.</summary>
    private static string Readable(string value)
    {
        if (!value.StartsWith('[')) return value;
        try
        {
            using var document = JsonDocument.Parse(value);
            return string.Join(", ", document.RootElement.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText()));
        }
        catch (JsonException) { return value; }
    }

    private static string Text(QueryPlanNode node, string name) => node.Properties.GetValueOrDefault(name, "");
    private static double? Number(QueryPlanNode node, string name) =>
        node.Properties.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    private static ExecutionPlanIcon IconFor(string type) => type switch
    {
        "Seq Scan" or "Sample Scan" or "Tid Scan" or "Tid Range Scan" or "Foreign Scan" or "Custom Scan" => ExecutionPlanIcon.TableScan,
        "Index Scan" or "Index Only Scan" or "Bitmap Index Scan" => ExecutionPlanIcon.IndexScan,
        "Bitmap Heap Scan" or "BitmapAnd" or "BitmapOr" => ExecutionPlanIcon.BitmapScan,
        "Nested Loop" or "Hash Join" or "Merge Join" => ExecutionPlanIcon.Join,
        "Sort" or "Incremental Sort" => ExecutionPlanIcon.Sort,
        "Aggregate" or "Group" or "WindowAgg" or "Unique" or "SetOp" => ExecutionPlanIcon.Aggregate,
        "Limit" => ExecutionPlanIcon.Limit,
        "Materialize" or "Memoize" or "Hash" => ExecutionPlanIcon.Materialize,
        "Append" or "Merge Append" or "Recursive Union" => ExecutionPlanIcon.Append,
        "Gather" or "Gather Merge" => ExecutionPlanIcon.Gather,
        "ModifyTable" or "LockRows" => ExecutionPlanIcon.Modify,
        "Subquery Scan" or "CTE Scan" or "Function Scan" or "Values Scan" or "Table Function Scan" or "WorkTable Scan" or "Named Tuplestore Scan" => ExecutionPlanIcon.Subquery,
        _ => ExecutionPlanIcon.Other,
    };

    private static string Describe(string type) => type switch
    {
        "Seq Scan" => "Reads every row of the table in physical order.",
        "Index Scan" => "Finds rows through an index and fetches them from the table.",
        "Index Only Scan" => "Answers from the index alone, visiting the table only for rows not known to be visible.",
        "Bitmap Index Scan" => "Marks the table pages that hold matching rows, using an index.",
        "Bitmap Heap Scan" => "Reads the table pages marked by bitmap index scans.",
        "BitmapAnd" => "Keeps pages present in all input bitmaps.",
        "BitmapOr" => "Keeps pages present in any input bitmap.",
        "Nested Loop" => "For each row of the top input, scans the bottom input for matches.",
        "Hash Join" => "Builds a hash table from the bottom input and probes it with each row of the top input.",
        "Merge Join" => "Joins two inputs already sorted on the join keys.",
        "Hash" => "Builds the in-memory hash table used by a Hash Join.",
        "Sort" => "Orders the rows of its input.",
        "Incremental Sort" => "Orders rows whose leading keys are already sorted.",
        "Aggregate" => "Computes aggregates, optionally per group.",
        "Group" => "Groups an already sorted input.",
        "WindowAgg" => "Computes window functions.",
        "Unique" => "Removes adjacent duplicate rows from a sorted input.",
        "SetOp" => "Performs INTERSECT or EXCEPT.",
        "Limit" => "Stops after the requested number of rows.",
        "Materialize" => "Keeps its input in memory so it can be read repeatedly.",
        "Memoize" => "Caches the results of the inner side of a nested loop by key.",
        "Append" => "Returns the rows of each input in turn.",
        "Merge Append" => "Combines sorted inputs, keeping the order.",
        "Recursive Union" => "Evaluates a recursive common table expression.",
        "Gather" => "Collects rows produced by parallel workers.",
        "Gather Merge" => "Collects sorted rows produced by parallel workers, keeping the order.",
        "ModifyTable" => "Inserts, updates or deletes rows.",
        "LockRows" => "Locks the selected rows (FOR UPDATE, FOR SHARE).",
        "Subquery Scan" => "Reads the output of a subquery.",
        "CTE Scan" => "Reads the output of a common table expression.",
        "Function Scan" => "Reads the rows returned by a function.",
        "Values Scan" => "Reads the rows of a VALUES list.",
        "Result" => "Returns computed rows without reading a table.",
        _ => "PostgreSQL plan operator.",
    };
}
