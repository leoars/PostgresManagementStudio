using Npgsql;
using PGMS.PostgreSql;

internal static class RoutinePrivilegeChecks
{
    private sealed class CapturingJournal : IPrivilegeCommitJournal
    {
        public PrivilegeCommitReceipt? Receipt { get; private set; }
        public bool Fail { get; set; }
        public Task RecordAsync(PrivilegeCommitReceipt receipt,CancellationToken token)
        { token.ThrowIfCancellationRequested();Receipt=receipt;if(Fail)throw new IOException("Synthetic journal failure");return Task.CompletedTask; }
    }
    internal static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="rotina \" 漢"+suffix, owner="routine_owner_"+suffix,
            worker="routine_worker_"+suffix, child="routine_child_"+suffix;
        string qs=SqlIdentifier.Quote(schema), qo=SqlIdentifier.Quote(owner), qw=SqlIdentifier.Quote(worker), qc=SqlIdentifier.Quote(child);
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        string As(string role)=>new NpgsqlConnectionStringBuilder(connection){Username=role,Pooling=false}.ConnectionString;
        async Task<uint> Oid(string signature)
        {
            await using var cmd=new NpgsqlCommand("SELECT @signature::regprocedure::oid",admin);
            cmd.Parameters.AddWithValue("signature",signature);return (uint)(await cmd.ExecuteScalarAsync())!;
        }
        async Task<bool> Execute(string sql)
        {
            await using var client=new NpgsqlConnection(As(worker));await client.OpenAsync();
            try{await using var cmd=new NpgsqlCommand(sql,client);await cmd.ExecuteNonQueryAsync();return true;}
            catch(PostgresException ex)when(ex.SqlState=="42501"){return false;}
        }
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qw} LOGIN;CREATE ROLE {qc} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};GRANT USAGE ON SCHEMA {qs} TO {qw},{qc}");
            await Sql($"""
                SET ROLE {qo};
                CREATE FUNCTION {qs}.overload(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';
                CREATE FUNCTION {qs}.overload(text) RETURNS text LANGUAGE sql AS 'SELECT $1';
                CREATE FUNCTION {qs}.zero() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                CREATE FUNCTION {qs}.outs(IN i integer, OUT value text) LANGUAGE sql AS 'SELECT i::text';
                CREATE FUNCTION {qs}.vari(VARIADIC items integer[]) RETURNS integer LANGUAGE sql AS 'SELECT 1';
                CREATE PROCEDURE {qs}.proc(INOUT value integer) LANGUAGE plpgsql AS 'BEGIN value:=value+1; END';
                CREATE DOMAIN {qs}."type ; "" 漢" AS integer;
                CREATE FUNCTION {qs}.typed({qs}."type ; "" 漢") RETURNS integer LANGUAGE sql AS 'SELECT $1::integer';
                RESET ROLE;
                """);
            var catalog=new RoutinePrivilegeCatalog(As(owner));var service=new PrivilegeService(As(owner));
            async Task<RoutinePrivilegeSelection> Select(string signature)=>await catalog.InspectAsync(await Oid(signature));
            var routine=await Select($"{qs}.overload(integer)");
            check(routine==await catalog.InspectAsync(routine.ObjectOid),"Repeated routine snapshots compare by value, including canonical input OIDs");
            check(routine.InputTypeOids=="23" && routine.ArgumentSql=="\"pg_catalog\".\"int4\"","Routine captures exact input type identity and qualified internal type name");
            PrivilegeRequest Request(RoutinePrivilegeSelection selected,string role)=>new(PrivilegeTarget.Routine,selected.Name,role,"EXECUTE",Schema:selected.Schema){Routine=selected};
            var request=Request(routine,worker);
            check(request.ToSql()==$"GRANT EXECUTE ON ROUTINE {routine.QualifiedSignature} TO {qw};","Routine SQL includes exact qualified overload");
            bool rejected=false;try{(request with{Routine=null}).ToSql();}catch(ArgumentException){rejected=true;}
            check(rejected,"Routine name without captured signature is rejected");
            rejected=false;try{(request with{Privilege="SELECT"}).ToSql();}catch(ArgumentException){rejected=true;}
            check(rejected,"Routine rejects privileges other than EXECUTE");
            async Task<EffectivePrivilegeSnapshot> Inspect(RoutinePrivilegeSelection r,string role)=>await new EffectivePrivilegeService(As(role)).InspectAsync(PrivilegeTarget.Routine,r.Name,r.Schema,routine:r);
            var initial=await Inspect(routine,worker);
            check(initial.Privileges.Single().Allowed && initial.AclEntries.Any(e=>e.GranteeOid==0 && e.Source==PrivilegeAclSource.BuiltInDefault),"Routine null ACL exposes built-in PUBLIC EXECUTE");
            check(await service.ApplyCurrentAsync(request with{RecipientIsPublic=true,Role="",Revoke=true}),"Owner revokes PUBLIC EXECUTE with catalog identity guard");
            check(!await Execute($"SELECT {qs}.overload(1)"),"Revoked overload refuses real execution by ordinary role");
            check(await Execute($"SELECT {qs}.overload('other'::text)"),"Different overload retains independent PUBLIC execution");
            var state=await Inspect(routine,owner);var recipient=(await service.InspectRecipientAsync(worker))!;
            check(await service.ApplyInspectedAsync(request with{GrantOption=true},state.TargetIdentity!,recipient),"Limited owner grants EXECUTE with target and recipient snapshots");
            check(await Execute($"SELECT {qs}.overload(1)"),"Explicit grant enables real execution");
            check(!await service.ApplyCurrentAsync(request),"Idempotent routine grant reports no committed ACL delta");
            var delegateService=new PrivilegeService(As(worker));await delegateService.ApplyAsync(request with{Role=child});
            rejected=false;try{await service.ApplyAsync(request with{Revoke=true,GrantOption=true});}catch(PostgresException ex)when(ex.SqlState=="2BP01"){rejected=true;}
            check(rejected && await Execute($"SELECT {qs}.overload(1)"),"RESTRICT preserves delegated routine privileges");
            await delegateService.ApplyAsync(request with{Role=child,Revoke=true});
            await service.ApplyAsync(request with{Revoke=true,GrantOption=true});
            state=await Inspect(routine,worker);
            check(state.Privileges.Single() is{Allowed:true,GrantOption:false},"Routine grant-option revoke preserves base EXECUTE");
            foreach(var signature in new[]{ $"{qs}.zero()",$"{qs}.outs(integer)",$"{qs}.vari(integer[])",$"{qs}.proc(integer)",$"{qs}.typed({qs}.\"type ; \"\" 漢\")" })
            {
                var selected=await Select(signature);var selectedRequest=Request(selected,worker);
                check(await service.ApplyCurrentAsync(selectedRequest with{RecipientIsPublic=true,Role="",Revoke=true}),"Exact catalog signature supports PUBLIC revoke: "+selected.Name);
                check(await service.ApplyCurrentAsync(selectedRequest),"Exact catalog signature supports grant: "+selected.Name);
            }
            check(await Execute($"CALL {qs}.proc(1)"),"Procedure INOUT signature grants actual CALL");
            check(await Execute($"SELECT {qs}.outs(1),{qs}.vari(1,2),{qs}.zero(),{qs}.typed(1::{qs}.\"type ; \"\" 漢\")"),"OUT exclusion, variadic arrays, zero arguments and quoted domain execute successfully");
            var journal=new CapturingJournal();
            var journalService=new PrivilegeService(As(owner),journal);
            check(await journalService.ApplyCurrentAsync(request with{Revoke=true}),"Routine revoke uses common commit journal");
            check(journal.Receipt is not null && await journalService.ReconcileCommitAsync(journal.Receipt)==PrivilegeCommitStatus.Committed,"Routine transaction receipt confirms committed without replay");
            journal.Fail=true;
            rejected=false;try{await journalService.ApplyAsync(request);}catch(IOException){rejected=true;}
            check(rejected && !await Execute($"SELECT {qs}.overload(1)"),"Journal failure before COMMIT rolls back routine EXECUTE");
            check(await journalService.ReconcileCommitAsync(journal.Receipt!)==PrivilegeCommitStatus.Aborted,"Failed routine journal transaction reconciles as aborted");
            var oldRecipient=(await service.InspectRecipientAsync(worker))!;
            await Sql($"ALTER ROLE {qw} RENAME TO {SqlIdentifier.Quote(worker+"_old")};CREATE ROLE {qw} LOGIN;GRANT USAGE ON SCHEMA {qs} TO {qw}");
            try
            {
                rejected=false;try{await service.ApplyInspectedAsync(request,initial.TargetIdentity!,oldRecipient);}catch(InvalidOperationException){rejected=true;}
                check(rejected && !await Execute($"SELECT {qs}.overload(1)"),"Recreated recipient cannot receive grant from stale inspected role OID");
            }
            finally{await Sql($"DROP OWNED BY {qw};DROP ROLE {qw};ALTER ROLE {SqlIdentifier.Quote(worker+"_old")} RENAME TO {qw}");}
            var domainRoutine=await Select($"{qs}.typed({qs}.\"type ; \"\" 漢\")");
            await Sql($"ALTER DOMAIN {qs}.\"type ; \"\" 漢\" RENAME TO renamed_type");
            rejected=false;try{await service.ApplyAsync(Request(domainRoutine,worker) with{Revoke=true});}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Renamed argument type invalidates captured routine signature");
            var refreshedDomain=await catalog.InspectAsync(domainRoutine.ObjectOid);
            check(refreshedDomain.InputTypeOids==domainRoutine.InputTypeOids && refreshedDomain.ArgumentSql!=domainRoutine.ArgumentSql,"Explicit catalog reload preserves type OID and captures changed qualified type name");
            check(await service.ApplyCurrentAsync(Request(refreshedDomain,worker) with{Revoke=true}),"Reloaded argument type permits guarded routine mutation");
            await Sql($"DROP FUNCTION {qs}.overload(integer);SET ROLE {qo};CREATE FUNCTION {qs}.overload(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';RESET ROLE");
            rejected=false;try{await service.ApplyAsync(request);}catch(InvalidOperationException){rejected=true;}
            check(rejected,"Recreated overload cannot inherit stale routine selection");
            check((await Select($"{qs}.overload(integer)")).ObjectOid!=routine.ObjectOid,"Replacement overload has a distinct OID");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            rejected=false;try{await catalog.InspectAsync(0,cancelled.Token);}catch(OperationCanceledException){rejected=true;}
            check(rejected,"Pre-cancelled routine inspection does not connect or resolve arguments");
        }
        finally{await Sql($"RESET ROLE;DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qo};DROP ROLE IF EXISTS {qw};DROP ROLE IF EXISTS {qc}");}
    }
}
