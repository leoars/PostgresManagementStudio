using Npgsql;
using PGMS.PostgreSql;

internal static class IdentitySequenceParameterChecks
{
    private sealed record RejectAfterApply(IdentityColumnEdit Inner):IAdministrativeEdit
    {
        public string ToSql()=>Inner.ToSql();
        public Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default)=>Inner.ValidateAsync(c,t,token);
        public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction t,CancellationToken token=default){await Inner.VerifyAppliedAsync(c,t,token);throw new IOException("Synthetic identity configuration verification failure.");}
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="identity_parameters_"+suffix,owner="identity_parameters_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner),table=ObjectSql.Relation(schema,"items");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Admin(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        try
        {
            await Admin($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            await using var client=new NpgsqlConnection(limited);await client.OpenAsync();
            async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,client);return await c.ExecuteScalarAsync();}
            await Scalar($"CREATE TABLE {table}(id integer GENERATED ALWAYS AS IDENTITY)");
            var service=new AdministrativeEditService(limited);var inspect=new IdentityColumnService(limited);
            Task<IdentityColumnSnapshot> Read()=>inspect.InspectAsync(schema,"items","id");
            async Task<long> Insert()=>Convert.ToInt64(await Scalar($"INSERT INTO {table} DEFAULT VALUES RETURNING id"));
            var initial=await Read();check(await Insert()==1,"Identity parameter fixture starts with a real generated value");
            await service.ApplyAsync(new IdentityColumnEdit(initial,IdentityColumnOperation.SetStart){ParameterValue=10});var current=await Read();
            check(current.Sequence==initial.Sequence! with{Start=10},"Configured identity START changes only its intended catalog field");
            check(await Insert()==2,"Changing configured identity START does not reset the next value");
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetIncrement){ParameterValue=5});current=await Read();
            check(current.Sequence!.Increment==5 && current.Sequence.Start==10 && await Insert()==7,"Identity increment affects real subsequent generation and preserves START");
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetBounds){ParameterValue=1,Maximum=20});current=await Read();check(current.Sequence is{Minimum:1,Maximum:20,Increment:5,Start:10},"Native identity bounds update preserves unrelated configuration");
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetCache){ParameterValue=2});current=await Read();check(current.Sequence is{Cache:2,Minimum:1,Maximum:20},"Identity cache changes through the selected column");
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetCycle){Cycle=true});current=await Read();check(current.Sequence!.Cycle,"Identity CYCLE is inspected after transactional Apply");
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.Restart,20));check(await Insert()==20 && await Insert()==1,"Identity CYCLE actually wraps at the configured upper bound");
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetCycle){Cycle=false});current=await Read();check(!current.Sequence!.Cycle,"Identity NO CYCLE disables wrapping");
            var stale=current;await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetCache){ParameterValue=3});
            bool conflict=false;try{await service.ApplyAsync(new IdentityColumnEdit(stale,IdentityColumnOperation.SetIncrement){ParameterValue=2});}catch(InvalidOperationException ex)when(ex.Message.Contains("changed")){conflict=true;}
            check(conflict && (await Read()).Sequence is{Cache:3,Increment:5},"Concurrent identity configuration change refuses a stale parameter proposal");
            current=await Read();bool rolledBack=false;try{await service.ApplyAsync(new RejectAfterApply(new IdentityColumnEdit(current,IdentityColumnOperation.SetCache){ParameterValue=7}));}catch(IOException){rolledBack=true;}
            check(rolledBack && await Read()==current,"Identity configuration verification failure rolls back the native ALTER");
            foreach(var invalid in new[]{new IdentityColumnEdit(current,IdentityColumnOperation.SetIncrement){ParameterValue=0},new(current,IdentityColumnOperation.SetIncrement){ParameterValue=long.MaxValue},new(current,IdentityColumnOperation.SetCache){ParameterValue=0},new(current,IdentityColumnOperation.SetStart){ParameterValue=21},new(current,IdentityColumnOperation.SetBounds){ParameterValue=11,Maximum=20},new(current,IdentityColumnOperation.SetBounds){ParameterValue=-2147483649L,Maximum=20},new(current,IdentityColumnOperation.SetBounds){ParameterValue=20,Maximum=1},new(current,IdentityColumnOperation.SetCycle),new(current,IdentityColumnOperation.SetAlways){ParameterValue=4},new(current,IdentityColumnOperation.SetCache){ParameterValue=1,Maximum=20}})
            {bool refused=false;try{invalid.ToSql();}catch(ArgumentException){refused=true;}check(refused,"Invalid or unrelated identity parameters are refused before SQL: "+invalid.Operation);}
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetCache){ParameterValue=1});current=await Read();
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetBounds){ParameterValue=-20,Maximum=20});current=await Read();
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetIncrement){ParameterValue=-1});current=await Read();
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.SetCycle){Cycle=true});current=await Read();
            await service.ApplyAsync(new IdentityColumnEdit(current,IdentityColumnOperation.Restart,-20));check(await Insert()==-20 && await Insert()==20,"Descending identity with negative bounds wraps at the configured lower bound");
            check(current.Sequence!.Oid==initial.Sequence!.Oid && current.TableOid==initial.TableOid && current.Generation=="a","Identity parameter edits preserve the original table, sequence and generation identities");
        }
        finally{await Admin($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qo}");}
    }
}
