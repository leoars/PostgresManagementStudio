using Npgsql;

namespace PGMS.PostgreSql;

public sealed record PrivilegeGrantorIdentity(uint DatabaseOid,uint Oid,string Name)
{
    internal static InvalidOperationException Conflict()=>new("Selected grantor identity or authority changed, or PostgreSQL chose another grant origin. Nothing was committed. Inspect and select the grantor again.");
    internal void Validate(){if(DatabaseOid==0 || Oid==0)throw new ArgumentException("Select an inspected grantor identity.");_=SqlIdentifier.Quote(Name);}
    internal async Task BindAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CancellationToken token)
    {
        Validate();await VerifyAsync(connection,transaction,false,token).ConfigureAwait(false);
        await using(var command=new NpgsqlCommand("SET LOCAL ROLE "+SqlIdentifier.Quote(Name),connection,transaction){CommandTimeout=15})
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await VerifyAsync(connection,transaction,true,token).ConfigureAwait(false);
    }
    internal async Task VerifyAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,bool requireCurrent,CancellationToken token)
    {
        const string sql="SELECT r.oid,d.oid,pg_catalog.pg_has_role(session_user,r.oid,'SET'),r.rolname=current_user FROM pg_catalog.pg_roles r CROSS JOIN pg_catalog.pg_database d WHERE r.rolname=@name AND d.datname=current_database()";
        await using var command=new NpgsqlCommand(sql,connection,transaction){CommandTimeout=15};command.Parameters.AddWithValue("name",Name);
        await using var row=await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if(!await row.ReadAsync(token).ConfigureAwait(false) || row.GetFieldValue<uint>(0)!=Oid || row.GetFieldValue<uint>(1)!=DatabaseOid || !row.GetBoolean(2) || requireCurrent && !row.GetBoolean(3))throw Conflict();
    }
}
