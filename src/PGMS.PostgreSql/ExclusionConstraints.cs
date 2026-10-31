using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum RangeExclusionOperator { Overlaps, Equal }
public sealed record RangeExclusionKey(string Column,RangeExclusionOperator Operator=RangeExclusionOperator.Overlaps)
{
    internal string OperatorName=>Operator switch{RangeExclusionOperator.Overlaps=>"&&",RangeExclusionOperator.Equal=>"=",_=>throw new ArgumentException("Choose overlap or equality.")};
    public string ToSql()=>SqlIdentifier.Quote(Column)+" WITH OPERATOR(pg_catalog."+OperatorName+")";
}
public sealed class RangeExclusionEdit:IAdministrativeEdit
{
    public ExpressionIndexTarget Target{get;}public string Name{get;}public IReadOnlyList<RangeExclusionKey> Keys{get;}public bool Deferrable{get;}public bool InitiallyDeferred{get;}
    public RangeExclusionEdit(ExpressionIndexTarget target,string name,IEnumerable<RangeExclusionKey> keys,bool deferrable=false,bool initiallyDeferred=false)
    {Target=target;Name=name;Keys=Array.AsReadOnly(keys.Take(33).ToArray());Deferrable=deferrable;InitiallyDeferred=initiallyDeferred;}
    public string ToSql()
    {
        if(Keys.Count is <1 or >32 || Keys.Any(k=>k is null) || Keys.Select(k=>k.Column).Distinct(StringComparer.Ordinal).Count()!=Keys.Count)throw new ArgumentException("Choose 1 to32 distinct inspected range columns.");
        foreach(var key in Keys){_=key.ToSql();var c=Target.Columns.SingleOrDefault(c=>c.Name==key.Column)??throw new ArgumentException("Select an inspected range column.");if(c.TypeOid is not(3904 or 3906 or 3908 or 3910 or 3912 or 3926))throw new ArgumentException("This exclusion flow supports built-in range columns only.");}
        if(InitiallyDeferred && !Deferrable)throw new ArgumentException("Initially deferred requires a deferrable constraint.");
        return $"ALTER TABLE {ObjectSql.Relation(Target.Schema,Target.Name)} ADD CONSTRAINT {SqlIdentifier.Quote(Name)} EXCLUDE USING gist ("+string.Join(", ",Keys.Select(k=>k.ToSql()))+")"+(Deferrable?" DEFERRABLE"+(InitiallyDeferred?" INITIALLY DEFERRED":" INITIALLY IMMEDIATE"):" NOT DEFERRABLE")+";";
    }
    public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await using(var cmd=new NpgsqlCommand($"LOCK TABLE ONLY {ObjectSql.Relation(Target.Schema,Target.Name)} IN ACCESS EXCLUSIVE MODE",c,tx){CommandTimeout=30})await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await VerifyTarget(c,tx,token).ConfigureAwait(false);
    }
    private async Task VerifyTarget(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)
    {if(!Target.Matches(await ExpressionIndexCatalog.Read(c,tx,Target.Schema,Target.Name,token).ConfigureAwait(false)))throw new InvalidOperationException("Exclusion target changed. Inspect again.");}
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await VerifyTarget(c,tx,token).ConfigureAwait(false);
        await using var cmd=new NpgsqlCommand("""
            SELECT x.contype='x' AND x.conrelid=@table AND x.condeferrable=@deferrable AND x.condeferred=@deferred
              AND x.convalidated AND i.indisvalid AND i.indisready AND m.amname='gist' AND x.conkey=@keys
            FROM pg_catalog.pg_constraint x JOIN pg_catalog.pg_index i ON i.indexrelid=x.conindid
            JOIN pg_catalog.pg_class c ON c.oid=x.conindid JOIN pg_catalog.pg_am m ON m.oid=c.relam
            WHERE x.conrelid=@table AND x.conname=@name
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("table",NpgsqlDbType.Oid,Target.Oid);cmd.Parameters.AddWithValue("name",Name);cmd.Parameters.AddWithValue("deferrable",Deferrable);cmd.Parameters.AddWithValue("deferred",InitiallyDeferred);
        cmd.Parameters.AddWithValue("keys",Keys.Select(k=>Target.Columns.Single(c=>c.Name==k.Column).Position).ToArray());
        if(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)throw new InvalidOperationException("Created exclusion constraint could not be verified.");
    }
}
