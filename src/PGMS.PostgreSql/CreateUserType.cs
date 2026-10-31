using System.Collections.ObjectModel;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record TypeCreationSchema(uint DatabaseOid,uint Oid,uint OwnerOid,uint CreatorOid,string Name,string Version);
public sealed class TypeCreationCatalog(string connectionString)
{
    public async Task<TypeCreationSchema> InspectSchemaAsync(string schema,CancellationToken token=default)
    {
        QuoteSchema(schema);
        await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx=await c.BeginTransactionAsync(token).ConfigureAwait(false);
        await using(var ro=new NpgsqlCommand("SET TRANSACTION READ ONLY",c,tx))await ro.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await Read(c,tx,schema,token).ConfigureAwait(false);
    }
    internal static string QuoteSchema(string schema)
    {
        string result=SqlIdentifier.Quote(schema);
        if(schema.StartsWith("pg_",StringComparison.OrdinalIgnoreCase) || schema.Equals("information_schema",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Choose a user schema.");
        return result;
    }
    internal static async Task<TypeCreationSchema> Read(NpgsqlConnection c,NpgsqlTransaction tx,string schema,CancellationToken token)
    {
        QuoteSchema(schema);
        await using var command=new NpgsqlCommand("""
            SELECT d.oid,n.oid,n.nspowner,r.oid,n.nspname,n.xmin::text
            FROM pg_catalog.pg_namespace n
            JOIN pg_catalog.pg_database d ON d.datname=pg_catalog.current_database()
            JOIN pg_catalog.pg_roles r ON r.rolname=CURRENT_USER
            WHERE n.nspname=@schema AND pg_catalog.has_schema_privilege(n.oid,'USAGE') AND pg_catalog.has_schema_privilege(n.oid,'CREATE')
            """,c,tx){CommandTimeout=15};
        command.Parameters.AddWithValue("schema",schema);
        await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await reader.ReadAsync(token).ConfigureAwait(false))throw new InvalidOperationException("Schema not found or USAGE/CREATE unavailable.");
        return new(reader.GetFieldValue<uint>(0),reader.GetFieldValue<uint>(1),reader.GetFieldValue<uint>(2),reader.GetFieldValue<uint>(3),reader.GetString(4),reader.GetString(5));
    }
    internal static async Task Verify(NpgsqlConnection c,NpgsqlTransaction tx,TypeCreationSchema schema,CancellationToken token)
    {if(await Read(c,tx,schema.Name,token).ConfigureAwait(false)!=schema)throw new InvalidOperationException("Schema identity, authority or current role changed. Inspect again.");}
    internal static void VerifyCreated(TypeCreationSchema schema,InspectedDataType type,string name,string kind)
    {
        if(type.DatabaseOid!=schema.DatabaseOid || type.NamespaceOid!=schema.Oid || type.OwnerOid!=schema.CreatorOid || type.Schema!=schema.Name || type.Name!=name || type.Kind!=kind)
            throw new InvalidOperationException("Created type identity differs from the inspected schema and current role. Operation rolled back.");
    }
}
public sealed class CreateEnumEdit : IAdministrativeEdit
{
    public TypeCreationSchema Schema {get;}
    public string Name {get;}
    public ReadOnlyCollection<string> Labels {get;}
    public CreateEnumEdit(TypeCreationSchema schema,string name,IEnumerable<string> labels)
    {Schema=schema;Name=name;ArgumentNullException.ThrowIfNull(labels);Labels=Array.AsReadOnly(labels.Take(1001).ToArray());}
    public string ToSql()
    {
        ArgumentNullException.ThrowIfNull(Schema);
        if(Labels.Count is <1 or >1000 || Labels.Distinct(StringComparer.Ordinal).Count()!=Labels.Count)throw new ArgumentException("Use 1–1000 distinct enum labels in this flow.");
        return "CREATE TYPE "+TypeCreationCatalog.QuoteSchema(Schema.Name)+"."+SqlIdentifier.Quote(Name)+" AS ENUM ("+string.Join(", ",Labels.Select(EnumValueEdit.Literal))+");";
    }
    public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)=>TypeCreationCatalog.Verify(c,tx,Schema,token);
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)
    {
        await TypeCreationCatalog.Verify(c,tx,Schema,token).ConfigureAwait(false);
        var actual=await EnumCatalog.Read(c,tx,Schema.Name,Name,token).ConfigureAwait(false);
        TypeCreationCatalog.VerifyCreated(Schema,actual.Type,Name,"e");
        if(!actual.Labels.Select(x=>x.Value).SequenceEqual(Labels))throw new InvalidOperationException("Created enum values differ from the reviewed ordered labels. Operation rolled back.");
    }
}
public sealed class CreateDomainEdit(TypeCreationSchema schema,string name,SelectedColumnType baseType,string? defaultLiteral=null,bool notNull=false,string? constraintName=null,DomainRule? rule=null):IAdministrativeEdit
{
    public string ToSql()
    {
        ArgumentNullException.ThrowIfNull(schema);ArgumentNullException.ThrowIfNull(baseType);
        if((constraintName is null)!=(rule is null))throw new ArgumentException("Provide both constraint name and typed rule, or neither.");
        return "CREATE DOMAIN "+TypeCreationCatalog.QuoteSchema(schema.Name)+"."+SqlIdentifier.Quote(name)+" AS "+baseType.ToSql()
            +(defaultLiteral is null?"":" DEFAULT "+new PartitionValue(PartitionValueKind.Literal,defaultLiteral).ToSql())+(notNull?" NOT NULL":"")
            +(rule is null?"":" CONSTRAINT "+SqlIdentifier.Quote(constraintName!)+" CHECK ("+rule.ToSql()+")")+";";
    }
    public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)
    {await TypeCreationCatalog.Verify(c,tx,schema,token).ConfigureAwait(false);await DataTypeCatalog.Verify(c,tx,baseType.Type,token).ConfigureAwait(false);}
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)
    {
        await TypeCreationCatalog.Verify(c,tx,schema,token).ConfigureAwait(false);await DataTypeCatalog.Verify(c,tx,baseType.Type,token).ConfigureAwait(false);
        var actual=await DomainCatalog.Read(c,tx,schema.Name,name,token).ConfigureAwait(false);
        TypeCreationCatalog.VerifyCreated(schema,actual.Type,name,"d");
        if(actual.BaseType!=baseType.Type || actual.NotNull!=notNull || defaultLiteral is not null && actual.DefaultSql is null ||
            actual.Constraints.Count!=(rule is null?0:1) || rule is not null && (actual.Constraints[0].Name!=constraintName || !actual.Constraints[0].Validated))
            throw new InvalidOperationException("Created domain properties differ from the reviewed definition. Operation rolled back.");
        await using var modifier=new NpgsqlCommand("SELECT typtypmod FROM pg_catalog.pg_type WHERE oid=@oid",c,tx){CommandTimeout=15};
        modifier.Parameters.AddWithValue("oid",NpgsqlDbType.Oid,actual.Type.Oid);
        if(await modifier.ExecuteScalarAsync(token).ConfigureAwait(false) is not int actualModifier || actualModifier!=InspectedColumnTypeEdit.ExpectedModifier(baseType))
            throw new InvalidOperationException("Created domain modifier differs from the reviewed base type. Operation rolled back.");
    }
}
