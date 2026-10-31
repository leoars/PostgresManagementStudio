using Npgsql;
using PGMS.PostgreSql;

internal sealed class FutureDefaultFixture : IAsyncDisposable
{
    private readonly NpgsqlConnection admin,owner,reader;
    internal string Creator {get;}="future_owner_"+Guid.NewGuid().ToString("N");
    internal string Recipient {get;}="future_reader_"+Guid.NewGuid().ToString("N");
    internal string Schema {get;}="future_"+Guid.NewGuid().ToString("N");
    internal string CreatorConnection {get;}
    internal string RecipientConnection {get;}
    internal string QS=>SqlIdentifier.Quote(Schema);
    internal string QO=>SqlIdentifier.Quote(Creator);
    internal string QR=>SqlIdentifier.Quote(Recipient);
    private FutureDefaultFixture(string connection)
    {
        admin=new(connection);CreatorConnection=new NpgsqlConnectionStringBuilder(connection){Username=Creator,Pooling=false}.ConnectionString;
        RecipientConnection=new NpgsqlConnectionStringBuilder(connection){Username=Recipient,Pooling=false}.ConnectionString;
        owner=new(CreatorConnection);reader=new(RecipientConnection);
    }
    internal static async Task<FutureDefaultFixture> CreateAsync(string connection)
    {
        var f=new FutureDefaultFixture(connection);await f.admin.OpenAsync();
        await f.Admin($"CREATE ROLE {f.QO} LOGIN;CREATE ROLE {f.QR} LOGIN;CREATE SCHEMA {f.QS} AUTHORIZATION {f.QO}");
        await f.owner.OpenAsync();await f.reader.OpenAsync();return f;
    }
    private static async Task Execute(NpgsqlConnection connection,string sql){await using var c=new NpgsqlCommand(sql,connection);await c.ExecuteNonQueryAsync();}
    internal Task Admin(string sql)=>Execute(admin,sql);
    internal Task Owner(string sql)=>Execute(owner,sql);
    internal Task Reader(string sql)=>Execute(reader,sql);
    internal async Task<long> ReaderCount(string sql){await using var c=new NpgsqlCommand(sql,reader);return Convert.ToInt64(await c.ExecuteScalarAsync());}
    internal async Task<bool> Denied(string sql){try{await Reader(sql);return false;}catch(PostgresException e)when(e.SqlState=="42501"){return true;}}
    internal async Task<bool> Has(string function,string target,string privilege)
    {
        if(function is not("has_table_privilege" or "has_sequence_privilege" or "has_function_privilege" or "has_type_privilege" or "has_schema_privilege"))throw new ArgumentException("Unsupported fixture inquiry.");
        await using var c=new NpgsqlCommand($"SELECT pg_catalog.{function}(@role,@target,@privilege)",admin);
        c.Parameters.AddWithValue("role",Recipient);c.Parameters.AddWithValue("target",target);c.Parameters.AddWithValue("privilege",privilege);return (bool)(await c.ExecuteScalarAsync())!;
    }
    internal async Task<bool> Apply(DefaultPrivilegeTarget target,string privilege,bool revoke=false,bool grantOption=false,bool global=false,bool isPublic=false)
    {
        var request=new DefaultPrivilegeRequest(target,Creator,isPublic?"":Recipient,privilege,global || target==DefaultPrivilegeTarget.Schemas?null:Schema,revoke,isPublic,grantOption);
        var snapshot=await new DefaultPrivilegeCatalog(CreatorConnection).InspectAsync(target,Creator,request.Schema);
        var recipient=(await new PrivilegeService(CreatorConnection).InspectRecipientAsync(request.Role,isPublic))!;
        return await new DefaultPrivilegeService(CreatorConnection).ApplyInspectedAsync(request,snapshot,recipient);
    }
    public async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync();await owner.DisposeAsync();
        try{await Admin($"DROP SCHEMA IF EXISTS {QS} CASCADE;DROP OWNED BY {QO},{QR} CASCADE;DROP ROLE {QR};DROP ROLE {QO}");}
        finally{await admin.DisposeAsync();}
    }
}
