using Npgsql;
using PGMS.PostgreSql;

internal static class GrantorCatalogChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string child="grantor_child_"+Guid.NewGuid().ToString("N"),qc=SqlIdentifier.Quote(child);
        await f.Admin($"CREATE ROLE {qc};GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");
        try
        {
            await f.Owner($"CREATE TABLE {f.QS}.target(value integer,other integer)");
            var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",child,"SELECT",Schema:f.Schema);
            var catalog=new PrivilegeGrantorCatalog(f.RecipientConnection);
            var initial=await catalog.InspectAsync(request);
            var owner=initial.Candidates.Single(x=>x.Name==f.Creator);var reader=initial.Candidates.Single(x=>x.Name==f.Recipient);
            check(initial.Target.ObjectOid!=0 && initial.Target.DatabaseOid!=0 && initial.OwnerOid==owner.Oid && initial.CurrentRoleOid==reader.Oid && initial.SessionRoleOid==reader.Oid,"Grantor inspection captures target/owner/current/session identities");
            check(owner.IsOwner && owner.DirectGrantOption && owner.EffectiveGrantOption && !owner.CanSetRole,"Owner implicit grant authority is distinguished from caller SET permission");
            check(!reader.DirectGrantOption && !reader.EffectiveGrantOption && reader.CanSetRole,"Current role is listed without inventing grant authority");
            await f.Owner($"GRANT SELECT ON {f.QS}.target TO {f.QR} WITH GRANT OPTION;GRANT UPDATE ON {f.QS}.target TO {qc} WITH GRANT OPTION;GRANT SELECT ON {f.QS}.target TO PUBLIC");
            await f.Reader($"GRANT SELECT ON {f.QS}.target TO {qc}");
            var delegated=await catalog.InspectAsync(request);reader=delegated.Candidates.Single(x=>x.Name==f.Recipient);
            check(reader.DirectGrantOption && reader.EffectiveGrantOption && reader.AppearsAsGrantor,"Actual delegated SELECT identifies recipient as an ACL grantor");
            check(delegated.Candidates.All(x=>x.Name!=child && x.Oid!=0),"Unrelated UPDATE grant option and PUBLIC do not become SELECT grantor candidates");
            await f.Admin($"GRANT {f.QO} TO {f.QR} WITH INHERIT FALSE,SET TRUE");
            var setOnly=(await catalog.InspectAsync(request)).Candidates.Single(x=>x.Name==f.Creator);
            check(setOnly.CanSetRole && !setOnly.AvailableWithoutSet,"SET-only membership is distinct from inherited authority");
            await f.Admin($"GRANT {f.QO} TO {f.QR} WITH INHERIT TRUE,SET FALSE");
            var inheritOnly=(await catalog.InspectAsync(request)).Candidates.Single(x=>x.Name==f.Creator);
            check(!inheritOnly.CanSetRole && inheritOnly.AvailableWithoutSet,"INHERIT-only membership does not authorize explicit SET ROLE");
            await f.Admin($"REVOKE {f.QO} FROM {f.QR}");
            await f.Owner($"CREATE TABLE {f.QS}.columns_only(value integer,other integer);GRANT SELECT(value) ON {f.QS}.columns_only TO {f.QR} WITH GRANT OPTION");
            var columnRequest=request with{Name="columns_only",Column="value"};
            var columns=await catalog.InspectAsync(columnRequest);reader=columns.Candidates.Single(x=>x.Name==f.Recipient);
            check(columns.Target.ColumnNumber==1 && reader.DirectGrantOption && reader.EffectiveGrantOption,"Selected column captures attnum and direct column grant authority");
            check(!(await catalog.InspectAsync(columnRequest with{Column=null})).Candidates.Single(x=>x.Name==f.Recipient).DirectGrantOption,"Column-only option does not authorize whole-table grant");
            check(!(await catalog.InspectAsync(columnRequest with{Column="other"})).Candidates.Single(x=>x.Name==f.Recipient).EffectiveGrantOption,"Column option does not authorize another column");
            await f.Owner($"GRANT SELECT ON {f.QS}.columns_only TO {f.QR} WITH GRANT OPTION");
            check((await catalog.InspectAsync(columnRequest with{Column="other"})).Candidates.Single(x=>x.Name==f.Recipient).DirectGrantOption,"Whole-table grant option also authorizes selected column");
            await f.Owner($"CREATE TABLE {f.QS}.empty_acl(value integer);REVOKE ALL ON {f.QS}.empty_acl FROM {f.QO}");
            var empty=await catalog.InspectAsync(request with{Name="empty_acl"});
            check(empty.Candidates.Single(x=>x.Name==f.Creator).DirectGrantOption && !empty.Candidates.Single(x=>x.Name==f.Creator).AppearsAsGrantor,"Explicit empty ACL keeps implicit owner authority without inventing stored origin");
            var schema=await catalog.InspectAsync(new(PrivilegeTarget.Schema,f.Schema,child,"USAGE"));
            check(schema.OwnerOid==initial.OwnerOid && schema.Candidates.Single(x=>x.Name==f.Creator).DirectGrantOption,"Schema grantor inspection uses schema ownership");
            var database=await catalog.InspectAsync(new(PrivilegeTarget.Database,new NpgsqlConnectionStringBuilder(connection).Database!,child,"CONNECT"));
            check(database.OwnerOid!=0 && database.Candidates.Any(x=>x.IsOwner),"Database grantor inspection resolves database owner independently");
            await f.Owner($"CREATE SEQUENCE {f.QS}.seq;GRANT USAGE ON SEQUENCE {f.QS}.seq TO {f.QR} WITH GRANT OPTION;CREATE FUNCTION {f.QS}.fn(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';GRANT EXECUTE ON FUNCTION {f.QS}.fn(integer) TO {f.QR} WITH GRANT OPTION");
            check((await catalog.InspectAsync(new(PrivilegeTarget.Sequence,"seq",child,"USAGE",Schema:f.Schema))).Candidates.Single(x=>x.Name==f.Recipient).DirectGrantOption,"Sequence grantor inspection captures USAGE grant option");
            await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
            await using var oidCommand=new NpgsqlCommand("SELECT @name::regprocedure::oid",admin);oidCommand.Parameters.AddWithValue("name",f.QS+".fn(integer)");
            var routine=await new RoutinePrivilegeCatalog(connection).InspectAsync((uint)(await oidCommand.ExecuteScalarAsync())!);
            var function=await catalog.InspectAsync(new(PrivilegeTarget.Routine,"fn",child,"EXECUTE",Schema:f.Schema){Routine=routine});
            check(function.Target.Routine==routine && function.Candidates.Single(x=>x.Name==f.Recipient).DirectGrantOption,"Routine grantor inspection retains exact overload identity");
            var after=await new EffectivePrivilegeService(f.RecipientConnection).InspectAsync(PrivilegeTarget.Table,"target",f.Schema);
            check(after.AclEntries.Any(x=>x.Grantee==child && x.Grantor==f.Recipient && x.Privilege=="SELECT"),"Read-only grantor inquiries preserve delegated ACL origin");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
            try{await new PrivilegeGrantorCatalog("invalid").InspectAsync(request,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped,"Pre-cancelled grantor inspection does not parse or open a connection");
            bool immutable=false;try{((IList<PrivilegeGrantorCandidate>)initial.Candidates).Clear();}catch(NotSupportedException){immutable=true;}
            check(immutable,"Grantor candidate snapshot cannot be edited through collection interface");
            string prefix="grantor_limit_"+Guid.NewGuid().ToString("N");
            string[] limited=Enumerable.Range(0,PrivilegeGrantorCatalog.MaximumCandidates+1).Select(i=>SqlIdentifier.Quote(prefix+"_"+i)).ToArray();
            await f.Admin(string.Join(";",limited.Select(x=>"CREATE ROLE "+x)));
            try
            {
                await f.Owner($"GRANT SELECT ON {f.QS}.target TO {string.Join(',',limited)} WITH GRANT OPTION");
                var bounded=await catalog.InspectAsync(request);
                check(bounded.Truncated && bounded.Candidates.Count==PrivilegeGrantorCatalog.MaximumCandidates,"Grantor candidate quota reports an incomplete observation explicitly");
                check(bounded.Candidates.Select(x=>x.Oid).Distinct().Count()==bounded.Candidates.Count,"Bounded grantor candidates retain unique identities");
            }
            finally{await f.Admin($"DROP OWNED BY {string.Join(',',limited)};DROP ROLE {string.Join(',',limited)}");}
        }
        finally{await f.Admin($"DROP SCHEMA IF EXISTS {f.QS} CASCADE;DROP OWNED BY {qc};DROP ROLE {qc}");}
    }
}
