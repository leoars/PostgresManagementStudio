using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record QueryPlan(string Json,bool Analyzed);

public sealed partial class QuerySession
{
    public const int MaximumPlanCharacters=1024*1024;
    public Task<QueryPlan> ExplainAsync(string sql,QueryExecutionOptions? options=null,CancellationToken token=default)
        => ReadPlanAsync(sql,false,options ?? new(),token);
    /// <summary>Executes the statement. Does not add a transaction or roll back its effects.</summary>
    public Task<QueryPlan> AnalyzeAsync(string sql,QueryExecutionOptions? options=null,CancellationToken token=default)
        => ReadPlanAsync(sql,true,options ?? new(),token);

    private async Task<QueryPlan> ReadPlanAsync(string sql,bool analyze,QueryExecutionOptions options,CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if(sql.Length>16*1024*1024)throw new ArgumentException("Plan SQL exceeds 16 Mi characters.",nameof(sql));
        string statement=sql.TrimEnd();
        if(statement.EndsWith(';'))statement=statement[..^1];
        if(statement.Contains(';'))throw new ArgumentException("Explain accepts one statement without embedded semicolons. Select a single statement without its terminator.",nameof(sql));
        await execution.WaitAsync(token).ConfigureAwait(false);
        void Notice(object sender,NpgsqlNoticeEventArgs args)=>options.Notices?.Add(args.Notice.InvariantSeverity,args.Notice.SqlState,args.Notice.MessageText);
        if(options.Notices is not null)connection.Notice+=Notice;
        try
        {
            string prefix=analyze?"EXPLAIN (ANALYZE TRUE, BUFFERS TRUE, FORMAT JSON) ":"EXPLAIN (ANALYZE FALSE, FORMAT JSON) ";
            await using var command=new NpgsqlCommand(prefix+statement,connection){CommandTimeout=options.CommandTimeoutSeconds,AllResultTypesAreUnknown=true};
            await using var reader=await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,token).ConfigureAwait(false);
            try
            {
            if(reader.FieldCount!=1 || !await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidDataException("Server returned no plan.");
            using var input=await reader.GetTextReaderAsync(0,token).ConfigureAwait(false);
            var json=new StringBuilder();char[] buffer=new char[8192];
            while(true)
            {
                int count=await input.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,MaximumPlanCharacters-json.Length+1)),token).ConfigureAwait(false);
                if(count==0)break;
                if(json.Length+count>MaximumPlanCharacters)throw new InvalidDataException("Plan exceeds 1 Mi characters; no partial plan is displayed.");
                json.Append(buffer,0,count);
            }
            string result=json.ToString();
            using var parsed=JsonDocument.Parse(result,new JsonDocumentOptions{MaxDepth=256});
            if(parsed.RootElement.ValueKind!=JsonValueKind.Array || parsed.RootElement.GetArrayLength()!=1 || !parsed.RootElement[0].TryGetProperty("Plan",out var root) || root.ValueKind!=JsonValueKind.Object)
                throw new InvalidDataException("Server returned an unsupported plan structure.");
            return new(result,analyze);
            }
            catch
            {
                try{await Task.Run(command.Cancel).ConfigureAwait(false);}catch{/* Keep original failure. */}
                throw;
            }
        }
        finally{if(options.Notices is not null)connection.Notice-=Notice;execution.Release();}
    }
}
