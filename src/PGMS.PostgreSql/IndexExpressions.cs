using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum IndexTransform { Column, Lower, Upper, Length, Absolute }
public enum IndexComparison { IsNull, IsNotNull, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }
public sealed record IndexOperatorClass(uint Oid,uint NamespaceOid,string Schema,string Name,uint InputTypeOid,uint MethodOid);
public sealed record IndexExpression(string Column,IndexTransform Transform=IndexTransform.Column)
{
    public string ToSql()
    {
        string column=SqlIdentifier.Quote(Column);
        return Transform switch
        {
            IndexTransform.Column=>column,IndexTransform.Lower=>$"pg_catalog.lower({column})",IndexTransform.Upper=>$"pg_catalog.upper({column})",
            IndexTransform.Length=>$"pg_catalog.length({column})",IndexTransform.Absolute=>$"pg_catalog.abs({column})",_=>throw new ArgumentException("Choose a supported index expression.")
        };
    }
}
public sealed record ExpressionIndexKey(IndexExpression Expression,IndexSortOrder Order=IndexSortOrder.Default,IndexNullPlacement Nulls=IndexNullPlacement.Default)
{
    public IndexOperatorClass? OperatorClass {get;init;}
    public string ToSql()=>"("+Expression.ToSql()+")"+(OperatorClass is {} op?" "+SqlIdentifier.Quote(op.Schema)+"."+SqlIdentifier.Quote(op.Name):"")+(Order switch{IndexSortOrder.Default=>"",IndexSortOrder.Ascending=>" ASC",IndexSortOrder.Descending=>" DESC",_=>throw new ArgumentException("Unsupported key order.")})
        +(Nulls switch{IndexNullPlacement.Default=>"",IndexNullPlacement.First=>" NULLS FIRST",IndexNullPlacement.Last=>" NULLS LAST",_=>throw new ArgumentException("Unsupported NULL placement.")});
}
public sealed record IndexPredicate(IndexExpression Expression,IndexComparison Comparison,string Value="")
{
    public string ToSql()
    {
        string expression=Expression.ToSql();
        if(Comparison is IndexComparison.IsNull or IndexComparison.IsNotNull)
        {
            if(Value.Length!=0)throw new ArgumentException("NULL predicates do not accept literal values.");
            return "("+expression+(Comparison==IndexComparison.IsNull?" IS NULL)":" IS NOT NULL)");
        }
        string op=Comparison switch{IndexComparison.Equal=>"=",IndexComparison.NotEqual=>"<>",IndexComparison.Less=>"<",IndexComparison.LessOrEqual=>"<=",IndexComparison.Greater=>">",IndexComparison.GreaterOrEqual=>">=",_=>throw new ArgumentException("Unsupported predicate comparison.")};
        return "("+expression+" "+op+" "+new PartitionValue(PartitionValueKind.Literal,Value).ToSql()+")";
    }
}
public sealed record IndexColumnIdentity(short Position,string Name,uint TypeOid,int TypeModifier,uint CollationOid);
public sealed class ExpressionIndexTarget
{
    public uint DatabaseOid{get;} public uint NamespaceOid{get;} public uint Oid{get;} public uint OwnerOid{get;}
    public string Schema{get;} public string Name{get;} public IReadOnlyList<IndexColumnIdentity> Columns{get;}
    internal ExpressionIndexTarget(uint database,uint ns,uint oid,uint owner,string schema,string name,IEnumerable<IndexColumnIdentity> columns)
    {DatabaseOid=database;NamespaceOid=ns;Oid=oid;OwnerOid=owner;Schema=schema;Name=name;Columns=Array.AsReadOnly(columns.ToArray());}
    internal bool Matches(ExpressionIndexTarget other)=>DatabaseOid==other.DatabaseOid && NamespaceOid==other.NamespaceOid && Oid==other.Oid && OwnerOid==other.OwnerOid && Schema==other.Schema && Name==other.Name && Columns.SequenceEqual(other.Columns);
}
public sealed class ExpressionIndexCatalog(string connectionString)
{
    public async Task<IReadOnlyList<IndexOperatorClass>> InspectOperatorClassesAsync(CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var cmd=new NpgsqlCommand("SELECT o.oid,n.oid,n.nspname,o.opcname,o.opcintype,o.opcmethod FROM pg_catalog.pg_opclass o JOIN pg_catalog.pg_namespace n ON n.oid=o.opcnamespace JOIN pg_catalog.pg_am m ON m.oid=o.opcmethod WHERE m.amname='btree' AND n.nspname='pg_catalog' ORDER BY o.opcname LIMIT 1001",c){CommandTimeout=15};
        await using var r=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);var result=new List<IndexOperatorClass>();
        while(await r.ReadAsync(token).ConfigureAwait(false))result.Add(new(r.GetFieldValue<uint>(0),r.GetFieldValue<uint>(1),r.GetString(2),r.GetString(3),r.GetFieldValue<uint>(4),r.GetFieldValue<uint>(5)));
        if(result.Count>1000)throw new InvalidOperationException("Operator class catalog exceeds the supported inspection size.");return result.AsReadOnly();
    }
    public async Task<ExpressionIndexTarget> InspectAsync(string schema,string table,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ObjectSql.Relation(schema,table);
        await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var ro=new NpgsqlCommand("SET TRANSACTION READ ONLY",c,tx))await ro.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await Read(c,tx,schema,table,token).ConfigureAwait(false);
    }
    internal static async Task<ExpressionIndexTarget> Read(NpgsqlConnection c,NpgsqlTransaction tx,string schema,string table,CancellationToken token)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT d.oid,n.oid,c.oid,c.relowner,a.attnum,a.attname,a.atttypid,a.atttypmod,a.attcollation
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_catalog.pg_database d ON d.datname=pg_catalog.current_database()
            JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE n.nspname=@schema AND c.relname=@table AND c.relkind='r' AND c.relpersistence='p' AND NOT c.relispartition
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
            ORDER BY a.attnum
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("schema",schema);cmd.Parameters.AddWithValue("table",table);await using var r=await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        var columns=new List<IndexColumnIdentity>();uint db=0,ns=0,oid=0,owner=0;
        while(await r.ReadAsync(token).ConfigureAwait(false)){db=r.GetFieldValue<uint>(0);ns=r.GetFieldValue<uint>(1);oid=r.GetFieldValue<uint>(2);owner=r.GetFieldValue<uint>(3);columns.Add(new(r.GetInt16(4),r.GetString(5),r.GetFieldValue<uint>(6),r.GetInt32(7),r.GetFieldValue<uint>(8)));}
        if(oid==0)throw new InvalidOperationException("Inspect an ordinary permanent table with columns, without inheritance or partitions.");
        return new(db,ns,oid,owner,schema,table,columns);
    }
}

/// <summary>Immutable, inspected index proposal. Functions are a deliberately bounded set of pg_catalog transforms.</summary>
public sealed class ExpressionIndexEdit:IAdministrativeEdit
{
    public ExpressionIndexTarget Target{get;} public string Name{get;} public IReadOnlyList<ExpressionIndexKey> Keys{get;}
    public IReadOnlyList<string> IncludedColumns{get;} public IndexPredicate? Predicate{get;} public bool Unique{get;} public bool NullsNotDistinct{get;}
    public ExpressionIndexEdit(ExpressionIndexTarget target,string name,IEnumerable<ExpressionIndexKey> keys,bool unique=false,bool nullsNotDistinct=false,IEnumerable<string>? includedColumns=null,IndexPredicate? predicate=null)
    {
        Target=target??throw new ArgumentNullException(nameof(target));Name=name;ArgumentNullException.ThrowIfNull(keys);
        Keys=Array.AsReadOnly(keys.Take(33).ToArray());IncludedColumns=Array.AsReadOnly((includedColumns??[]).Take(33).ToArray());Unique=unique;NullsNotDistinct=nullsNotDistinct;Predicate=predicate;
    }
    private void ValidateExpression(IndexExpression expression)
    {
        _=expression.ToSql();var column=Target.Columns.SingleOrDefault(c=>c.Name==expression.Column)??throw new ArgumentException("Select an exact inspected column.");
        bool text=column.TypeOid is 25 or 1043 or 1042,numeric=column.TypeOid is 20 or 21 or 23 or 700 or 701 or 1700;
        if(expression.Transform is IndexTransform.Lower or IndexTransform.Upper or IndexTransform.Length && !text)throw new ArgumentException("Text transforms require text, varchar or char.");
        if(expression.Transform==IndexTransform.Absolute && !numeric)throw new ArgumentException("Absolute requires a built-in numeric column.");
        if(expression.Transform==IndexTransform.Column && !(text||numeric||column.TypeOid is 16 or 1082 or 1114 or 1184 or 2950))throw new ArgumentException("This inspected index flow currently accepts built-in scalar types only.");
    }
    public uint ResultType(IndexExpression expression)
    {
        ValidateExpression(expression);uint type=Target.Columns.Single(c=>c.Name==expression.Column).TypeOid;
        return expression.Transform switch{IndexTransform.Lower or IndexTransform.Upper=>25,IndexTransform.Length=>23,_=>type==1043?25:type};
    }
    public string ToSql()
    {
        SqlIdentifier.Quote(Name);if(Keys.Count is <1 or >32 || Keys.Count+IncludedColumns.Count>32 || Keys.Any(k=>k is null))throw new ArgumentException("Use 1 to32 keys and at most32 keys plus included columns.");
        if(NullsNotDistinct && !Unique)throw new ArgumentException("NULLS NOT DISTINCT requires UNIQUE.");
        foreach(var key in Keys){ValidateExpression(key.Expression);if(key.OperatorClass is {} op && (op.Schema!="pg_catalog" || op.InputTypeOid!=ResultType(key.Expression)))throw new ArgumentException("Choose a compatible inspected built-in B-tree operator class.");}
        if(IncludedColumns.Distinct(StringComparer.Ordinal).Count()!=IncludedColumns.Count)throw new ArgumentException("INCLUDE names must be distinct.");
        foreach(var column in IncludedColumns){SqlIdentifier.Quote(column);if(!Target.Columns.Any(c=>c.Name==column) || Keys.Any(k=>k.Expression.Transform==IndexTransform.Column && k.Expression.Column==column))throw new ArgumentException("INCLUDE must reference inspected columns outside the plain keys.");}
        if(Predicate is not null)ValidateExpression(Predicate.Expression);
        return $"CREATE {(Unique?"UNIQUE ":"")}INDEX {SqlIdentifier.Quote(Name)} ON {ObjectSql.Relation(Target.Schema,Target.Name)} USING btree ("+string.Join(", ",Keys.Select(k=>k.ToSql()))+")"
            +(IncludedColumns.Count==0?"":" INCLUDE ("+string.Join(", ",IncludedColumns.Select(SqlIdentifier.Quote))+")")+(NullsNotDistinct?" NULLS NOT DISTINCT":"")+(Predicate is null?"":" WHERE "+Predicate.ToSql())+";";
    }
    public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await using(var guard=new NpgsqlCommand($"LOCK TABLE ONLY {ObjectSql.Relation(Target.Schema,Target.Name)} IN SHARE MODE",c,tx){CommandTimeout=30})await guard.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await VerifyTarget(c,tx,token).ConfigureAwait(false);
        foreach(var key in Keys.Where(k=>k.OperatorClass is not null))
        {
            var op=key.OperatorClass!;await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_opclass o JOIN pg_catalog.pg_namespace n ON n.oid=o.opcnamespace JOIN pg_catalog.pg_am m ON m.oid=o.opcmethod WHERE o.oid=@oid AND n.oid=@ns AND n.nspname=@schema AND o.opcname=@name AND o.opcintype=@type AND o.opcmethod=@method AND m.amname='btree')",c,tx);
            cmd.Parameters.AddWithValue("oid",NpgsqlDbType.Oid,op.Oid);cmd.Parameters.AddWithValue("ns",NpgsqlDbType.Oid,op.NamespaceOid);cmd.Parameters.AddWithValue("schema",op.Schema);cmd.Parameters.AddWithValue("name",op.Name);cmd.Parameters.AddWithValue("type",NpgsqlDbType.Oid,op.InputTypeOid);cmd.Parameters.AddWithValue("method",NpgsqlDbType.Oid,op.MethodOid);
            if(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)throw new InvalidOperationException("Operator class identity changed. Inspect again.");
        }
    }
    private async Task VerifyTarget(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)
    {if(!Target.Matches(await ExpressionIndexCatalog.Read(c,tx,Target.Schema,Target.Name,token).ConfigureAwait(false)))throw new InvalidOperationException("Index target identity or column definition changed. Inspect again.");}
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await VerifyTarget(c,tx,token).ConfigureAwait(false);
        await using var cmd=new NpgsqlCommand("""
            SELECT i.indrelid=@table AND i.indisvalid AND i.indisready AND i.indisunique=@unique AND i.indnullsnotdistinct=@nulls
              AND i.indnkeyatts=@keys AND i.indnatts=@total AND am.amname='btree'
            FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid JOIN pg_catalog.pg_am am ON am.oid=c.relam
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.oid=@ns AND c.relname=@name
            """,c,tx){CommandTimeout=15};
        cmd.Parameters.AddWithValue("table",NpgsqlDbType.Oid,Target.Oid);cmd.Parameters.AddWithValue("ns",NpgsqlDbType.Oid,Target.NamespaceOid);cmd.Parameters.AddWithValue("name",Name);cmd.Parameters.AddWithValue("unique",Unique);cmd.Parameters.AddWithValue("nulls",NullsNotDistinct);cmd.Parameters.AddWithValue("keys",Keys.Count);cmd.Parameters.AddWithValue("total",Keys.Count+IncludedColumns.Count);
        if(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)throw new InvalidOperationException("Created index identity and validity could not be verified.");
        for(int index=0;index<Keys.Count;index++)if(Keys[index].OperatorClass is {} op)
        {
            cmd.CommandText="SELECT i.indclass[@position]=@op FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid WHERE c.relnamespace=@ns AND c.relname=@name";
            if(!cmd.Parameters.Contains("position")){cmd.Parameters.AddWithValue("position",index);cmd.Parameters.AddWithValue("op",NpgsqlDbType.Oid,op.Oid);}else{cmd.Parameters["position"].Value=index;cmd.Parameters["op"].Value=op.Oid;}
            if(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)throw new InvalidOperationException("Created index used a different operator class.");
        }
    }
}
