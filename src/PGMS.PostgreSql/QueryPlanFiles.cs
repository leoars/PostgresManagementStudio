using System.Text;
using System.Text.Json;

namespace PGMS.PostgreSql;

public static class QueryPlanFiles
{
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
