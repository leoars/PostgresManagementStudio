using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record QuerySessionSnapshot(Guid SessionIdentity,int BackendPid,string Database,string SessionUser,string CurrentUser,IReadOnlyDictionary<string,string> Settings,DateTimeOffset CapturedAt)
{
    public int StatementTimeoutMilliseconds{get;init;}
    public int LockTimeoutMilliseconds{get;init;}
}

/// <summary>What closing this session would throw away.</summary>
public sealed record QuerySessionLossRisk(bool OpenTransaction,bool TemporaryTables,bool Aborted=false);

public sealed partial class QuerySession
{
    private Guid sessionIdentity=Guid.NewGuid();
    /// <summary>
    /// Reports an open transaction and temporary tables without changing the session. Npgsql does not expose the
    /// transaction status, so a savepoint probes it: it only succeeds inside a transaction block and is released at once.
    /// </summary>
    public async Task<QuerySessionLossRisk> InspectLossRiskAsync(CancellationToken token=default)
    {
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(!IsConnected)return new(false,false);
            bool open=false;
            try
            {
                await RunAsync("SAVEPOINT pgms_loss_probe").ConfigureAwait(false);
                await RunAsync("RELEASE SAVEPOINT pgms_loss_probe").ConfigureAwait(false);
                open=true;
            }
            catch(PostgresException ex)when(ex.SqlState=="25P01"){}// No transaction block.
            catch(PostgresException ex)when(ex.SqlState=="25P02"){return new(true,false,true);}// Aborted: nothing else can be read.
            await using var command=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c WHERE c.relnamespace OPERATOR(pg_catalog.=) pg_catalog.pg_my_temp_schema())",connection){CommandTimeout=15};
            return new(open,(bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
        }
        finally{execution.Release();}

        async Task RunAsync(string sql)
        {
            await using var probe=new NpgsqlCommand(sql,connection){CommandTimeout=15};
            await probe.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
    /// <summary>Query Options: sets the isolation of later transactions and lock_timeout for this session. Call outside a transaction block, where a later
    /// ROLLBACK would undo them. A negative lock timeout and ServerDefault go back to the values the session started with.</summary>
    public async Task ApplySessionOptionsAsync(QueryIsolationLevel isolation,int lockTimeoutMilliseconds,CancellationToken token=default)
    {
        if(lockTimeoutMilliseconds>86400000)throw new ArgumentOutOfRangeException(nameof(lockTimeoutMilliseconds),"Use up to 86400000 milliseconds.");
        string level=isolation switch
        {
            QueryIsolationLevel.ServerDefault=>"RESET default_transaction_isolation",
            QueryIsolationLevel.ReadCommitted=>"SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL READ COMMITTED",
            QueryIsolationLevel.RepeatableRead=>"SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL REPEATABLE READ",
            QueryIsolationLevel.Serializable=>"SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL SERIALIZABLE",
            _=>throw new ArgumentOutOfRangeException(nameof(isolation))
        };
        string wait=lockTimeoutMilliseconds<0?"RESET lock_timeout":$"SET lock_timeout='{lockTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}ms'";
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var command=new NpgsqlCommand(level+"; "+wait,connection){CommandTimeout=15};
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally{execution.Release();}
    }
    /// <summary>Query ▸ Parse: the server parses and analyzes each statement without running it. Inside a transaction block a savepoint keeps a syntax error from
    /// aborting the transaction.</summary>
    public async Task ParseAsync(string sql,bool insideTransaction,CancellationToken token=default)
    {
        QueryPlaceholders.ThrowIfUnbound(sql);
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(insideTransaction)await RunAsync("SAVEPOINT pgms_parse").ConfigureAwait(false);
            try
            {
                await using var command=new NpgsqlCommand(sql,connection){CommandTimeout=30};
                await command.PrepareAsync(token).ConfigureAwait(false);
                await command.UnprepareAsync(token).ConfigureAwait(false);
            }
            catch(PostgresException)when(insideTransaction)
            {
                await RunAsync("ROLLBACK TO SAVEPOINT pgms_parse").ConfigureAwait(false);
                await RunAsync("RELEASE SAVEPOINT pgms_parse").ConfigureAwait(false);
                throw;
            }
            if(insideTransaction)await RunAsync("RELEASE SAVEPOINT pgms_parse").ConfigureAwait(false);
        }
        finally{execution.Release();}

        async Task RunAsync(string statement)
        {
            await using var step=new NpgsqlCommand(statement,connection){CommandTimeout=15};
            await step.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
    /// <summary>Commits the open transaction block; on a failed transaction the server rolls back instead.</summary>
    public async Task CommitAsync(CancellationToken token=default)
    {
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using var command=new NpgsqlCommand("COMMIT",connection){CommandTimeout=30};
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally{execution.Release();}
    }
    public async Task<QuerySessionSnapshot> InspectSessionAsync(CancellationToken token=default)
    {
        await execution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            const string sql="""
                SELECT pg_catalog.pg_backend_pid()::pg_catalog.text,pg_catalog.current_database()::pg_catalog.text,session_user::pg_catalog.text,current_user::pg_catalog.text,
                  pg_catalog.current_setting('search_path'),pg_catalog.current_setting('transaction_isolation'),pg_catalog.current_setting('transaction_read_only'),
                  pg_catalog.current_setting('statement_timeout'),pg_catalog.current_setting('lock_timeout'),pg_catalog.current_setting('application_name'),pg_catalog.current_schemas(true)::pg_catalog.text,
                  (SELECT setting FROM pg_catalog.pg_settings WHERE name OPERATOR(pg_catalog.=) 'statement_timeout'),
                  (SELECT setting FROM pg_catalog.pg_settings WHERE name OPERATOR(pg_catalog.=) 'lock_timeout')
                """;
            await using var command=new NpgsqlCommand(sql,connection){CommandTimeout=15};
            await using var reader=await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,token).ConfigureAwait(false);
            try
            {
                if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidDataException("Session inspection returned no row.");
                string[] values=new string[13];char[] scratch=new char[4096];
                for(int i=0;i<values.Length;i++)
                {
                    using var text=await reader.GetTextReaderAsync(i,token).ConfigureAwait(false);
                    values[i]=await ReadBoundedAsync(text,4096,scratch,token).ConfigureAwait(false) ?? throw new InvalidDataException("A session field exceeds 4096 characters; snapshot refused.");
                }
                string[] names=["search_path","transaction_isolation","transaction_read_only","statement_timeout","lock_timeout","application_name","effective_schemas"];
                var settings=new Dictionary<string,string>(StringComparer.Ordinal);
                for(int i=0;i<names.Length;i++)settings.Add(names[i],values[i+4]);
                return new(sessionIdentity,int.Parse(values[0],CultureInfo.InvariantCulture),values[1],values[2],values[3],new ReadOnlyDictionary<string,string>(settings),DateTimeOffset.UtcNow)
                {StatementTimeoutMilliseconds=int.Parse(values[11],CultureInfo.InvariantCulture),LockTimeoutMilliseconds=int.Parse(values[12],CultureInfo.InvariantCulture)};
            }
            catch{try{await Task.Run(command.Cancel).ConfigureAwait(false);}catch{}throw;}
        }
        finally{execution.Release();}
    }
}
