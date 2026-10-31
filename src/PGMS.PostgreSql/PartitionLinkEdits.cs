using System.Data;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record PartitionAttachCandidate(uint DatabaseOid,uint NamespaceOid,uint Oid,string Schema,string Name,string Signature);

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
              jsonb_build_array(c.relowner,c.relpersistence,
                (SELECT jsonb_agg(jsonb_build_array(a.attnum,a.attname,a.atttypid,a.atttypmod,a.attcollation,a.attnotnull,a.attidentity,a.attgenerated) ORDER BY a.attnum)
                 FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped))::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_database d ON d.datname=current_database()
            WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='r' AND c.relpersistence='p' AND NOT c.relispartition
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("schema",schema);cmd.Parameters.AddWithValue("name",name);
        await using var r=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await r.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Choose an ordinary permanent table without inheritance or partition links.");
        return new(r.GetFieldValue<uint>(0),r.GetFieldValue<uint>(1),r.GetFieldValue<uint>(2),schema,name,r.GetString(3));
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
        if(Child.DetachPending || Child.Kind!="r")throw new ArgumentException("Only ordinary attached partitions without pending detach are supported.");
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
            SELECT c.oid=@oid AND n.oid=@ns AND c.relkind='r' AND c.relpersistence='p' AND c.relispartition=@attached
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
