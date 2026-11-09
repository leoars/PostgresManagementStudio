using System.Text;
using System.Text.Json;

namespace PGMS.PostgreSql;

public static class QueryPlanFiles
{
    /// <summary>Reads a plan saved by PGMS or produced by EXPLAIN (FORMAT JSON). Refuses anything else with InvalidDataException; analyzed means the file holds actual rows.</summary>
    public static async Task<QueryPlan> LoadAsync(string path,CancellationToken token=default)
    {
        if(new FileInfo(path).Length>QuerySession.MaximumPlanCharacters*4L)throw new InvalidDataException("The file exceeds the plan display limit.");
        string json=await File.ReadAllTextAsync(path,new UTF8Encoding(false,true),token).ConfigureAwait(false);
        try
        {
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=256,AllowTrailingCommas=false});
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Array || root.GetArrayLength()!=1 || root[0].ValueKind!=JsonValueKind.Object || !root[0].TryGetProperty("Plan",out var top) || top.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("The file is not a PostgreSQL plan in JSON format.");
            var plan=new QueryPlan(json,top.TryGetProperty("Actual Rows",out _));
            _=QueryPlanTree.Parse(plan,token);
            return plan;
        }
        catch(Exception ex)when(ex is JsonException or KeyNotFoundException or InvalidOperationException){throw new InvalidDataException("The file is not a PostgreSQL plan in JSON format.",ex);}
    }

    public static async Task SaveAsync(QueryPlan plan,string path,bool overwrite=false,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(plan);token.ThrowIfCancellationRequested();
        // Validate the bounded source before touching the destination.
        _=QueryPlanTree.Parse(plan,token);
        string destination=Path.GetFullPath(path);
        if(!overwrite && File.Exists(destination))throw new IOException("The destination already exists.");
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
            {
                await using var writer=new StreamWriter(stream,new UTF8Encoding(false,true),65536,leaveOpen:true);
                await writer.WriteAsync(plan.Json.AsMemory(),token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();File.Move(temporary,destination,overwrite);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
