using Npgsql;
using PGMS.PostgreSql;

internal static class DefaultDelegationChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string child="future_child_"+Guid.NewGuid().ToString("N"),qchild=SqlIdentifier.Quote(child);
        await f.Admin($"CREATE ROLE {qchild} LOGIN;GRANT USAGE ON SCHEMA {f.QS} TO {f.QR},{qchild}");
        try
        {
            check(await f.Apply(DefaultPrivilegeTarget.Tables,"SELECT",grantOption:true),"Default SELECT grant option is accepted by creator");
            await f.Owner($"CREATE TABLE {f.QS}.delegable(value integer);INSERT INTO {f.QS}.delegable VALUES(1)");
            check(await f.Has("has_table_privilege",f.QS+".delegable","SELECT WITH GRANT OPTION"),"Future table materializes delegation authority");
            await f.Reader($"GRANT SELECT ON {f.QS}.delegable TO {qchild}");
            await using var childConnection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Username=child,Pooling=false}.ConnectionString);
            await childConnection.OpenAsync();
            async Task<bool> ChildHas(string table){await using var c=new NpgsqlCommand("SELECT has_table_privilege(current_user,@table,'SELECT')",childConnection);c.Parameters.AddWithValue("table",f.QS+"."+table);return (bool)(await c.ExecuteScalarAsync())!;}
            await using(var c=new NpgsqlCommand($"SELECT count(*) FROM {f.QS}.delegable",childConnection))check(Convert.ToInt64(await c.ExecuteScalarAsync())==1,"Delegated child actually reads future table");
            var unauthorized=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,f.Creator,child,"SELECT",f.Schema);
            var snapshot=await new DefaultPrivilegeCatalog(f.RecipientConnection).InspectAsync(unauthorized.Target,f.Creator,f.Schema);
            var identity=(await new PrivilegeService(f.RecipientConnection).InspectRecipientAsync(child,false))!;
            bool denied=false;try{await new DefaultPrivilegeService(f.RecipientConnection).ApplyInspectedAsync(unauthorized,snapshot,identity);}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}
            check(denied,"Future object grant option does not delegate management of creator templates");
            check(await f.Apply(DefaultPrivilegeTarget.Tables,"SELECT",revoke:true,grantOption:true),"RESTRICT removes template grant option despite existing object delegation");
            check(await ChildHas("delegable") && await f.Has("has_table_privilege",f.QS+".delegable","SELECT WITH GRANT OPTION"),"Template grant option revoke preserves existing grant chain");
            await f.Owner($"CREATE TABLE {f.QS}.plain(value integer)");
            check(await f.Has("has_table_privilege",f.QS+".plain","SELECT") && !await f.Has("has_table_privilege",f.QS+".plain","SELECT WITH GRANT OPTION"),"Next table retains SELECT without delegation");
            await f.Reader($"GRANT SELECT ON {f.QS}.plain TO {qchild}");
            check(!await ChildHas("plain"),"GRANT without authority leaves child ungranted even when command only emits notice");
            check(await f.Apply(DefaultPrivilegeTarget.Tables,"SELECT",revoke:true),"RESTRICT removes template base privilege without traversing old objects");
            await f.Owner($"CREATE TABLE {f.QS}.private(value integer)");
            check(!await f.Has("has_table_privilege",f.QS+".private","SELECT") && await ChildHas("delegable"),"Template revoke affects only new table and preserves old delegated access");
            denied=false;try{await f.Owner($"REVOKE GRANT OPTION FOR SELECT ON {f.QS}.delegable FROM {f.QR} RESTRICT");}catch(PostgresException e)when(e.SqlState=="2BP01"){denied=true;}
            check(denied && await ChildHas("delegable"),"Existing object RESTRICT detects dependent grants unlike template revoke");
            await f.Reader($"REVOKE SELECT ON {f.QS}.delegable FROM {qchild} RESTRICT");
            await f.Owner($"REVOKE GRANT OPTION FOR SELECT ON {f.QS}.delegable FROM {f.QR} RESTRICT");
            check(!await ChildHas("delegable") && await f.Has("has_table_privilege",f.QS+".delegable","SELECT") && !await f.Has("has_table_privilege",f.QS+".delegable","SELECT WITH GRANT OPTION"),"Explicit dependent revoke allows object grant-option removal while retaining base SELECT");
        }
        finally{await f.Admin($"DROP OWNED BY {qchild};DROP ROLE {qchild}");}
    }
}
