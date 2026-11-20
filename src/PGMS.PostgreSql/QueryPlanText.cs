using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PGMS.PostgreSql;

/// <summary>The plan as text (PGMS-084, M1119), the PostgreSQL counterpart of Show Execution Plan XML. The JSON is the one EXPLAIN (FORMAT JSON) returned,
/// indented; the text is rebuilt from that JSON in the layout of EXPLAIN (FORMAT TEXT), without asking the server again (an analyzed plan would run
/// the statement a second time). The text keeps the usual details; less common keys stay only in the JSON.</summary>
public static class QueryPlanText
{
    /// <summary>The JSON of the plan, indented.</summary>
    public static string Json(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var document = JsonDocument.Parse(plan.Json, new JsonDocumentOptions { MaxDepth = 256 });
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    /// <summary>The plan in the layout of EXPLAIN (FORMAT TEXT), followed by Planning Time and Execution Time when the plan has them.</summary>
    public static string Text(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var document = JsonDocument.Parse(plan.Json, new JsonDocumentOptions { MaxDepth = 256 });
        var envelope = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 ? document.RootElement[0] : document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("Plan", out var root)) throw new InvalidDataException("Expected one PostgreSQL JSON plan.");
        var text = new StringBuilder();
        Write(text, root, 0);
        if (Number(envelope, "Planning Time") is { } planning) text.Append("Planning Time: ").Append(Format(planning, 3)).AppendLine(" ms");
        if (Number(envelope, "Execution Time") is { } execution) text.Append("Execution Time: ").Append(Format(execution, 3)).AppendLine(" ms");
        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>The first line of a node without its estimates, such as "Index Scan using orders_pkey on orders o". Used by the comparison.</summary>
    public static string NodeName(JsonElement node)
    {
        string type = String(node, "Node Type") ?? "?";
        string name = type switch
        {
            "Aggregate" => (String(node, "Strategy") switch { "Hashed" => "HashAggregate", "Sorted" => "GroupAggregate", "Mixed" => "MixedAggregate", _ => "Aggregate" }),
            "ModifyTable" => String(node, "Operation") ?? type,
            "SetOp" => (String(node, "Strategy") == "Hashed" ? "HashSetOp" : "SetOp") + (String(node, "Command") is { } command ? " " + command : ""),
            _ => type,
        };
        if (String(node, "Join Type") is { } join && join != "Inner" && type is "Nested Loop" or "Hash Join" or "Merge Join")
            name = type == "Nested Loop" ? $"Nested Loop {join} Join" : type.Replace(" Join", $" {join} Join", StringComparison.Ordinal);
        if (String(node, "Partial Mode") is "Partial" or "Finalize") name = String(node, "Partial Mode") + " " + name;
        if (Bool(node, "Parallel Aware")) name = "Parallel " + name;
        if (String(node, "Index Name") is { } index) name += (type == "Bitmap Index Scan" ? " on " : " using ") + index;
        if (String(node, "Relation Name") is { } relation)
        {
            name += " on " + (String(node, "Schema") is { } schema ? schema + "." : "") + relation;
            if (String(node, "Alias") is { } alias && alias != relation) name += " " + alias;
        }
        else if ((String(node, "CTE Name") ?? String(node, "Function Name") ?? String(node, "Table Function Name")) is { } source)
        {
            name += " on " + source;
            if (String(node, "Alias") is { } alias && alias != source) name += " " + alias;
        }
        else if (type is "Subquery Scan" or "Values Scan" && String(node, "Alias") is { } alias) name += " on " + alias;
        return name;
    }

    private static readonly string[] ConditionKeys = ["Hash Cond", "Merge Cond", "Index Cond", "Recheck Cond", "Join Filter", "Filter", "One-Time Filter", "TID Cond", "Cache Key"];

    private static void Write(StringBuilder text, JsonElement node, int depth)
    {
        string arrow = depth == 0 ? "" : new string(' ', 6 * (depth - 1) + 2), details = new string(' ', 6 * depth + 2);
        if (depth > 0 && String(node, "Subplan Name") is { } subplan) text.Append(arrow).AppendLine(subplan);
        text.Append(arrow).Append(depth == 0 ? "" : "->  ").Append(NodeName(node));
        if (Number(node, "Startup Cost") is { } startup && Number(node, "Total Cost") is { } total)
            text.Append("  (cost=").Append(Format(startup, 2)).Append("..").Append(Format(total, 2)).Append(" rows=").Append(Format(Number(node, "Plan Rows") ?? 0, 0)).Append(" width=").Append(Format(Number(node, "Plan Width") ?? 0, 0)).Append(')');
        if (Number(node, "Actual Loops") is { } loops)
        {
            if (loops == 0) text.Append(" (never executed)");
            else
            {
                text.Append(' ').Append(Number(node, "Actual Startup Time") is { } first && Number(node, "Actual Total Time") is { } last ? $"(actual time={Format(first, 3)}..{Format(last, 3)} " : "(actual ");
                text.Append("rows=").Append(Format(Number(node, "Actual Rows") ?? 0, Number(node, "Actual Rows") is { } rows && rows % 1 != 0 ? 2 : 0)).Append(" loops=").Append(Format(loops, 0)).Append(')');
            }
        }
        text.AppendLine();
        if (Strings(node, "Output") is { Count: > 0 } output) Line(text, details, "Output", string.Join(", ", output));
        foreach (string key in ConditionKeys)
        {
            if (String(node, key) is { } condition) Line(text, details, key, condition);
            if (key is "Filter" or "Join Filter" && Number(node, "Rows Removed by " + key) is { } removed) Line(text, details, "Rows Removed by " + key, Format(removed, 0));
        }
        if (Number(node, "Rows Removed by Index Recheck") is { } recheck) Line(text, details, "Rows Removed by Index Recheck", Format(recheck, 0));
        foreach (string key in new[] { "Sort Key", "Presorted Key", "Group Key" })
            if (Strings(node, key) is { Count: > 0 } keys) Line(text, details, key, string.Join(", ", keys));
        if (String(node, "Sort Method") is { } method)
            Line(text, details, "Sort Method", method + (Number(node, "Sort Space Used") is { } used ? $"  {String(node, "Sort Space Type") ?? "Memory"}: {Format(used, 0)}kB" : ""));
        if (Number(node, "Hash Buckets") is { } buckets)
            Line(text, details, "Buckets", Format(buckets, 0) + (Number(node, "Hash Batches") is { } batches ? $"  Batches: {Format(batches, 0)}" : "") + (Number(node, "Peak Memory Usage") is { } memory ? $"  Memory Usage: {Format(memory, 0)}kB" : ""));
        if (Number(node, "Heap Fetches") is { } fetches) Line(text, details, "Heap Fetches", Format(fetches, 0));
        if (Number(node, "Workers Planned") is { } planned) Line(text, details, "Workers Planned", Format(planned, 0));
        if (Number(node, "Workers Launched") is { } launched) Line(text, details, "Workers Launched", Format(launched, 0));
        string buffers = Buffers(node);
        if (buffers.Length > 0) Line(text, details, "Buffers", buffers);
        if (node.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Write(text, child, depth + 1);
    }

    private static string Buffers(JsonElement node)
    {
        var parts = new List<string>();
        foreach (string scope in new[] { "Shared", "Local", "Temp" })
        {
            var counts = new[] { "Hit", "Read", "Dirtied", "Written" }.Select(k => (k, v: Number(node, $"{scope} {k} Blocks"))).Where(p => p.v is > 0).Select(p => $"{p.k.ToLowerInvariant()}={Format(p.v!.Value, 0)}").ToList();
            if (counts.Count > 0) parts.Add(scope.ToLowerInvariant() + " " + string.Join(" ", counts));
        }
        return string.Join(", ", parts);
    }

    private static void Line(StringBuilder text, string indent, string key, string value) => text.Append(indent).Append(key).Append(": ").AppendLine(value);
    internal static string? String(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static double? Number(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static bool Bool(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static IReadOnlyList<string>? Strings(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList() : null;
    internal static string Format(double value, int decimals) => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
