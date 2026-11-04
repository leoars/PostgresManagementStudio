using System.Data;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record PartitionAttachCandidate(uint DatabaseOid,uint NamespaceOid,uint Oid,string Schema,string Name,string Signature,string Kind);

public sealed class PartitionAttachCatalog(string connectionString)
{
    public async Task<PartitionAttachCandidate> InspectAsync(string schema,string name,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ObjectSql.Relation(schema,name);
        await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var cmd=new NpgsqlCommand("SET TRANSACTION READ ONLY",c,tx))await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await Read(c,tx,schema,name,token).ConfigureAwait(false);
    }
    internal static async Task<PartitionAttachCandidate> Read(NpgsqlConnection c,NpgsqlTransaction tx,string schema,string name,CancellationToken token)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT d.oid,n.oid,c.oid,
              jsonb_build_array(c.relowner,c.relpersistence,c.relkind,
                CASE WHEN c.relkind='p' THEN pg_catalog.pg_get_partkeydef(c.oid) END,
                (SELECT jsonb_agg(jsonb_build_array(a.attnum,a.attname,a.atttypid,a.atttypmod,a.attcollation,a.attnotnull,a.attidentity,a.attgenerated) ORDER BY a.attnum)
                 FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped))::text,c.relkind::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_database d ON d.datname=current_database()
            WHERE n.nspname=@schema AND c.relname=@name AND c.relkind IN ('r','p') AND c.relpersistence='p' AND NOT c.relispartition
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR (i.inhparent=c.oid AND c.relkind='r'))
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("schema",schema);cmd.Parameters.AddWithValue("name",name);
        await using var r=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await r.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Choose a permanent ordinary or partitioned table that is not a partition and has no inheritance links.");
        return new(r.GetFieldValue<uint>(0),r.GetFieldValue<uint>(1),r.GetFieldValue<uint>(2),schema,name,r.GetString(3),r.GetString(4));
    }
}

public sealed record AttachPartitionEdit(PartitionParent Parent,PartitionAttachCandidate Child,PartitionBounds Bounds):IAdministrativeEdit
{
    public string ToSql()
    {
        if(Parent.DatabaseOid!=Child.DatabaseOid || Parent.Oid==Child.Oid)throw new ArgumentException("Choose a distinct child in the inspected database.");
        return $"ALTER TABLE {ObjectSql.Relation(Parent.Schema,Parent.Name)} ATTACH PARTITION {ObjectSql.Relation(Child.Schema,Child.Name)} {Bounds.ToSql(Parent.Strategy,Parent.KeyCount)};";
    }
    public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await PartitionCatalog.Guard(c,tx,Parent,token).ConfigureAwait(false);
        await PartitionLinkGuard.Lock(c,tx,Child.Schema,Child.Name,token).ConfigureAwait(false);
        if(await PartitionAttachCatalog.Read(c,tx,Child.Schema,Child.Name,token).ConfigureAwait(false)!=Child)
            throw new InvalidOperationException("Attachment candidate changed. Inspect again.");
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await PartitionCatalog.Verify(c,tx,Parent,token).ConfigureAwait(false);
        await PartitionLinkGuard.Verify(c,tx,Parent.Oid,Child.NamespaceOid,Child.Oid,Child.Schema,Child.Name,true,null,token).ConfigureAwait(false);
    }
}

public sealed record DetachPartitionEdit(PartitionParent Parent,PartitionChild Child):IAdministrativeEdit
{
    public string ToSql()
    {
        if(Child.DetachPending || Child.Kind is not ("r" or "p"))throw new ArgumentException("Only ordinary or partitioned attached partitions without pending detach are supported.");
        return $"ALTER TABLE {ObjectSql.Relation(Parent.Schema,Parent.Name)} DETACH PARTITION {ObjectSql.Relation(Child.Schema,Child.Name)};";
    }
    public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await PartitionCatalog.Guard(c,tx,Parent,token).ConfigureAwait(false);
        await PartitionLinkGuard.Lock(c,tx,Child.Schema,Child.Name,token).ConfigureAwait(false);
        await PartitionLinkGuard.Verify(c,tx,Parent.Oid,Child.NamespaceOid,Child.Oid,Child.Schema,Child.Name,true,Child.Bound,token).ConfigureAwait(false);
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await PartitionCatalog.Verify(c,tx,Parent,token).ConfigureAwait(false);
        await PartitionLinkGuard.Verify(c,tx,Parent.Oid,Child.NamespaceOid,Child.Oid,Child.Schema,Child.Name,false,null,token).ConfigureAwait(false);
    }
}

public enum PartitionDetachState { Attached, DetachPending, Detached, Changed }

public sealed class PartitionDetachUncertainException(PartitionDetachState observed,Exception inner)
    :Exception("Concurrent detach did not confirm completion. Observed state: "+observed+".",inner)
{
    public PartitionDetachState Observed { get; }=observed;
}

/// <summary>Standalone DETACH CONCURRENTLY/FINALIZE use nominal resolution outside a transaction. A pending detach is recorded by the server in pg_inherits, so no local journal is needed.</summary>
public sealed class ConcurrentPartitionDetachService(string connectionString)
{
    public static string ToSql(PartitionParent parent,PartitionChild child)
    {
        if(child.Kind is not ("r" or "p"))throw new ArgumentException("Only ordinary or partitioned partitions are supported.");
        return $"ALTER TABLE {ObjectSql.Relation(parent.Schema,parent.Name)} DETACH PARTITION {ObjectSql.Relation(child.Schema,child.Name)} {(child.DetachPending?"FINALIZE":"CONCURRENTLY")};";
    }
    public async Task<PartitionDetachState> InspectAsync(PartitionParent parent,PartitionChild child,CancellationToken token=default)
    {
        await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var ro=new NpgsqlCommand("SET TRANSACTION READ ONLY;SET LOCAL search_path=pg_catalog",c,tx))await ro.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        try{await PartitionCatalog.Verify(c,tx,parent,token).ConfigureAwait(false);}catch(InvalidOperationException){return PartitionDetachState.Changed;}
        await using var cmd=new NpgsqlCommand("""
            SELECT CASE
              WHEN i.inhparent=@parent AND c.relispartition AND NOT i.inhdetachpending AND pg_catalog.pg_get_expr(c.relpartbound,c.oid)=@bound THEN 0
              WHEN i.inhparent=@parent AND i.inhdetachpending THEN 1
              WHEN i.inhrelid IS NULL AND NOT c.relispartition THEN 2 ELSE 3 END
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_catalog.pg_inherits i ON i.inhrelid=c.oid
            WHERE c.oid=@oid AND n.oid=@ns AND n.nspname=@schema AND c.relname=@name AND c.relkind IN ('r','p')
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("parent",NpgsqlTypes.NpgsqlDbType.Oid,parent.Oid);cmd.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,child.Oid);cmd.Parameters.AddWithValue("ns",NpgsqlTypes.NpgsqlDbType.Oid,child.NamespaceOid);
        cmd.Parameters.AddWithValue("schema",child.Schema);cmd.Parameters.AddWithValue("name",child.Name);cmd.Parameters.AddWithValue("bound",child.Bound);
        return await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) is int state?(PartitionDetachState)state:PartitionDetachState.Changed;
    }
    public async Task<IReadOnlyList<string>> BlockersAsync(PartitionParent parent,PartitionChild child,CancellationToken token=default)
    {
        var blockers=new List<string>();
        var state=await InspectAsync(parent,child,token).ConfigureAwait(false);
        if(state!=(child.DetachPending?PartitionDetachState.DetachPending:PartitionDetachState.Attached))blockers.Add("Partition identity, bound or detach state changed. Inspect again.");
        if(!child.DetachPending)
        {
            await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
            await using var cmd=new NpgsqlCommand("""
                SELECT p.partdefid<>0,EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhparent=p.partrelid AND i.inhdetachpending)
                FROM pg_catalog.pg_partitioned_table p WHERE p.partrelid=@parent
                """,c){CommandTimeout=15};
            cmd.Parameters.AddWithValue("parent",NpgsqlTypes.NpgsqlDbType.Oid,parent.Oid);
            await using var r=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if(await r.ReadAsync(token).ConfigureAwait(false))
            {
                if(r.GetBoolean(0))blockers.Add("PostgreSQL does not detach concurrently while the parent has a DEFAULT partition.");
                if(r.GetBoolean(1))blockers.Add("Another partition of this parent is pending detach. Finalize it first.");
            }
        }
        return blockers.AsReadOnly();
    }
    public async Task<PartitionDetachState> ApplyAsync(PartitionParent parent,PartitionChild child,bool acceptNominalResolution,CancellationToken token=default)
    {
        if(!acceptNominalResolution)throw new InvalidOperationException("Review that concurrent schema/name changes cannot be excluded by this standalone command.");
        if(System.Transactions.Transaction.Current is not null)throw new InvalidOperationException("Concurrent detach cannot run in an ambient transaction.");
        string sql=ToSql(parent,child);token.ThrowIfCancellationRequested();
        var blockers=await BlockersAsync(parent,child,token).ConfigureAwait(false);
        if(blockers.Count>0)throw new InvalidOperationException(string.Join(Environment.NewLine,blockers));
        var settings=new NpgsqlConnectionStringBuilder(connectionString){Enlist=false,Multiplexing=false,Pooling=false};
        await using var connection=new NpgsqlConnection(settings.ConnectionString);await connection.OpenAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Exception? failure=null;
        try{await using var command=new NpgsqlCommand(sql,connection){CommandTimeout=0};await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
        catch(Exception error)when(error is NpgsqlException or OperationCanceledException or TimeoutException or System.IO.IOException){failure=error;}
        // The first phase may have committed before a failure or cancellation. Observe the server state instead of claiming rollback.
        using var verification=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        PartitionDetachState observed;
        try{observed=await InspectAsync(parent,child with{DetachPending=false},verification.Token).ConfigureAwait(false);}
        catch(Exception error)when(error is NpgsqlException or OperationCanceledException or TimeoutException or System.IO.IOException){throw new PartitionDetachUncertainException(PartitionDetachState.Changed,failure??error);}
        if(failure is not null || observed!=PartitionDetachState.Detached)
            throw new PartitionDetachUncertainException(observed,failure??new InvalidOperationException("Detached partition could not be verified."));
        return observed;
    }
}

internal static class PartitionLinkGuard
{
    internal static async Task Lock(NpgsqlConnection c,NpgsqlTransaction tx,string schema,string name,CancellationToken token)
    {
        await using var cmd=new NpgsqlCommand($"LOCK TABLE ONLY {ObjectSql.Relation(schema,name)} IN ACCESS EXCLUSIVE MODE",c,tx){CommandTimeout=30};
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    internal static async Task Verify(NpgsqlConnection c,NpgsqlTransaction tx,uint parent,uint ns,uint oid,string schema,string name,bool attached,string? bound,CancellationToken token)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT c.oid=@oid AND n.oid=@ns AND c.relkind IN ('r','p') AND c.relpersistence='p' AND c.relispartition=@attached
             AND CASE WHEN @attached THEN EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid AND i.inhparent=@parent AND NOT i.inhdetachpending)
               ELSE NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid) END
             AND (@bound::text IS NULL OR pg_catalog.pg_get_expr(c.relpartbound,c.oid)=@bound)
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,oid);cmd.Parameters.AddWithValue("ns",NpgsqlTypes.NpgsqlDbType.Oid,ns);cmd.Parameters.AddWithValue("parent",NpgsqlTypes.NpgsqlDbType.Oid,parent);
        cmd.Parameters.AddWithValue("schema",schema);cmd.Parameters.AddWithValue("name",name);cmd.Parameters.AddWithValue("attached",attached);
        cmd.Parameters.AddWithValue("bound",NpgsqlTypes.NpgsqlDbType.Text,(object?)bound??DBNull.Value);
        if(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)throw new InvalidOperationException("Partition identity, bound or link changed. Inspect again.");
    }
}
