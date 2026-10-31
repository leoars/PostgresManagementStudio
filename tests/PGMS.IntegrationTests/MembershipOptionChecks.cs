using Npgsql;
using PGMS.PostgreSql;

internal static class MembershipOptionChecks
{
    public static void RunSql(Action<bool,string> check)
    {
        var r=new RoleMembershipEdit("group","member");
        check(r.ToSql()=="GRANT \"group\" TO \"member\";","Basic membership GRANT SQL remains compatible");
        check((r with {Revoke=true}).ToSql()=="REVOKE \"group\" FROM \"member\" RESTRICT;","Basic membership REVOKE remains restrictive");
        foreach(var option in Enum.GetValues<MembershipOption>())
        {
            string key=option.ToString().ToUpperInvariant();
            check((r with {Option=option}).ToSql()==$"GRANT \"group\" TO \"member\" WITH {key} TRUE;","Explicit membership TRUE SQL: "+option);
            check((r with {Option=option,OptionValue=false}).ToSql()==$"GRANT \"group\" TO \"member\" WITH {key} FALSE;","Explicit membership FALSE SQL: "+option);
            check((r with {Option=option,Revoke=true}).ToSql()==$"REVOKE {key} OPTION FOR \"group\" FROM \"member\" RESTRICT;","Membership option-only restrictive revoke SQL: "+option);
        }
        check(new RoleMembershipEdit("g'\"é","m'\"é",Option:MembershipOption.Admin).ToSql()=="GRANT \"g'\"\"é\" TO \"m'\"\"é\" WITH ADMIN TRUE;","Membership options quote exact Unicode role identifiers");
        foreach(var invalid in new[]{r with{GrantedRole=""},r with{MemberRole=""},r with{GrantedRole="member"},r with{Option=(MembershipOption)99},r with{MemberRole="x\0y"},r with{GrantedRole=new string('x',64)}})
        {bool refused=false;try{invalid.ToSql();}catch(ArgumentException){refused=true;}check(refused,"Invalid membership option request refused before connection");}
    }

    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),group="mo_group_"+suffix,member="mo_member_"+suffix,other="mo_other_"+suffix;
        string schema="mo_schema_"+suffix,qs=SqlIdentifier.Quote(schema);
        string qg=SqlIdentifier.Quote(group),qm=SqlIdentifier.Quote(member),qo=SqlIdentifier.Quote(other);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        async Task<(bool Admin,bool Inherit,bool Set)> Options()
        {
            await using var c=new NpgsqlCommand("SELECT m.admin_option,m.inherit_option,m.set_option FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles g ON g.oid=m.roleid JOIN pg_catalog.pg_roles u ON u.oid=m.member WHERE g.rolname=@g AND u.rolname=@m",admin);
            c.Parameters.AddWithValue("g",group);c.Parameters.AddWithValue("m",member);await using var row=await c.ExecuteReaderAsync();if(!await row.ReadAsync())throw new Exception("Missing fixture membership");return(row.GetBoolean(0),row.GetBoolean(1),row.GetBoolean(2));
        }
        var service=new AdministrativeEditService(connection);var r=new RoleMembershipEdit(group,member);
        try
        {
            await Sql($"CREATE ROLE {qg};CREATE ROLE {qm} LOGIN;CREATE ROLE {qo};");
            await service.ApplyAsync(r);
            check(await Options()==(false,true,true),"Basic membership retains server defaults ADMINfalse INHERITtrue SETtrue");
            await Sql($"GRANT {qg} TO {qm} WITH ADMIN TRUE;");
            foreach(var option in Enum.GetValues<MembershipOption>())
            {
                await service.ApplyAsync(r with{Option=option,OptionValue=false});var disabled=await Options();
                check(option switch{MembershipOption.Admin=>!disabled.Admin,MembershipOption.Inherit=>!disabled.Inherit,_=>!disabled.Set},"Membership option FALSE persists: "+option);
                await service.ApplyAsync(r with{Option=option});var enabled=await Options();
                check(option switch{MembershipOption.Admin=>enabled.Admin,MembershipOption.Inherit=>enabled.Inherit,_=>enabled.Set},"Membership option TRUE persists: "+option);
                await service.ApplyAsync(r with{Option=option,Revoke=true});var revoked=await Options();
                check(option switch{MembershipOption.Admin=>!revoked.Admin,MembershipOption.Inherit=>!revoked.Inherit,_=>!revoked.Set},"Option-only revoke retains membership and disables selected flag: "+option);
            }
            check(await Options()==(false,false,false),"Three option-only revokes retain membership with all flags false");
            var text=await new RoleMembershipService(connection).InspectAsync(member);
            check(text.Contains(group) && text.Contains("ADMIN=False INHERIT=False SET=False"),"Membership inspection exposes persisted option values");
            string limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=member}.ConnectionString;
            var delegateService=new AdministrativeEditService(limitedConnection);var delegated=new RoleMembershipEdit(group,other);
            async Task<bool> DelegationDenied(){try{await delegateService.ApplyAsync(delegated);return false;}catch(PostgresException ex)when(ex.SqlState=="42501"){return true;}}
            check(await DelegationDenied(),"Membership without ADMIN cannot delegate group to another role");
            await service.ApplyAsync(r with{Option=MembershipOption.Admin});await delegateService.ApplyAsync(delegated);
            check((await new RoleMembershipService(connection).InspectAsync(other)).Contains(group),"ADMIN true permits real delegation without CREATEROLE");
            await delegateService.ApplyAsync(delegated with{Revoke=true});await service.ApplyAsync(r with{Option=MembershipOption.Admin,Revoke=true});
            check(await Options()==(false,false,false),"ADMIN option-only revoke preserves other disabled flags and membership");
            check(await DelegationDenied(),"Removing ADMIN blocks further membership delegation");
            await Sql($"CREATE SCHEMA {qs};CREATE TABLE {qs}.target(id integer);INSERT INTO {qs}.target VALUES(42);GRANT USAGE ON SCHEMA {qs} TO {qg};GRANT SELECT ON {qs}.target TO {qg};");
            await using var limited=new NpgsqlConnection(limitedConnection);await limited.OpenAsync();
            async Task<object?> Value(string sql){await using var c=new NpgsqlCommand(sql,limited);return await c.ExecuteScalarAsync();}
            async Task<bool> Denied(string sql){try{await Value(sql);return false;}catch(PostgresException ex)when(ex.SqlState=="42501"){return true;}}
            check(await Denied($"SELECT id FROM {qs}.target"),"INHERIT false does not inherit group schema and table access");
            await service.ApplyAsync(r with{Option=MembershipOption.Inherit});
            check((int)(await Value($"SELECT id FROM {qs}.target"))! ==42,"INHERIT true makes group SELECT usable in existing member session");
            check(await Options()==(false,true,false),"Changing INHERIT leaves ADMIN and SET unchanged");
            await service.ApplyAsync(r with{Option=MembershipOption.Inherit,Revoke=true});
            check(await Denied($"SELECT id FROM {qs}.target") && await Options()==(false,false,false),"INHERIT option-only revoke removes inherited access without deleting membership");
            check(await Denied($"SET ROLE {qg}"),"SET false refuses explicit switch to granted group");
            await service.ApplyAsync(r with{Option=MembershipOption.Set});
            check(await Denied($"SELECT id FROM {qs}.target"),"SET true alone does not inherit group SELECT");
            await Value($"SET ROLE {qg}");
            check((string)(await Value("SELECT current_user"))! ==group && (int)(await Value($"SELECT id FROM {qs}.target"))! ==42,"SET true permits explicit role switch and group privileges");
            await Value("RESET ROLE");await service.ApplyAsync(r with{Option=MembershipOption.Set,Revoke=true});
            check(await Denied($"SET ROLE {qg}") && await Options()==(false,false,false),"SET option-only revoke prevents future switch while retaining membership");
            await service.ApplyAsync(r with{Option=MembershipOption.Admin});await delegateService.ApplyAsync(delegated);
            check((await new RoleMembershipService(connection).InspectAsync(other)).Contains("grantor: "+member),"Delegated membership inspection identifies actual non-superuser grantor");
            bool dependency=false;try{await service.ApplyAsync(r with{Option=MembershipOption.Admin,Revoke=true});}catch(PostgresException ex)when(ex.SqlState=="2BP01"){dependency=true;}
            check(dependency && (await Options()).Admin,"ADMIN option-only RESTRICT refuses dependent grant and retains flag");
            await delegateService.ApplyAsync(delegated with{Revoke=true});await service.ApplyAsync(r with{Option=MembershipOption.Admin,Revoke=true});
            check(await Options()==(false,false,false),"Explicit dependent cleanup permits ADMIN option-only revoke without removing membership");
            bool noOp=false;try{await service.ApplyAsync(r with{Option=MembershipOption.Admin,OptionValue=false});}catch(InvalidOperationException ex)when(ex.Message.Contains("notice")){noOp=true;}
            check(noOp && await Options()==(false,false,false),"No-op membership Notice is not reported as confirmed success");
            bool missing=false;try{await service.ApplyAsync(r with{GrantedRole=group+"_missing",Option=MembershipOption.Set});}catch(PostgresException ex)when(ex.SqlState=="42704"){missing=true;}
            check(missing && await Options()==(false,false,false),"Missing role fails42704 without changing original membership");
            bool cycle=false;try{await service.ApplyAsync(new RoleMembershipEdit(member,group,Option:MembershipOption.Inherit));}catch(PostgresException ex)when(ex.SqlState=="0LP01"){cycle=true;}
            check(cycle && await Options()==(false,false,false),"Server refuses circular membership without altering existing flags");
            using(var cancel=new CancellationTokenSource())
            {cancel.Cancel();bool stopped=false;try{await service.ApplyAsync(r with{Option=MembershipOption.Set},cancel.Token);}catch(OperationCanceledException){stopped=true;}check(stopped && await Options()==(false,false,false),"Pre-canceled option change leaves catalog flags intact");}
        }
        finally {await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qm},{qo},{qg};");}
    }
}
