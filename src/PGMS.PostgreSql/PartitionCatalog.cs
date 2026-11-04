using System.Data;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record PartitionParent(uint DatabaseOid,uint NamespaceOid,uint Oid,string Schema,string Name,PartitionStrategy Strategy,int KeyCount,string Definition,string Signature);
public sealed record PartitionChild(uint Oid,uint NamespaceOid,string Schema,string Name,string Kind,string Bound,bool DetachPending);
public sealed record PartitionInspection(PartitionParent Parent,IReadOnlyList<PartitionChild> Children,IReadOnlyList<string> Columns);

public sealed class PartitionCatalog(string connectionString)
{
    public async Task<PartitionInspection> InspectAsync(string schema,string name,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ObjectSql.Relation(schema,name);
        await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var ro=new NpgsqlCommand("SET TRANSACTION READ ONLY;SET LOCAL search_path=pg_catalog",c,tx))await ro.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var parent=await ReadParent(c,tx,schema,name,token).ConfigureAwait(false);
        await using var cmd=new NpgsqlCommand("""
            SELECT c.oid,n.oid,n.nspname,c.relname,c.relkind::text,pg_catalog.pg_get_expr(c.relpartbound,c.oid),i.inhdetachpending
            FROM pg_catalog.pg_inherits i JOIN pg_catalog.pg_class c ON c.oid=i.inhrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE i.inhparent=@oid ORDER BY c.oid LIMIT 1001
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,parent.Oid);await using var reader=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        var children=new List<PartitionChild>();while(await reader.ReadAsync(token).ConfigureAwait(false))
            children.Add(new(reader.GetFieldValue<uint>(0),reader.GetFieldValue<uint>(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetBoolean(6)));
        if(children.Count>1000)throw new InvalidOperationException("More than 1000 direct partitions; refusing an incomplete inspection.");
        await reader.DisposeAsync().ConfigureAwait(false);
        await using var columns=new NpgsqlCommand("SELECT a.attname FROM pg_catalog.pg_attribute a WHERE a.attrelid=@oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum",c,tx){CommandTimeout=15};
        columns.Parameters.AddWithValue("oid",NpgsqlTypes.NpgsqlDbType.Oid,parent.Oid);
        var names=new List<string>();await using(var r=await columns.ExecuteReaderAsync(token).ConfigureAwait(false))while(await r.ReadAsync(token).ConfigureAwait(false))names.Add(r.GetString(0));
        return new(parent,children.AsReadOnly(),names.AsReadOnly());
    }
    internal static async Task<PartitionParent> ReadParent(NpgsqlConnection c,NpgsqlTransaction tx,string schema,string name,CancellationToken token)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT d.oid,n.oid,c.oid,p.partstrat::text,p.partnatts,pg_catalog.pg_get_partkeydef(c.oid),
              jsonb_build_array(c.relowner,c.relpersistence,p.partattrs::text,p.partclass::text,p.partcollation::text,p.partexprs::text,
                (SELECT jsonb_agg(jsonb_build_array(a.attnum,a.attname,a.atttypid,a.atttypmod,a.attcollation,a.attnotnull,a.attidentity,a.attgenerated) ORDER BY a.attnum)
                 FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped))::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_partitioned_table p ON p.partrelid=c.oid
            JOIN pg_catalog.pg_database d ON d.datname=current_database()
            WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='p' AND c.relpersistence='p'
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("schema",schema);cmd.Parameters.AddWithValue("name",name);
        await using var r=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await r.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Permanent partitioned parent not found. Inspect again.");
        var strategy=r.GetString(3) switch{"r"=>PartitionStrategy.Range,"l"=>PartitionStrategy.List,"h"=>PartitionStrategy.Hash,_=>throw new InvalidOperationException("Unsupported partition strategy.")};
        return new(r.GetFieldValue<uint>(0),r.GetFieldValue<uint>(1),r.GetFieldValue<uint>(2),schema,name,strategy,r.GetInt16(4),r.GetString(5),r.GetString(6));
    }
    internal static async Task Guard(NpgsqlConnection c,NpgsqlTransaction tx,PartitionParent expected,CancellationToken token)
    {
        await using(var cmd=new NpgsqlCommand($"SET LOCAL search_path=pg_catalog;SET LOCAL DateStyle='ISO, YMD';SET LOCAL TimeZone='UTC';LOCK TABLE ONLY {ObjectSql.Relation(expected.Schema,expected.Name)} IN ACCESS EXCLUSIVE MODE",c,tx){CommandTimeout=30})
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await Verify(c,tx,expected,token).ConfigureAwait(false);
    }
    internal static async Task Verify(NpgsqlConnection c,NpgsqlTransaction tx,PartitionParent expected,CancellationToken token)
    {
        if(await ReadParent(c,tx,expected.Schema,expected.Name,token).ConfigureAwait(false)!=expected)
            throw new InvalidOperationException("Partition parent identity or definition changed. Inspect again before applying.");
    }
}

public sealed record CreatePartitionEdit(PartitionParent Parent,string Schema,string Name,PartitionBounds Bounds):IAdministrativeEdit
{
    /// <summary>When set, the new partition is itself partitioned (a sub-partition level) and starts without partitions.</summary>
    public TablePartitioning? Partitioning { get; init; }
    public string ToSql()=> $"CREATE TABLE {ObjectSql.Relation(Schema,Name)} PARTITION OF {ObjectSql.Relation(Parent.Schema,Parent.Name)} {Bounds.ToSql(Parent.Strategy,Parent.KeyCount)}{(Partitioning is null?"":" "+Partitioning.KeySql())};";
    public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)=>PartitionCatalog.Guard(c,tx,Parent,token);
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await PartitionCatalog.Verify(c,tx,Parent,token).ConfigureAwait(false);
        await using var cmd=new NpgsqlCommand("""
            SELECT c.relkind::text,p.partstrat::text,
              (SELECT array_agg(a.attname::text ORDER BY k.o) FROM unnest(p.partattrs::int2[]) WITH ORDINALITY k(n,o)
                 JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.n),p.partexprs IS NULL
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_inherits i ON i.inhrelid=c.oid LEFT JOIN pg_catalog.pg_partitioned_table p ON p.partrelid=c.oid
            WHERE n.nspname=@schema AND c.relname=@name AND i.inhparent=@parent AND c.relispartition AND NOT i.inhdetachpending
            """,c,tx);
        cmd.Parameters.AddWithValue("schema",Schema);cmd.Parameters.AddWithValue("name",Name);cmd.Parameters.AddWithValue("parent",NpgsqlTypes.NpgsqlDbType.Oid,Parent.Oid);
        await using var r=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await r.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Created partition link could not be verified.");
        bool matches=Partitioning is null?r.GetString(0)=="r"
            :r.GetString(0)=="p" && r.GetString(1)==Partitioning.Strategy switch{PartitionStrategy.Range=>"r",PartitionStrategy.List=>"l",_=>"h"}
              && !r.IsDBNull(2) && r.GetFieldValue<string[]>(2).SequenceEqual(Partitioning.Columns) && r.GetBoolean(3);
        if(!matches)throw new InvalidOperationException("Created partition kind or sub-partition key could not be verified.");
    }
}
