using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceAttachChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string name="pgms_attach_"+Guid.NewGuid().ToString("N"), seq=ObjectSql.Relation("public",name), table=ObjectSql.Relation("public",name+"_t");
        await using var db=new NpgsqlConnection(connection); await db.OpenAsync();
        async Task<object?> Sql(string sql) { await using var c=new NpgsqlCommand(sql,db); return await c.ExecuteScalarAsync(); }
        var sequences=new SequenceService(connection); var targets=new SequenceOwnerTargetService(connection); var edits=new AdministrativeEditService(connection);
        try
        {
            await Sql($"CREATE SEQUENCE {seq} START 10; CREATE TABLE {table}(id bigint DEFAULT 42, other bigint);");
            var before=await sequences.InspectAsync("public",name); var target=await targets.InspectAsync("public",name+"_t","id");
            var edit=new SequenceAttachEdit(before,target);
            bool Reject(SequenceSnapshot source,SequenceOwnerTarget destination) { try { new SequenceOwnershipProposal(source,destination).ToSql(); return false; } catch(ArgumentException) { return true; } }
            check(Reject(before,target with {Schema="other"}),"Association refuses cross-schema proposal");
            check(Reject(before,target with {Owner="other"}),"Association refuses different owners");
            check(Reject(before,target with {Identity="a"}) && Reject(before,target with {Generated="s"}),"Association refuses identity and generated destinations");
            check(Reject(before,target with {Kind="v"}) && Reject(before,target with {Persistence="u"}),"Association refuses unsupported table kinds and persistence");
            check(Reject(before with {Dependency=new(target.TableOid,1,"i")},target),"Association refuses identity source sequence");
            async Task<bool> Conflict(SequenceAttachEdit proposal) { try { await edits.ApplyAsync(proposal); return false; } catch(InvalidOperationException) { return true; } }
            await Sql($"ALTER TABLE {table} ALTER COLUMN id TYPE integer;");
            check(await Conflict(edit),"Association refuses changed destination type");
            await Sql($"ALTER TABLE {table} DROP COLUMN id; ALTER TABLE {table} ADD COLUMN id bigint DEFAULT 42;");
            check(await Conflict(edit),"Association refuses recreated destination column");
            await Sql($"DROP TABLE {table}; CREATE TABLE {table}(id bigint DEFAULT 42,other bigint);");
            check(await Conflict(edit),"Association refuses replacement destination table");
            target=await targets.InspectAsync("public",name+"_t","id"); edit=new(before,target);
            await Sql($"ALTER SEQUENCE {seq} CACHE 2;");
            check(await Conflict(edit),"Association refuses changed sequence configuration");
            await Sql($"ALTER SEQUENCE {seq} CACHE 1 OWNED BY {table}.other;");
            check(await Conflict(edit),"Association refuses concurrent reassociation");
            await Sql($"DROP SEQUENCE {seq}; CREATE SEQUENCE {seq} START 10;");
            check(await Conflict(edit),"Association refuses replacement source sequence");
            before=await sequences.InspectAsync("public",name); edit=new(before,target);
            await using(var tx=await db.BeginTransactionAsync())
            {
                await edit.ValidateAsync(db,tx); await using var command=new NpgsqlCommand(edit.ToSql(),db,tx); await command.ExecuteNonQueryAsync();
                await edit.VerifyAppliedAsync(db,tx); await tx.RollbackAsync();
            }
            check(await sequences.InspectAsync("public",name)==before,"Association rollback restores prior metadata");
            await using(var tx=await db.BeginTransactionAsync())
            {
                await using var command=new NpgsqlCommand($"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE",db,tx); await command.ExecuteNonQueryAsync();
                using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(2)); bool stopped=false;
                try { await edits.ApplyAsync(edit,cancel.Token); } catch(OperationCanceledException) { stopped=true; }
                check(stopped,"Association awaiting destination lock honors cancellation");
                await tx.RollbackAsync();
            }
            string role="pgms_attach_reader_"+Guid.NewGuid().ToString("N");
            try
            {
                await Sql($"CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN; GRANT SELECT ON {table} TO {SqlIdentifier.Quote(role)}; GRANT SELECT ON SEQUENCE {seq} TO {SqlIdentifier.Quote(role)};");
                var limited=new AdministrativeEditService(new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString);
                bool denied=false; try { await limited.ApplyAsync(edit); } catch(PostgresException ex) when(ex.SqlState=="42501") { denied=true; }
                check(denied && await sequences.InspectAsync("public",name)==before,"SELECT grants alone cannot associate a sequence");
            }
            finally { await Sql($"REVOKE ALL ON {table} FROM {SqlIdentifier.Quote(role)}; REVOKE ALL ON SEQUENCE {seq} FROM {SqlIdentifier.Quote(role)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
            object? state=await Sql($"SELECT last_value::text||':'||is_called::text FROM {seq}");
            await edits.ApplyAsync(edit);
            var after=await sequences.InspectAsync("public",name);
            check(after.Dependency==new SequenceDependency(target.TableOid,target.ColumnNumber,"a"),"Association writes exact target OID and attnum");
            check(after with {OwnedBy=before.OwnedBy,Dependency=before.Dependency}==before,"Association preserves all other sequence metadata");
            check(Equals(state,await Sql($"SELECT last_value::text||':'||is_called::text FROM {seq}")),"Association preserves current sequence state");
            check(Equals(await Sql($"INSERT INTO {table} DEFAULT VALUES RETURNING id"),42L),"Association does not replace column default");
            check(Reject(after,target),"Association refuses no-op target");
            await edits.ApplyAsync(new SequenceAttachEdit(after,await targets.InspectAsync("public",name+"_t","other")));
            check((await sequences.InspectAsync("public",name)).Dependency?.ColumnNumber==2,"Association can move to another common column");
            await Sql($"ALTER TABLE {table} DROP COLUMN other;");
            bool absent=false; try { await sequences.InspectAsync("public",name); } catch(InvalidOperationException) { absent=true; }
            check(absent,"Dropping associated column removes sequence through PostgreSQL dependency");
        }
        finally { await Sql($"DROP TABLE IF EXISTS {table}; DROP SEQUENCE IF EXISTS {seq};"); }
    }
}
