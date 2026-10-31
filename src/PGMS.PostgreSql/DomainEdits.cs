using System.Collections.ObjectModel;
using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record DomainConstraint(uint Oid, string Name, string Expression, bool Validated);
public sealed class DomainSnapshot
{
    public InspectedDataType Type { get; }
    public InspectedDataType BaseType { get; }
    public string Properties { get; }
    public string BaseTypeSql { get; }
    public string? DefaultSql { get; }
    public bool NotNull { get; }
    public ReadOnlyCollection<DomainConstraint> Constraints { get; }
    internal DomainSnapshot(InspectedDataType type, InspectedDataType baseType, string properties, string baseSql, string? defaultSql, bool notNull, IEnumerable<DomainConstraint> constraints)
    { Type=type; BaseType=baseType; Properties=properties; BaseTypeSql=baseSql; DefaultSql=defaultSql; NotNull=notNull; Constraints=Array.AsReadOnly(constraints.ToArray()); }
}
public sealed class DomainCatalog(string connectionString)
{
    public async Task<DomainSnapshot> InspectAsync(string schema, string name, CancellationToken token=default)
    {
        SqlIdentifier.Quote(schema); SqlIdentifier.Quote(name);
        await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var ro=new NpgsqlCommand("SET TRANSACTION READ ONLY",c,tx))await ro.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await Read(c,tx,schema,name,token).ConfigureAwait(false);
    }
    internal static async Task<DomainSnapshot> Read(NpgsqlConnection c,NpgsqlTransaction tx,string schema,string name,CancellationToken token)
    {
        var types=await DataTypeCatalog.Read(c,tx,schema,name,token).ConfigureAwait(false);
        if(types.Count!=1 || types[0].Kind!="d")throw new InvalidOperationException("Domain not found or USAGE unavailable.");
        var type=types[0];string properties,baseSql,baseSchema,baseName;string? defaultSql;bool notNull;
        await using(var command=new NpgsqlCommand("""
            SELECT ROW(t.typbasetype,t.typtypmod,t.typnotnull,t.typdefaultbin::text,t.typcollation,t.typacl::text)::text,
                   pg_catalog.format_type(t.typbasetype,t.typtypmod),t.typdefault,t.typnotnull,n.nspname,b.typname
            FROM pg_catalog.pg_type t JOIN pg_catalog.pg_type b ON b.oid=t.typbasetype
            JOIN pg_catalog.pg_namespace n ON n.oid=b.typnamespace WHERE t.oid=@oid
            """,c,tx){CommandTimeout=15})
        {
            command.Parameters.AddWithValue("oid",NpgsqlDbType.Oid,type.Oid);
            await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Domain was replaced. Inspect again.");
            properties=reader.GetString(0);baseSql=reader.GetString(1);defaultSql=reader.IsDBNull(2)?null:reader.GetString(2);notNull=reader.GetBoolean(3);baseSchema=reader.GetString(4);baseName=reader.GetString(5);
        }
        var bases=await DataTypeCatalog.Read(c,tx,baseSchema,baseName,token).ConfigureAwait(false);
        if(bases.Count!=1 || bases[0].Oid!=type.BaseTypeOid)throw new InvalidOperationException("Domain base type is not supported or USAGE is unavailable.");
        var constraints=new List<DomainConstraint>();
        await using(var command=new NpgsqlCommand("SELECT oid,conname,pg_catalog.pg_get_expr(conbin,0),convalidated FROM pg_catalog.pg_constraint WHERE contypid=@oid AND contype='c' ORDER BY conname,oid LIMIT 1001",c,tx){CommandTimeout=15})
        {
            command.Parameters.AddWithValue("oid",NpgsqlDbType.Oid,type.Oid);
            await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while(await reader.ReadAsync(token).ConfigureAwait(false))constraints.Add(new(reader.GetFieldValue<uint>(0),reader.GetString(1),reader.GetString(2),reader.GetBoolean(3)));
        }
        if(constraints.Count>1000)throw new InvalidOperationException("Domain exceeds the 1000-constraint limit.");
        return new(type,bases[0],properties,baseSql,defaultSql,notNull,constraints);
    }
}
public sealed record DomainRule(IndexComparison Comparison,string Value="")
{
    public string ToSql()
    {
        if(Comparison is IndexComparison.IsNull or IndexComparison.IsNotNull)
        {
            if(Value.Length!=0)throw new ArgumentException("NULL comparisons do not use a literal value.");
            return Comparison==IndexComparison.IsNull?"VALUE IS NULL":"VALUE IS NOT NULL";
        }
        string op=Comparison switch{IndexComparison.Equal=>"=",IndexComparison.NotEqual=>"<>",IndexComparison.Less=>"<",IndexComparison.LessOrEqual=>"<=",IndexComparison.Greater=>">",IndexComparison.GreaterOrEqual=>">=",_=>throw new ArgumentException("Choose a supported domain comparison.")};
        return "VALUE "+op+" "+new PartitionValue(PartitionValueKind.Literal,Value).ToSql();
    }
}
public enum DomainConstraintOperation { Add, Validate, Drop }
public sealed class DomainConstraintEdit(DomainSnapshot target,DomainConstraintOperation operation,string name,DomainRule? rule=null,bool notValid=false):IAdministrativeEdit
{
    public string ToSql()
    {
        ArgumentNullException.ThrowIfNull(target);string constraint=SqlIdentifier.Quote(name);string prefix="ALTER DOMAIN "+target.Type.ToSql();
        if(target.Type.Kind!="d")throw new ArgumentException("Select a domain.");
        var existing=target.Constraints.SingleOrDefault(x=>x.Name==name);
        if(operation==DomainConstraintOperation.Add)
        {
            if(existing is not null || target.Constraints.Count>=1000)throw new ArgumentException("Constraint name exists or the 1000-constraint limit was reached.");
            return prefix+" ADD CONSTRAINT "+constraint+" CHECK ("+(rule??throw new ArgumentException("Choose a domain rule.")).ToSql()+")"+(notValid?" NOT VALID":"")+";";
        }
        if(rule is not null || notValid)throw new ArgumentException("Unused domain rule or NOT VALID option.");
        if(existing is null)throw new ArgumentException("Select an inspected constraint.");
        return operation switch
        {
            DomainConstraintOperation.Validate when !existing.Validated=>prefix+" VALIDATE CONSTRAINT "+constraint+";",
            DomainConstraintOperation.Drop=>prefix+" DROP CONSTRAINT "+constraint+" RESTRICT;",
            _=>throw new ArgumentException("Unsupported operation or constraint is already validated.")
        };
    }
    public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)
    {
        ToSql();var actual=await DomainCatalog.Read(c,tx,target.Type.Schema,target.Type.Name,token).ConfigureAwait(false);
        if(actual.Type!=target.Type || actual.BaseType!=target.BaseType || actual.Properties!=target.Properties || !actual.Constraints.SequenceEqual(target.Constraints))throw new InvalidOperationException("Domain identity, base type or constraints changed. Inspect again.");
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)
    {
        var actual=await DomainCatalog.Read(c,tx,target.Type.Schema,target.Type.Name,token).ConfigureAwait(false);
        if((actual.Type with{Signature=target.Type.Signature})!=target.Type || actual.BaseType!=target.BaseType || actual.Properties!=target.Properties)throw new InvalidOperationException("Domain identity or unrelated properties changed. Operation rolled back.");
        var expected=target.Constraints.Where(x=>x.Name!=name).ToArray();var unrelated=actual.Constraints.Where(x=>x.Name!=name).ToArray();
        var result=actual.Constraints.SingleOrDefault(x=>x.Name==name);var old=target.Constraints.SingleOrDefault(x=>x.Name==name);
        bool matches=operation switch
        {
            DomainConstraintOperation.Add=>result is not null && result.Oid!=0 && result.Validated==!notValid,
            DomainConstraintOperation.Validate=>result==old! with{Validated=true},
            DomainConstraintOperation.Drop=>result is null,
            _=>false
        };
        if(!expected.SequenceEqual(unrelated) || !matches)throw new InvalidOperationException("Domain constraint result differs from the reviewed change. Operation rolled back.");
    }
}
