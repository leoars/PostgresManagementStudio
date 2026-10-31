using System.Text.Json;

namespace PGMS.PostgreSql;

public sealed record QueryPlanTimings(double? PlanningMilliseconds,double? ExecutionMilliseconds)
{
    public static QueryPlanTimings Read(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if(plan.Json.Length>QuerySession.MaximumPlanCharacters)throw new InvalidDataException("Plan exceeds display limit.");
        using var json=JsonDocument.Parse(plan.Json,new JsonDocumentOptions{MaxDepth=256});
        if(json.RootElement.ValueKind!=JsonValueKind.Array || json.RootElement.GetArrayLength()!=1 || json.RootElement[0].ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Expected one plan.");
        var root=json.RootElement[0];return new(ReadNumber("Planning Time"),ReadNumber("Execution Time"));
        double? ReadNumber(string name)
        {
            if(!root.TryGetProperty(name,out var field))return null;
            if(field.ValueKind!=JsonValueKind.Number || !field.TryGetDouble(out double value) || !double.IsFinite(value) || value<0)throw new InvalidDataException("Invalid server plan timing: "+name);
            return value;
        }
    }
}
