using Npgsql;
using PGMS.PostgreSql;

internal static class ConcurrentIndexPreflightChecks
{
    internal static async Task RunAsync(string connection, Action<bool,string> check)
    {
        await using var fixture = await FutureDefaultFixture.CreateAsync(connection);
        await fixture.Owner($"CREATE TABLE {fixture.QS}.target(id integer, label text); INSERT INTO {fixture.QS}.target VALUES(1,'a'),(1,'b')");
        var catalog = new ExpressionIndexCatalog(fixture.CreatorConnection);
        var target = await catalog.InspectAsync(fixture.Schema,"target");
        var proposal = new ExpressionIndexEdit(target,"concurrent_idx",[new(new("label",IndexTransform.Lower))],true);
        var inspector = new ConcurrentIndexInspector(fixture.CreatorConnection);
        var ready = await inspector.InspectAsync(proposal);
        check(ready.EligibleAtInspection && ready.Sql.StartsWith("CREATE UNIQUE INDEX CONCURRENTLY "),"Concurrent preflight generates standalone SQL for eligible owner");
        check(!ready.Sql.Contains("BEGIN") && !ready.Sql.Contains("IF NOT EXISTS"),"Concurrent proposal omits transaction wrapper and false reconciliation");
        bool immutable=false;try { ((IList<string>)ready.Blockers).Add("altered"); } catch(NotSupportedException) { immutable=true; }
        check(immutable,"Preflight blockers cannot be mutated by callers");
        await using var owner = new NpgsqlConnection(fixture.CreatorConnection); await owner.OpenAsync();
        async Task<object?> Scalar(string sql) { await using var cmd=new NpgsqlCommand(sql,owner);return await cmd.ExecuteScalarAsync(); }
        check((long)(await Scalar($"SELECT count(*) FROM pg_class WHERE relnamespace='{fixture.QS}'::regnamespace AND relname='concurrent_idx'"))! == 0,"Preflight does not create the proposed index");
        var denied = await new ConcurrentIndexInspector(fixture.RecipientConnection).InspectAsync(proposal);
        check(denied.Blockers.Any(x=>x.Contains("owner")),"Role without ownership cannot pass concurrent preflight");
        await fixture.Owner($"CREATE SEQUENCE {fixture.QS}.concurrent_idx");
        check((await inspector.InspectAsync(proposal)).Blockers.Any(x=>x.Contains("occupies")),"A sequence occupying index name blocks preflight");
        await fixture.Owner($"DROP SEQUENCE {fixture.QS}.concurrent_idx");
        var invalidProposal = new ExpressionIndexEdit(target,"invalid_idx",[new(new("id"))],true);
        var invalidPreflight = await inspector.InspectAsync(invalidProposal);
        check(invalidPreflight.EligibleAtInspection,"Preflight does not claim to prove uniqueness by scanning data");
        bool duplicate=false;
        try { await Scalar(invalidPreflight.Sql); } catch(PostgresException e) when(e.SqlState=="23505") { duplicate=true; }
        check(duplicate && (bool)(await Scalar($"SELECT NOT indisvalid FROM pg_index WHERE indexrelid='{fixture.QS}.invalid_idx'::regclass"))!,"Actual concurrent uniqueness failure leaves invalid index in isolated fixture");
        check((await inspector.InspectAsync(invalidProposal)).Blockers.Any(x=>x.Contains("occupies")),"Invalid residue blocks retry by name");
        await Scalar($"DROP INDEX {fixture.QS}.invalid_idx");
        var classes=await catalog.InspectOperatorClassesAsync();var op=classes.Single(x=>x.Name=="text_pattern_ops");
        var forged=new ExpressionIndexEdit(target,"bad_op",[new(new("label")){OperatorClass=op with {Oid=op.Oid+1000000}}]);
        check((await inspector.InspectAsync(forged)).Blockers.Any(x=>x.Contains("Operator class")),"Concurrent preflight refuses forged opclass identity");
        var readOnlyConnection=new NpgsqlConnectionStringBuilder(fixture.CreatorConnection){Options="-c default_transaction_read_only=on"}.ConnectionString;
        check((await new ConcurrentIndexInspector(readOnlyConnection).InspectAsync(proposal)).Blockers.Any(x=>x.Contains("read-only")),"Read-only connection inspection reports write precondition");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();bool observed=false;try { await inspector.InspectAsync(proposal,canceled.Token); } catch(OperationCanceledException) { observed=true; }
            check(observed,"Canceled preflight does not open a connection or submit DDL");
        }
        await using(var blocker = new NpgsqlConnection(fixture.CreatorConnection))
        await using(var worker = new NpgsqlConnection(fixture.CreatorConnection))
        {
            await blocker.OpenAsync(); await worker.OpenAsync();
            await using var tx=await blocker.BeginTransactionAsync();
            await using(var hold=new NpgsqlCommand($"LOCK TABLE {fixture.QS}.target IN ROW EXCLUSIVE MODE",blocker,tx))
                await hold.ExecuteNonQueryAsync();
            await using var create=new NpgsqlCommand(ready.Sql,worker){CommandTimeout=20};
            var creation=create.ExecuteNonQueryAsync();
            bool seen=false;
            for(int attempt=0;attempt<80 && !seen;attempt++)
            {
                var observation=await inspector.InspectAsync(proposal);
                seen=observation.Blockers.Any(x=>x.Contains("already visible"));
                if(!seen)await Task.Delay(25);
            }
            // Always release before awaiting the worker, including a failed observation.
            await tx.RollbackAsync();await creation;
            check(seen,"Preflight detects actual index build waiting behind another session lock");
            await Scalar($"DROP INDEX {fixture.QS}.concurrent_idx");
        }
        await fixture.Owner($"ALTER TABLE {fixture.QS}.target ADD COLUMN extra integer");
        check((await inspector.InspectAsync(proposal)).Blockers.Any(x=>x.Contains("identity")),"Concurrent preflight refuses stale column signature");
        await fixture.Owner($"DROP TABLE {fixture.QS}.target; CREATE TABLE {fixture.QS}.target(id integer,label text)");
        check((await inspector.InspectAsync(proposal)).Blockers.Any(x=>x.Contains("identity")),"Concurrent preflight refuses same-name replacement table");
    }
}
