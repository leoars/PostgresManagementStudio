using Npgsql;
using PGMS.PostgreSql;

internal static class PartitionTableChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        void Refuses(Action action, string label)
        {
            bool refused = false;
            try { action(); } catch (ArgumentException) { refused = true; }
            check(refused, label);
        }
        TableColumn[] columns = [new("Key", ColumnType.Integer), new("key", ColumnType.Integer), new("é\";--", ColumnType.Text)];
        Refuses(() => new TablePartitioning((PartitionStrategy)99, ["Key"]), "Unknown partition strategy refused");
        Refuses(() => new TablePartitioning(PartitionStrategy.Range, []), "Empty partition key refused");
        Refuses(() => new TablePartitioning(PartitionStrategy.List, ["Key", "key"]), "LIST accepts only one key");
        Refuses(() => new TablePartitioning(PartitionStrategy.Hash, ["Key", "Key"]), "Duplicate key refused");
        foreach (string invalid in new[] { "", "a\0b", new string('é', 32) })
            Refuses(() => new TablePartitioning(PartitionStrategy.Range, [invalid]), "Invalid identifier refused before connection");
        Refuses(() => new TablePartitioning(PartitionStrategy.Range, ["KEY"]).ToSql(columns), "Key binding is case sensitive");
        Refuses(() => new TablePartitioning(PartitionStrategy.Range, ["lower(Key)"]).ToSql(columns), "SQL expression is not accepted as a declared key");
        var input = new List<string> { "Key", "key" };
        var captured = new TablePartitioning(PartitionStrategy.Range, input);
        input.Clear();
        check(captured.Columns.SequenceEqual(new[] { "Key", "key" }), "Partition keys survive mutation of caller collection");
        bool immutable = false;
        try { ((IList<string>)captured.Columns).Clear(); } catch (NotSupportedException) { immutable = true; }
        check(immutable, "Partition keys cannot be modified through IList");
        var many = Enumerable.Range(0, 33).Select(i => new TableColumn("c" + i, ColumnType.Integer)).ToArray();
        foreach (var strategy in new[] { PartitionStrategy.Range, PartitionStrategy.Hash })
            Refuses(() => new TablePartitioning(strategy, many.Select(c => c.Name)), "33 keys refused: " + strategy);
        var legacy = new CreateTableEdit("public", "plain", [new("id", ColumnType.Integer)]);
        check(legacy.ToSql() == "CREATE TABLE \"public\".\"plain\" (\n    \"id\" integer NULL\n);", "Ordinary CREATE TABLE keeps exact legacy SQL");

        await using var f = await FutureDefaultFixture.CreateAsync(connection);
        await using var owner = new NpgsqlConnection(f.CreatorConnection);
        await owner.OpenAsync();
        async Task<object?> Scalar(string sql) { await using var c = new NpgsqlCommand(sql, owner); return await c.ExecuteScalarAsync(); }
        var service = new AdministrativeEditService(f.CreatorConnection);
        check((bool)(await Scalar("SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname=current_user"))!, "Partition creation uses ordinary owner without cluster privileges");
        foreach (var strategy in Enum.GetValues<PartitionStrategy>())
        {
            string name = strategy.ToString(), target = ObjectSql.Relation(f.Schema, name);
            string key = strategy == PartitionStrategy.List ? "é\";--" : "Key";
            var edit = new CreateTableEdit(f.Schema, name, columns) { Partitioning = new(strategy, [key]) };
            await service.ApplyAsync(edit);
            await using var catalog = new NpgsqlCommand("SELECT c.relkind::text || ':' || p.partstrat::text || ':' || p.partattrs::text FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_partitioned_table p ON p.partrelid=c.oid WHERE n.nspname=@schema AND c.relname=@name", owner);
            catalog.Parameters.AddWithValue("schema", f.Schema); catalog.Parameters.AddWithValue("name", name);
            string expected = strategy switch { PartitionStrategy.Range => "p:r:1", PartitionStrategy.List => "p:l:3", _ => "p:h:1" };
            check((string)(await catalog.ExecuteScalarAsync())! == expected, "Catalog strategy and exact attnum: " + strategy);
            string bound = strategy switch { PartitionStrategy.Range => "FOR VALUES FROM (0) TO (10)", PartitionStrategy.List => "FOR VALUES IN ('ação')", _ => "FOR VALUES WITH (MODULUS 1, REMAINDER 0)" };
            await f.Owner($"CREATE TABLE {f.QS}.child_{name.ToLowerInvariant()} PARTITION OF {target} {bound}");
            check(Convert.ToInt32(await Scalar($"INSERT INTO {target} VALUES(2,3,'ação') RETURNING \"Key\"")) == 2, "Rows route into native partition: " + strategy);
            bool conflict = false;
            try { await service.ApplyAsync(edit); } catch (PostgresException e) when (e.SqlState == "42P07") { conflict = true; }
            check(conflict && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {target}")) == 1, "Duplicate creation preserves original data: " + strategy);
        }
        foreach (var strategy in new[] { PartitionStrategy.Range, PartitionStrategy.Hash })
        {
            var edit = new CreateTableEdit(f.Schema, "max_" + strategy, many.Take(32).ToArray()) { Partitioning = new(strategy, many.Take(32).Select(c => c.Name)) };
            await service.ApplyAsync(edit);
            check(Convert.ToInt64(await Scalar($"SELECT partnatts FROM pg_partitioned_table WHERE partrelid='{ObjectSql.Relation(f.Schema, edit.Name)}'::regclass")) == 32, "PG17 accepts 32-key boundary: " + strategy);
        }
        var deniedEdit = new CreateTableEdit(f.Schema, "denied", columns) { Partitioning = captured };
        bool denied = false;
        try { await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(deniedEdit); } catch (PostgresException e) when (e.SqlState == "42501") { denied = true; }
        check(denied && await Scalar($"SELECT to_regclass('{f.QS}.denied')::text") is DBNull, "Missing schema authority leaves no table");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { await service.ApplyAsync(deniedEdit, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && await Scalar($"SELECT to_regclass('{f.QS}.denied')::text") is DBNull, "Pre-cancelled creation leaves no table");
        var missing = deniedEdit with { Partitioning = new(PartitionStrategy.Range, ["missing"]) };
        bool validated = false;
        try { await new AdministrativeEditService("invalid").ApplyAsync(missing); } catch (ArgumentException e) { validated = e.Message.Contains("exactly match"); }
        check(validated, "Invalid key refused before parsing connection");
    }
}
