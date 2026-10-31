using System.Data;
using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public enum IdentityColumnOperation { AddByDefault,AddAlways,SetByDefault,SetAlways,Restart,Drop,SetIncrement,SetStart,SetBounds,SetCache,SetCycle }
public sealed record IdentitySequenceDefinition(uint Oid,uint NamespaceOid,string Schema,string Name,uint TypeOid,long Start,long Minimum,long Maximum,long Increment,long Cache,bool Cycle);
public sealed record IdentityColumnSnapshot(string Schema,string Table,string Column,uint DatabaseOid,uint TableOid,uint NamespaceOid,short Position,uint TypeOid,string Generation,bool Nullable,string? DefaultExpression,string Generated,IdentitySequenceDefinition? Sequence)
{
    public bool HasIdentity=>Generation is "a" or "d";
}

public sealed class IdentityColumnService(string connectionString)
{
    public async Task<IdentityColumnSnapshot> InspectAsync(string schema,string table,string column,CancellationToken token=default,uint? expectedTableOid=null,short? expectedPosition=null)
    {
        token.ThrowIfCancellationRequested();await using var connection=new NpgsqlConnection(connectionString);await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,transaction))await readOnly.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var snapshot=await ReadAsync(connection,transaction,schema,table,column,token).ConfigureAwait(false);
        if(expectedTableOid is { } oid && oid!=snapshot.TableOid || expectedPosition is { } position && position!=snapshot.Position)throw Conflict();
        return snapshot;
    }
    internal static InvalidOperationException Conflict()=>new("Identity column or sequence configuration changed. Nothing was committed. Inspect the table and column again.");
    internal static async Task<IdentityColumnSnapshot> ReadAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string schema,string table,string column,CancellationToken token)
    {
        _=ObjectSql.Relation(schema,table);_=SqlIdentifier.Quote(column);
        const string sql="""
            SELECT (SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()),c.oid,n.oid,a.attnum,a.atttypid,a.attidentity::text,a.attnotnull,
                pg_catalog.pg_get_expr(ad.adbin,ad.adrelid),a.attgenerated::text,s.seqrelid,sn.oid,sn.nspname,sc.relname,s.seqtypid,s.seqstart,s.seqmin,s.seqmax,s.seqincrement,s.seqcache,s.seqcycle
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid=c.oid AND ad.adnum=a.attnum
            LEFT JOIN pg_catalog.pg_depend d ON d.refclassid='pg_catalog.pg_class'::regclass AND d.refobjid=c.oid AND d.refobjsubid=a.attnum AND d.classid='pg_catalog.pg_class'::regclass AND d.deptype='i'
            LEFT JOIN pg_catalog.pg_sequence s ON s.seqrelid=d.objid
            LEFT JOIN pg_catalog.pg_class sc ON sc.oid=s.seqrelid LEFT JOIN pg_catalog.pg_namespace sn ON sn.oid=sc.relnamespace
            WHERE n.nspname=@schema AND c.relname=@table AND a.attname=@column AND c.relkind='r' AND NOT c.relispartition AND c.relpersistence<>'t'
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
            """;
        await using var command=new NpgsqlCommand(sql,connection,transaction){CommandTimeout=15};command.Parameters.AddWithValue("schema",schema);command.Parameters.AddWithValue("table",table);command.Parameters.AddWithValue("column",column);
        await using var row=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await row.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Identity inspection requires an existing column in an ordinary permanent table.");
        IdentitySequenceDefinition? sequence=row.IsDBNull(9)?null:new(row.GetFieldValue<uint>(9),row.GetFieldValue<uint>(10),row.GetString(11),row.GetString(12),row.GetFieldValue<uint>(13),row.GetInt64(14),row.GetInt64(15),row.GetInt64(16),row.GetInt64(17),row.GetInt64(18),row.GetBoolean(19));
        var result=new IdentityColumnSnapshot(schema,table,column,row.GetFieldValue<uint>(0),row.GetFieldValue<uint>(1),row.GetFieldValue<uint>(2),row.GetInt16(3),row.GetFieldValue<uint>(4),row.GetString(5),!row.GetBoolean(6),row.IsDBNull(7)?null:row.GetString(7),row.GetString(8),sequence);
        if(await row.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Identity sequence association is ambiguous; no edit is authorized.");
        return result;
    }
}

public sealed record IdentityColumnEdit(IdentityColumnSnapshot Snapshot,IdentityColumnOperation Operation,long? RestartValue=null):IAdministrativeEdit
{
    public long? ParameterValue { get; init; }
    public long? Maximum { get; init; }
    public bool? Cycle { get; init; }
    private IdentitySequenceDefinition? DesiredSequence()
    {
        bool numeric=Operation is IdentityColumnOperation.SetIncrement or IdentityColumnOperation.SetStart or IdentityColumnOperation.SetBounds or IdentityColumnOperation.SetCache;
        if(numeric!=(ParameterValue is not null) || (Operation==IdentityColumnOperation.SetBounds)!=(Maximum is not null) || (Operation==IdentityColumnOperation.SetCycle)!=(Cycle is not null))throw new ArgumentException("Supply only the explicit parameters required by the selected identity operation.");
        var s=Snapshot.Sequence;
        if(!numeric && Operation!=IdentityColumnOperation.SetCycle)return s;
        if(s is null)throw new ArgumentException("Inspect an existing identity sequence before editing its configuration.");
        (long low,long high)=s.TypeOid switch{21=>(short.MinValue,short.MaxValue),23=>(int.MinValue,int.MaxValue),20=>(long.MinValue,long.MaxValue),_=>throw new ArgumentException("Unsupported inspected identity sequence type.")};
        long value=ParameterValue??0;
        return Operation switch
        {
            IdentityColumnOperation.SetIncrement when value!=0 && value>=low && value<=high=>s with{Increment=value},
            IdentityColumnOperation.SetStart when value>=s.Minimum && value<=s.Maximum=>s with{Start=value},
            IdentityColumnOperation.SetBounds when value<Maximum && value>=low && Maximum<=high && s.Start>=value && s.Start<=Maximum=>s with{Minimum=value,Maximum=Maximum!.Value},
            IdentityColumnOperation.SetCache when value>=1=>s with{Cache=value},
            IdentityColumnOperation.SetCycle=>s with{Cycle=Cycle!.Value},
            _=>throw new ArgumentException("Identity sequence parameter must fit its inspected type and bounds; bounds must include configured START, increment must be nonzero and cache positive.")
        };
    }
    public string ToSql()
    {
        ArgumentNullException.ThrowIfNull(Snapshot);
        if(!Enum.IsDefined(Operation) || Snapshot.DatabaseOid==0 || Snapshot.TableOid==0 || Snapshot.NamespaceOid==0 || Snapshot.Position<=0 || Snapshot.TypeOid is not(20 or 21 or 23) || Snapshot.Generated.Length!=0)
            throw new ArgumentException("Identity operations require an inspected smallint, integer or bigint column.");
        bool add=Operation is IdentityColumnOperation.AddAlways or IdentityColumnOperation.AddByDefault;
        if(add?(Snapshot.HasIdentity || Snapshot.Sequence is not null || Snapshot.Nullable || Snapshot.DefaultExpression is not null):(!Snapshot.HasIdentity || Snapshot.Sequence is null))
            throw new ArgumentException(add?"Adding identity requires an inspected NOT NULL column without identity, default or generated expression.":"Inspect an existing identity column for this operation.");
        if(Operation==IdentityColumnOperation.Restart)
        {
            if(RestartValue is not { } value || value<Snapshot.Sequence!.Minimum || value>Snapshot.Sequence.Maximum)throw new ArgumentException("Enter an explicit restart value within the inspected sequence bounds.");
        }
        else if(RestartValue is not null)throw new ArgumentException("Restart value applies only to Restart identity.");
        var desired=DesiredSequence();
        string Number(long value)=>value.ToString(CultureInfo.InvariantCulture);
        string action=Operation switch
        {
            IdentityColumnOperation.AddAlways=>"ADD GENERATED ALWAYS AS IDENTITY",IdentityColumnOperation.AddByDefault=>"ADD GENERATED BY DEFAULT AS IDENTITY",
            IdentityColumnOperation.SetAlways=>"SET GENERATED ALWAYS",IdentityColumnOperation.SetByDefault=>"SET GENERATED BY DEFAULT",
            IdentityColumnOperation.Drop=>"DROP IDENTITY",
            IdentityColumnOperation.SetIncrement=>"SET INCREMENT BY "+Number(desired!.Increment),
            IdentityColumnOperation.SetStart=>"SET START WITH "+Number(desired!.Start),
            IdentityColumnOperation.SetBounds=>"SET MINVALUE "+Number(desired!.Minimum)+" SET MAXVALUE "+Number(desired.Maximum),
            IdentityColumnOperation.SetCache=>"SET CACHE "+Number(desired!.Cache),
            IdentityColumnOperation.SetCycle=>desired!.Cycle?"SET CYCLE":"SET NO CYCLE",
            _=>"RESTART WITH "+Number(RestartValue!.Value)
        };
        return $"ALTER TABLE ONLY {ObjectSql.Relation(Snapshot.Schema,Snapshot.Table)} ALTER COLUMN {SqlIdentifier.Quote(Snapshot.Column)} {action};";
    }
    public async Task ValidateAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken token=default)
    {
        _=ToSql();
        await using var guard=new NpgsqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED; LOCK TABLE ONLY "+ObjectSql.Relation(Snapshot.Schema,Snapshot.Table)+" IN ACCESS EXCLUSIVE MODE",connection,transaction){CommandTimeout=30};
        await guard.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if(await IdentityColumnService.ReadAsync(connection,transaction,Snapshot.Schema,Snapshot.Table,Snapshot.Column,token).ConfigureAwait(false)!=Snapshot)throw IdentityColumnService.Conflict();
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken token=default)
    {
        var actual=await IdentityColumnService.ReadAsync(connection,transaction,Snapshot.Schema,Snapshot.Table,Snapshot.Column,token).ConfigureAwait(false);
        string generation=Operation switch {IdentityColumnOperation.AddAlways or IdentityColumnOperation.SetAlways=>"a",IdentityColumnOperation.AddByDefault or IdentityColumnOperation.SetByDefault=>"d",IdentityColumnOperation.Drop=>"",_=>Snapshot.Generation};
        var expected=Snapshot with{Generation=generation,Sequence=Operation==IdentityColumnOperation.Drop?null:Operation is IdentityColumnOperation.AddAlways or IdentityColumnOperation.AddByDefault?actual.Sequence:DesiredSequence()};
        if(actual!=expected || generation.Length>0 && actual.Sequence is null)throw IdentityColumnService.Conflict();
        if(Operation==IdentityColumnOperation.Restart)
        {
            // Native RESTART holds the underlying sequence lock through COMMIT.
            // Reading state here does not consume a value or infer past nextval calls.
            var sequence=actual.Sequence!;
            await using var command=new NpgsqlCommand("SELECT last_value,is_called FROM "+ObjectSql.Relation(sequence.Schema,sequence.Name),connection,transaction){CommandTimeout=15};
            await using var row=await command.ExecuteReaderAsync(token).ConfigureAwait(false);await row.ReadAsync(token).ConfigureAwait(false);
            if(row.GetInt64(0)!=RestartValue || row.GetBoolean(1))throw IdentityColumnService.Conflict();
        }
    }
}
