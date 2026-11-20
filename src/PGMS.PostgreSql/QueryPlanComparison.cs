using System.Text.Json;

namespace PGMS.PostgreSql;

/// <summary>One line of the differences between two plans: a statement-level property or an operator. <see cref="Difference"/> is empty when both agree.</summary>
public sealed record PlanDifference(string Area, string Item, string First, string Second, string Difference);

/// <summary>Compare Showplan for PostgreSQL plans (PGMS-084, M1119). SSMS 22.11 shows two plans one above the other and shades similar regions; here
/// the two plans are shown side by side and the differences are listed: statement totals first, then the operators of both trees in display order,
/// matched by their name and depth (longest common subsequence), so an operator only in one plan, or the same operator with other estimates or
/// actual values, stands out.</summary>
public static class QueryPlanComparison
{
    public const string Same = "", OnlyFirst = "Only in plan 1", OnlySecond = "Only in plan 2", Changed = "Different values";

    public static IReadOnlyList<PlanDifference> Compare(QueryPlan first, QueryPlan second)
    {
        ArgumentNullException.ThrowIfNull(first); ArgumentNullException.ThrowIfNull(second);
        using var a = JsonDocument.Parse(first.Json, new JsonDocumentOptions { MaxDepth = 256 });
        using var b = JsonDocument.Parse(second.Json, new JsonDocumentOptions { MaxDepth = 256 });
        var (envelopeA, rootA) = Root(a); var (envelopeB, rootB) = Root(b);
        var result = new List<PlanDifference>();
        var nodesA = Flatten(rootA); var nodesB = Flatten(rootB);
        void Total(string item, string? x, string? y) { if (x is null && y is null) return; result.Add(new("Statement", item, x ?? "", y ?? "", x == y ? Same : Changed)); }
        string? Cost(JsonElement n) => QueryPlanText.Number(n, "Startup Cost") is { } s && QueryPlanText.Number(n, "Total Cost") is { } t ? $"{QueryPlanText.Format(s, 2)}..{QueryPlanText.Format(t, 2)}" : null;
        string? Value(JsonElement n, string key, int decimals) => QueryPlanText.Number(n, key) is { } v ? QueryPlanText.Format(v, decimals) : null;
        Total("Total cost", Cost(rootA), Cost(rootB));
        Total("Estimated rows", Value(rootA, "Plan Rows", 0), Value(rootB, "Plan Rows", 0));
        Total("Actual rows", Value(rootA, "Actual Rows", 0), Value(rootB, "Actual Rows", 0));
        Total("Actual total time (ms)", Value(rootA, "Actual Total Time", 3), Value(rootB, "Actual Total Time", 3));
        Total("Planning time (ms)", Value(envelopeA, "Planning Time", 3), Value(envelopeB, "Planning Time", 3));
        Total("Execution time (ms)", Value(envelopeA, "Execution Time", 3), Value(envelopeB, "Execution Time", 3));
        Total("Operators", nodesA.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), nodesB.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Longest common subsequence of the operators, by name and depth.
        int n = nodesA.Count, m = nodesB.Count;
        var table = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                table[i, j] = nodesA[i].Key == nodesB[j].Key ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        string Values(JsonElement node) =>
            $"cost={Cost(node) ?? "?"} rows={Value(node, "Plan Rows", 0) ?? "?"}" + (Value(node, "Actual Rows", 0) is { } actual ? $" actual rows={actual} loops={Value(node, "Actual Loops", 0)}" : "");
        string Name((int Depth, string Name, string Key, JsonElement Node) entry) => new string(' ', 2 * entry.Depth) + entry.Name;
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && nodesA[x].Key == nodesB[y].Key)
            {
                string va = Values(nodesA[x].Node), vb = Values(nodesB[y].Node);
                result.Add(new("Operator", Name(nodesA[x]), va, vb, va == vb ? Same : Changed));
                x++; y++;
            }
            else if (y >= m || x < n && table[x + 1, y] >= table[x, y + 1]) { result.Add(new("Operator", Name(nodesA[x]), Values(nodesA[x].Node), "", OnlyFirst)); x++; }
            else { result.Add(new("Operator", Name(nodesB[y]), "", Values(nodesB[y].Node), OnlySecond)); y++; }
        }
        return result;
    }

    private static (JsonElement Envelope, JsonElement Root) Root(JsonDocument document)
    {
        var envelope = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 ? document.RootElement[0] : document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("Plan", out var root)) throw new InvalidDataException("Expected one PostgreSQL JSON plan.");
        return (envelope, root);
    }

    private static List<(int Depth, string Name, string Key, JsonElement Node)> Flatten(JsonElement root)
    {
        var result = new List<(int, string, string, JsonElement)>();
        void Visit(JsonElement node, int depth)
        {
            if (result.Count >= 4096 || depth > 64) throw new InvalidDataException("Plan exceeds 4096 nodes or 64 levels.");
            string name = QueryPlanText.NodeName(node);
            result.Add((depth, name, depth + "|" + name, node));
            if (node.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray()) Visit(child, depth + 1);
        }
        Visit(root, 0);
        return result;
    }
}
