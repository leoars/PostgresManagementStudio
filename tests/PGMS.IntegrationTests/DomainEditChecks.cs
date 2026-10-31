using Npgsql;
using PGMS.PostgreSql;

internal static class DomainEditChecks
{
    private sealed class Interleave(IAdministrativeEdit edit,Func<Task> action):IAdministrativeEdit
    {
        public string ToSql()=>edit.ToSql();
        public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token){await edit.ValidateAsync(c,tx,token);await action();}
        public Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token)=>edit.VerifyAppliedAsync(c,tx,token);
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Owner($"CREATE DOMAIN {f.QS}.amount AS numeric(8,2) DEFAULT 4.25;CREATE TABLE {f.QS}.values(v {f.QS}.amount);INSERT INTO {f.QS}.values VALUES(-1),(2)");
        var catalog=new DomainCatalog(f.CreatorConnection);var service=new AdministrativeEditService(f.CreatorConnection);
        Task<DomainSnapshot> Inspect()=>catalog.InspectAsync(f.Schema,"amount");
        var original=await Inspect();
        check(original.Type.BaseTypeOid==1700 && original.BaseTypeSql=="numeric(8,2)" && original.DefaultSql=="4.25" && !original.NotNull,"Domain inspection captures base modifier, default and nullability");
        var rule=new DomainRule(IndexComparison.Greater,"0");
        bool rejected=false;try{await service.ApplyAsync(new DomainConstraintEdit(original,DomainConstraintOperation.Add,"positive",rule));}catch(PostgresException e)when(e.SqlState=="23514"){rejected=true;}
        check(rejected && (await Inspect()).Constraints.Count==0,"Validated domain ADD checks existing rows and rolls back on violations");
        await service.ApplyAsync(new DomainConstraintEdit(await Inspect(),DomainConstraintOperation.Add,"positive",rule,notValid:true));
        var pending=await Inspect();
        check(pending.Constraints.Single().Name=="positive" && !pending.Constraints[0].Validated,"NOT VALID domain constraint retains pending validation state");
        rejected=false;try{await f.Owner($"INSERT INTO {f.QS}.values VALUES(-2)");}catch(PostgresException e)when(e.SqlState=="23514"){rejected=true;}
        check(rejected,"NOT VALID domain constraint already rejects new invalid values");
        rejected=false;try{await service.ApplyAsync(new DomainConstraintEdit(pending,DomainConstraintOperation.Validate,"positive"));}catch(PostgresException e)when(e.SqlState=="23514"){rejected=true;}
        check(rejected && !(await Inspect()).Constraints[0].Validated,"Failed domain validation preserves the unvalidated constraint");
        await f.Owner($"UPDATE {f.QS}.values SET v=1 WHERE v<0");
        await service.ApplyAsync(new DomainConstraintEdit(await Inspect(),DomainConstraintOperation.Validate,"positive"));
        var validated=await Inspect();
        check(validated.Constraints[0].Validated && validated.Constraints[0].Oid==pending.Constraints[0].Oid,"Domain validation preserves original constraint OID after data repair");
        await service.ApplyAsync(new DomainConstraintEdit(validated,DomainConstraintOperation.Add,"upper",new(IndexComparison.LessOrEqual,"100")));
        var both=await Inspect();
        await service.ApplyAsync(new DomainConstraintEdit(both,DomainConstraintOperation.Drop,"positive"));
        var dropped=await Inspect();
        check(dropped.Constraints.Single()==both.Constraints.Single(x=>x.Name=="upper") && dropped.DefaultSql==original.DefaultSql,"Domain DROP RESTRICT preserves unrelated constraint and default");
        await f.Owner($"INSERT INTO {f.QS}.values VALUES(-3)");
        check(true,"Removing domain lower bound permits actual negative value again");
        bool stale=false;try{await service.ApplyAsync(new DomainConstraintEdit(both,DomainConstraintOperation.Drop,"upper"));}catch(InvalidOperationException){stale=true;}
        check(stale,"Domain mutation refuses stale constraint set");
        bool concurrent=false;
        try{await service.ApplyAsync(new Interleave(new DomainConstraintEdit(await Inspect(),DomainConstraintOperation.Add,"ours",new(IndexComparison.Greater,"-10")),()=>f.Owner($"ALTER DOMAIN {f.QS}.amount ADD CONSTRAINT theirs CHECK(VALUE<>99)")));}catch(InvalidOperationException){concurrent=true;}
        var after=await Inspect();
        check(concurrent && after.Constraints.Any(x=>x.Name=="theirs") && !after.Constraints.Any(x=>x.Name=="ours"),"Concurrent domain constraint between validation and DDL rolls back only our change");
        await f.Owner($"GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");
        bool denied=false;try{await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(new DomainConstraintEdit(await Inspect(),DomainConstraintOperation.Drop,"upper"));}catch(PostgresException e)when(e.SqlState=="42501"){denied=true;}
        check(denied,"USAGE-only role cannot administer domain constraints");
        await f.Owner($"CREATE DOMAIN {f.QS}.replaceable AS int");var old=await catalog.InspectAsync(f.Schema,"replaceable");
        await f.Owner($"DROP DOMAIN {f.QS}.replaceable;CREATE DOMAIN {f.QS}.replaceable AS int");
        bool replaced=false;try{await service.ApplyAsync(new DomainConstraintEdit(old,DomainConstraintOperation.Add,"new",new(IndexComparison.IsNotNull)));}catch(InvalidOperationException){replaced=true;}
        check(replaced,"Domain edit refuses same-name replacement");
        await f.Owner($"CREATE DOMAIN {f.QS}.words AS text");
        string literal="ação漢字'\\;--";
        await service.ApplyAsync(new DomainConstraintEdit(await catalog.InspectAsync(f.Schema,"words"),DomainConstraintOperation.Add,"exact",new(IndexComparison.Equal,literal)));
        await using(var c=new NpgsqlConnection(f.CreatorConnection))
        {await c.OpenAsync();await using var cmd=new NpgsqlCommand($"SELECT @value::{f.QS}.words::text",c);cmd.Parameters.AddWithValue("value",literal);check((string)(await cmd.ExecuteScalarAsync())! ==literal,"Typed domain rule safely roundtrips Unicode quotes backslash and terminator as literal");}
        bool invalid=false;try{new DomainConstraintEdit(await Inspect(),DomainConstraintOperation.Add,"unused",new(IndexComparison.IsNull,"ignored")).ToSql();}catch(ArgumentException){invalid=true;}
        check(invalid,"Domain NULL rule rejects an unused literal");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool cancel=false;
        try{await catalog.InspectAsync(f.Schema,"amount",canceled.Token);}catch(OperationCanceledException){cancel=true;}
        check(cancel,"Domain inspection honors pre-cancellation");
    }
}
