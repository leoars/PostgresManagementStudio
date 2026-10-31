using System.Globalization;
using System.Text.Json;

namespace PGMS.PostgreSql;

public sealed record QueryPlanNode(string Label,IReadOnlyDictionary<string,string> Properties,IReadOnlyList<QueryPlanNode> Children);

public static class QueryPlanTree
{
    public static QueryPlanNode Parse(QueryPlan plan,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(plan);token.ThrowIfCancellationRequested();
        if(plan.Json.Length>QuerySession.MaximumPlanCharacters)throw new InvalidDataException("Plan exceeds display limit.");
        using var document=JsonDocument.Parse(plan.Json,new JsonDocumentOptions{MaxDepth=256});
        var envelope=document.RootElement;
        if(envelope.ValueKind!=JsonValueKind.Array || envelope.GetArrayLength()!=1 || envelope[0].ValueKind!=JsonValueKind.Object || !envelope[0].TryGetProperty("Plan",out var root))throw new InvalidDataException("Expected one PostgreSQL JSON plan.");
        int count=0;return Read(root,0);
        QueryPlanNode Read(JsonElement node,int depth)
        {
            token.ThrowIfCancellationRequested();
            if(depth>64 || ++count>4096)throw new InvalidDataException("Plan exceeds 4096 nodes or 64 levels; no partial tree is displayed.");
            if(node.ValueKind!=JsonValueKind.Object || !node.TryGetProperty("Node Type",out var type) || type.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Plan node has no type.");
            var properties=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var property in node.EnumerateObject())
            {
                if(property.Name=="Plans")continue;
                string value=property.Value.ValueKind==JsonValueKind.String?property.Value.GetString()!:property.Value.GetRawText();
                properties[property.Name]=Shorten(value);
            }
            var children=new List<QueryPlanNode>();
            if(node.TryGetProperty("Plans",out var plans))
            {
                if(plans.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Invalid child plan list.");
                foreach(var child in plans.EnumerateArray())children.Add(Read(child,depth+1));
            }
            string label=Shorten(type.GetString()!);
            if(properties.TryGetValue("Relation Name",out var relation))label+=" · "+relation;
            if(properties.TryGetValue("Plan Rows",out var rows))label+=" · estimated rows "+rows;
            if(properties.TryGetValue("Total Cost",out var cost))label+=" · cost "+cost;
            if(properties.TryGetValue("Actual Rows",out var actual) && properties.TryGetValue("Actual Loops",out var loops))label+=" · actual rows "+actual+"/loop × "+loops+" loops";
            if(label.Length>512)
            {
                const string marker=" [truncated]";int length=512-marker.Length;
                if(char.IsHighSurrogate(label[length-1]))length--;
                label=label[..length]+marker;
            }
            return new(label,properties,children);
        }
    }
    private static string Shorten(string text)
    {
        if(text.Length<=4096)return text;
        int length=char.IsHighSurrogate(text[4095])?4095:4096;return text[..length]+" [truncated]";
    }
}
