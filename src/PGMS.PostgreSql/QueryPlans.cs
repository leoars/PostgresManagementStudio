using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record QueryPlan(string Json,bool Analyzed)
{
    /// <summary>Planned with GENERIC_PLAN: $n parameters stay unbound, so the plan cannot use their values.</summary>
    public bool Generic{get;init;}
}

public sealed partial class QuerySession
{
    public const int MaximumPlanCharacters=1024*1024;
    public Task<QueryPlan> ExplainAsync(string sql,QueryExecutionOptions? options=null,CancellationToken token=default)
        => ReadPlanAsync(sql,false,false,options ?? new(),token);
    /// <summary>Estimated plan for text with $1, $2… placeholders, such as pg_stat_statements' normalised SQL (EXPLAIN GENERIC_PLAN, PostgreSQL 16+). Does not execute.</summary>
    public Task<QueryPlan> ExplainGenericAsync(string sql,QueryExecutionOptions? options=null,CancellationToken token=default)
        => ReadPlanAsync(sql,false,true,options ?? new(),token);
    /// <summary>Executes the statement. Does not add a transaction or roll back its effects.</summary>
    public Task<QueryPlan> AnalyzeAsync(string sql,QueryExecutionOptions? options=null,CancellationToken token=default)
        => ReadPlanAsync(sql,true,false,options ?? new(),token);

    private async Task<QueryPlan> ReadPlanAsync(string sql,bool analyze,bool generic,QueryExecutionOptions options,CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if(sql.Length>16*1024*1024)throw new ArgumentException("Plan SQL exceeds 16 Mi characters.",nameof(sql));
        string statement=sql.TrimEnd();
        if(statement.EndsWith(';'))statement=statement[..^1];
        if(statement.Contains(';'))throw new ArgumentException("Explain accepts one statement without embedded semicolons. Select a single statement without its terminator.",nameof(sql));
        if(!generic)QueryPlaceholders.ThrowIfUnbound(statement);
        await execution.WaitAsync(token).ConfigureAwait(false);
        string? genericJson=null;bool genericTooLarge=false;
        void Notice(object sender,NpgsqlNoticeEventArgs args)
        {
            // Npgsql redacts notice Detail by default, so the plan travels in the message after a marker.
            if(generic && args.Notice.MessageText.StartsWith(GenericPlanMarker,StringComparison.Ordinal)){if(args.Notice.MessageText.Length-GenericPlanMarker.Length>MaximumPlanCharacters)genericTooLarge=true;else genericJson=args.Notice.MessageText[GenericPlanMarker.Length..];return;}
            options.Notices?.Add(args.Notice.InvariantSeverity,args.Notice.SqlState,args.Notice.MessageText);
        }
        if(options.Notices is not null || generic)connection.Notice+=Notice;
        try
        {
            await DisarmPlanCaptureAsync().ConfigureAwait(false);
            string result;
            if(generic)
            {
                // A Parse message makes every $n a parameter of the message itself, and binding placeholder values turns the
                // plan into a custom one. Dynamic SQL inside DO is analysed without outer parameters, so GENERIC_PLAN keeps $n
                // free; INFO is always delivered to the client. DO leaves no session state behind and EXPLAIN does not execute.
                string tag="$pgms_"+Guid.NewGuid().ToString("N")+"$";
                string literal="E'"+("EXPLAIN (GENERIC_PLAN TRUE, FORMAT JSON) "+statement).Replace("\\","\\\\").Replace("'","\\'")+"'";
                string block=$"DO {tag} DECLARE plan text; BEGIN EXECUTE {literal} INTO plan; RAISE INFO USING MESSAGE = '{GenericPlanMarker}' || plan; END {tag}";
                await using var command=new NpgsqlCommand(block,connection){CommandTimeout=options.CommandTimeoutSeconds};
                try{await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
                catch(OperationCanceledException){try{await Task.Run(command.Cancel).ConfigureAwait(false);}catch{/* Keep original failure. */}throw;}
                if(genericTooLarge)throw new InvalidDataException("Plan exceeds 1 Mi characters; no partial plan is displayed.");
                result=genericJson ?? throw new InvalidDataException("Server returned no plan.");
            }
            else
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
                result=json.ToString();
                }
                catch
                {
                    try{await Task.Run(command.Cancel).ConfigureAwait(false);}catch{/* Keep original failure. */}
                    throw;
                }
            }
            using var parsed=JsonDocument.Parse(result,new JsonDocumentOptions{MaxDepth=256});
            if(parsed.RootElement.ValueKind!=JsonValueKind.Array || parsed.RootElement.GetArrayLength()!=1 || !parsed.RootElement[0].TryGetProperty("Plan",out var root) || root.ValueKind!=JsonValueKind.Object)
                throw new InvalidDataException("Server returned an unsupported plan structure.");
            return new(result,analyze){Generic=generic};
        }
        finally{if(options.Notices is not null || generic)connection.Notice-=Notice;execution.Release();}
    }

    private const string GenericPlanMarker="pgms-generic-plan:";
}