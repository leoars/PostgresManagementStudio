using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum IndexMaintenanceOperation { DropConcurrently, Reindex, ReindexConcurrently }
public sealed record MaintenanceIndexSnapshot(ExpressionIndexTarget Table, uint Oid, uint OwnerOid, string Name,
    string Definition, bool Valid, bool Ready, bool Live, bool SupportsConstraint, bool Exclusion)
{
    internal bool SameIdentity(MaintenanceIndexSnapshot other) => Table.Matches(other.Table) && Oid==other.Oid &&
        OwnerOid==other.OwnerOid && Name==other.Name && Definition==other.Definition &&
        SupportsConstraint==other.SupportsConstraint && Exclusion==other.Exclusion;
}

public sealed class IndexMaintenanceCatalog(string connectionString)
{
    public async Task<MaintenanceIndexSnapshot> InspectAsync(string schema,string table,string index,CancellationToken token=default)
    {
        await using var connection=new NpgsqlConnection(connectionString);await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var command=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,transaction))await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await Read(connection,transaction,schema,table,index,token).ConfigureAwait(false);
    }
    internal static async Task<MaintenanceIndexSnapshot> Read(NpgsqlConnection connection,NpgsqlTransaction transaction,string schema,string table,string index,CancellationToken token)
    {
        SqlIdentifier.Quote(index);
        var target=await ExpressionIndexCatalog.Read(connection,transaction,schema,table,token).ConfigureAwait(false);
        await using var command=new NpgsqlCommand("""
            SELECT c.oid,c.relowner,pg_catalog.pg_get_indexdef(c.oid),i.indisvalid,i.indisready,i.indislive,
                   EXISTS(SELECT 1 FROM pg_catalog.pg_constraint x WHERE x.conindid=c.oid),i.indisexclusion
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_index i ON i.indexrelid=c.oid
            WHERE c.relnamespace=@namespace AND c.relname=@name AND i.indrelid=@table
              AND c.relkind='i' AND c.relpersistence='p' AND NOT c.relispartition
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits h WHERE h.inhrelid=c.oid OR h.inhparent=c.oid)
            """,connection,transaction){CommandTimeout=15};
        command.Parameters.AddWithValue("namespace",NpgsqlDbType.Oid,target.NamespaceOid);
        command.Parameters.AddWithValue("name",index);command.Parameters.AddWithValue("table",NpgsqlDbType.Oid,target.Oid);
        await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Inspect an ordinary permanent index on the selected table, without partitions.");
        return new(target,reader.GetFieldValue<uint>(0),reader.GetFieldValue<uint>(1),index,reader.GetString(2),
            reader.GetBoolean(3),reader.GetBoolean(4),reader.GetBoolean(5),reader.GetBoolean(6),reader.GetBoolean(7));
    }
}

public sealed class ReindexEdit(MaintenanceIndexSnapshot expected):IAdministrativeEdit
{
    public string ToSql()=>"REINDEX INDEX "+ObjectSql.Relation(expected.Table.Schema,expected.Name)+";";
    public async Task ValidateAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken token=default)
    {
        await using(var command=new NpgsqlCommand("LOCK TABLE ONLY "+ObjectSql.Relation(expected.Table.Schema,expected.Table.Name)+" IN SHARE MODE",connection,transaction){CommandTimeout=30})
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var actual=await IndexMaintenanceCatalog.Read(connection,transaction,expected.Table.Schema,expected.Table.Name,expected.Name,token).ConfigureAwait(false);
        if(!expected.SameIdentity(actual))throw new InvalidOperationException("Index identity or definition changed. Inspect again.");
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken token=default)
    {
        var actual=await IndexMaintenanceCatalog.Read(connection,transaction,expected.Table.Schema,expected.Table.Name,expected.Name,token).ConfigureAwait(false);
        if(!expected.SameIdentity(actual) || !actual.Valid || !actual.Ready || !actual.Live)
            throw new InvalidOperationException("Rebuilt index could not be verified on the original target.");
    }
}

public sealed class ConcurrentIndexMaintenanceService(string connectionString,IConcurrentIndexJournal journal)
{
    public static string ToSql(MaintenanceIndexSnapshot expected,IndexMaintenanceOperation operation)=>operation switch
    {
        IndexMaintenanceOperation.DropConcurrently when !expected.SupportsConstraint=>"DROP INDEX CONCURRENTLY "+ObjectSql.Relation(expected.Table.Schema,expected.Name)+" RESTRICT;",
        IndexMaintenanceOperation.ReindexConcurrently when !expected.Exclusion=>"REINDEX INDEX CONCURRENTLY "+ObjectSql.Relation(expected.Table.Schema,expected.Name)+";",
        _=>throw new ArgumentException("Choose a supported concurrent operation. Constraint indexes cannot be dropped; exclusion indexes cannot be rebuilt concurrently.")
    };
    public async Task<ConcurrentIndexResult> ApplyAsync(MaintenanceIndexSnapshot expected,IndexMaintenanceOperation operation,bool acceptNominalResolution,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(journal);token.ThrowIfCancellationRequested();
        if(!acceptNominalResolution || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Review nominal resolution and execute outside an ambient transaction.");
        string sql=ToSql(expected,operation);
        async Task Verify()
        {
            var actual=await new IndexMaintenanceCatalog(connectionString).InspectAsync(expected.Table.Schema,expected.Table.Name,expected.Name,token).ConfigureAwait(false);
            if(!expected.SameIdentity(actual) || expected.Valid!=actual.Valid || expected.Ready!=actual.Ready || expected.Live!=actual.Live)
                throw new InvalidOperationException("Index identity, definition or state changed. Inspect again.");
        }
        await Verify().ConfigureAwait(false);
        var target=expected.Table;
        var settings=new NpgsqlConnectionStringBuilder(connectionString){Enlist=false,Multiplexing=false,Pooling=false};
        await using var connection=new NpgsqlConnection(settings.ConnectionString);await connection.OpenAsync(token).ConfigureAwait(false);
        DateTime started=await ConcurrentIndexService.ReadWorkerStart(connection,token).ConfigureAwait(false);
        var receipt=new ConcurrentIndexReceipt(Guid.NewGuid(),target.DatabaseOid,target.NamespaceOid,target.Oid,target.Schema,target.Name,expected.Name,sql,DateTimeOffset.UtcNow)
        {OwnerOid=target.OwnerOid,ColumnSignature=ConcurrentIndexReceipt.Signature(target),Operation=operation.ToString(),OriginalIndexOid=expected.Oid,WorkerPid=connection.ProcessID,WorkerStarted=started};
        await journal.RecordAsync(receipt,token).ConfigureAwait(false);await Verify().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await using var command=new NpgsqlCommand(sql,connection){CommandTimeout=0};
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            using var verification=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var observation=await new ConcurrentIndexService(connectionString,journal).InspectAsync(target,expected.Name,verification.Token).ConfigureAwait(false);
            if(operation==IndexMaintenanceOperation.DropConcurrently)
            {
                if(observation.Kind!=ConcurrentIndexObservationKind.Absent)throw new InvalidOperationException("Dropped index absence could not be verified.");
            }
            else if(observation.Kind!=ConcurrentIndexObservationKind.Valid || observation.Definition!=expected.Definition)
                throw new InvalidOperationException("Concurrent rebuild definition or validity could not be verified.");
            return new(receipt,observation);
        }
        catch(Exception error){throw new ConcurrentIndexUncertainException(receipt,error);}
    }
}
