using Npgsql;
using PGMS.PostgreSql;

internal static class TablePropertyChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "pgms_props_" + Guid.NewGuid().ToString("N");
        string otherSchema = schema + "_other";
        string qo = SqlIdentifier.Quote(otherSchema);
        string q = SqlIdentifier.Quote(schema);
        string role = schema + "_reader";
        const string table = "quoted\"table";
        string target = q + "." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var service = new TablePropertiesService(connection);
        try
        {
            await session.ExecuteAsync($"CREATE SCHEMA {q}; CREATE TABLE {target}(id int); INSERT INTO {target} VALUES (1),(2); ANALYZE {target}; COMMENT ON TABLE {target} IS 'Descrição'; ALTER TABLE {target} ENABLE ROW LEVEL SECURITY; CREATE ROLE {SqlIdentifier.Quote(role)} LOGIN;");
            var info = await service.InspectAsync(schema, table);
            check(info.Name == table && info.Owner == "pgms_test" && info.Kind == "Ordinary table" && info.Persistence == "Permanent", "table properties resolve quoted name and catalog owner/kind");
            check(info.EstimatedRows == 2 && info.RowSecurity && !info.ForceRowSecurity && info.Comment == "Descrição", "table properties report estimate, RLS and Unicode comment");
            bool missing = false;
            try { await service.InspectAsync(schema, "absent"); } catch (InvalidOperationException) { missing = true; }
            check(missing, "missing table inspection reports explicit failure");
            var limited = new NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString;
            check((await new TablePropertiesService(limited).InspectAsync(schema, table)).Oid == info.Oid, "ordinary role reads catalog metadata without table SELECT");
            await session.ExecuteAsync($"ALTER TABLE {target} ADD COLUMN note text DEFAULT 'default value', ADD COLUMN amount numeric(12,2), ADD COLUMN identity_id bigint GENERATED ALWAYS AS IDENTITY, ADD COLUMN computed int GENERATED ALWAYS AS (id+1) STORED, ADD COLUMN removed int; ALTER TABLE {target} DROP COLUMN removed; COMMENT ON COLUMN {target}.note IS 'Column description';");
            var columns = (await service.InspectAsync(schema, table)).Columns;
            check(columns.Count == 5 && columns[0].Name == "id" && columns.All(c => c.Name != "removed"), "column inspection preserves order and excludes dropped/system columns");
            check(columns.Single(c => c.Name == "amount").Type == "numeric(12,2)" && columns.Single(c => c.Name == "note").DefaultExpression == "'default value'::text" && columns.Single(c => c.Name == "note").Comment == "Column description", "column inspection reports type precision, default expression and comment");
            check(columns.Single(c => c.Name == "identity_id").Identity == "Always" && !columns.Single(c => c.Name == "identity_id").Nullable && columns.Single(c => c.Name == "computed").Generated == "Stored" && columns.Single(c => c.Name == "computed").DefaultExpression!.Contains("id + 1", StringComparison.Ordinal), "column inspection distinguishes identity and stored generation");
            await session.ExecuteAsync($"CREATE VIEW {q}.direct_view AS SELECT note FROM {target}; CREATE VIEW {q}.indirect_view AS SELECT * FROM {q}.direct_view;");
            var dependencies = (await service.InspectAsync(schema, table)).Dependents;
            check(dependencies.Any(d => d.Description.Contains("direct_view", StringComparison.Ordinal) && d.Reference == "note" && d.Kind == "Normal"), "direct dependents identify view rule referencing exact column");
            check(!dependencies.Any(d => d.Description.Contains("indirect_view", StringComparison.Ordinal)), "direct dependents do not claim transitive coverage");
            await session.ExecuteAsync($"DROP VIEW {q}.indirect_view; DROP VIEW {q}.direct_view;");
            check(!(await service.InspectAsync(schema, table)).Dependents.Any(d => d.Description.Contains("direct_view", StringComparison.Ordinal)), "reinspection refreshes removed dependency");
            await session.ExecuteAsync($"CREATE UNIQUE INDEX id_unique ON {target}(id); CREATE INDEX expression_idx ON {target}(lower(note)) INCLUDE(id) WHERE note IS NOT NULL;");
            var indexes = (await service.InspectAsync(schema, table)).Indexes;
            check(indexes.Count == 2 && indexes.Single(i => i.Name == "id_unique") is { Unique: true, Primary: false, Valid: true, Ready: true, Method: "btree" }, "index inspection reports actual method and catalog flags");
            check(indexes.Single(i => i.Name == "expression_idx").Definition.Contains("lower(note)", StringComparison.Ordinal) && indexes.Single(i => i.Name == "expression_idx").Definition.Contains("INCLUDE (id)", StringComparison.Ordinal) && indexes.Single(i => i.Name == "expression_idx").Definition.Contains("WHERE", StringComparison.Ordinal), "index definition retains expression, include and predicate");
            check((await new TablePropertiesService(limited).InspectAsync(schema, table)).Indexes.Count == 2, "index metadata remains readable without table SELECT");
            await session.ExecuteAsync($"DROP INDEX {q}.expression_idx;");
            check((await service.InspectAsync(schema, table)).Indexes.Count == 1, "index reinspection removes dropped index");
            await session.ExecuteAsync($"ALTER TABLE {target} ADD CONSTRAINT primary_id PRIMARY KEY(id), ADD CONSTRAINT unique_amount UNIQUE(amount) DEFERRABLE INITIALLY DEFERRED, ADD CONSTRAINT positive_id CHECK(id>0) NOT VALID, ADD CONSTRAINT self_reference FOREIGN KEY(id) REFERENCES {target}(id) ON DELETE CASCADE;");
            var constraints = (await service.InspectAsync(schema, table)).Constraints;
            check(constraints.Count == 4 && constraints.Select(c => c.Kind).Order().SequenceEqual(new[] { "Check", "Foreign key", "Primary key", "Unique" }), "constraint inspection identifies table-owned kinds");
            check(constraints.Single(c => c.Name == "positive_id") is { Validated: false } && constraints.Single(c => c.Name == "unique_amount") is { Deferrable: true, InitiallyDeferred: true }, "constraint inspection preserves NOT VALID and deferred flags");
            check(constraints.Single(c => c.Name == "self_reference").Definition.Contains("ON DELETE CASCADE", StringComparison.Ordinal), "constraint definition preserves referential action");
            await session.ExecuteAsync($"CREATE TABLE {q}.exclusion_fixture(period int4range, EXCLUDE USING gist(period WITH &&));");
            check((await service.InspectAsync(schema, "exclusion_fixture")).Constraints.Single().Kind == "Exclusion", "constraint inspection can read exclusion without claiming creation support");
            await session.ExecuteAsync($"CREATE SCHEMA {qo}; CREATE TABLE {qo}.child(id int); ALTER TABLE {qo}.child ADD CONSTRAINT child_reference FOREIGN KEY(id) REFERENCES {target}(id) ON DELETE CASCADE NOT VALID;");
            var incoming = (await service.InspectAsync(schema, table)).IncomingForeignKeys;
            check(incoming.Count == 2 && incoming.Any(k => k.Table == table && k.Name == "self_reference"), "incoming FK inspection includes direct and self references");
            check(incoming.Single(k => k.Schema == otherSchema) is { Name: "child_reference", Validated: false } && incoming.Single(k => k.Schema == otherSchema).Definition.Contains("ON DELETE CASCADE", StringComparison.Ordinal), "incoming FK reports cross-schema source, validation and complete action");
            check((await new TablePropertiesService(limited).InspectAsync(schema, table)).IncomingForeignKeys.Count == 2, "incoming FK metadata does not require SELECT on source table");
            await session.ExecuteAsync($"DROP TABLE {qo}.child;");
            check((await service.InspectAsync(schema, table)).IncomingForeignKeys.Count == 1, "incoming FK reinspection removes dropped source");
            await session.ExecuteAsync($"CREATE FUNCTION {q}.capture_change() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$; CREATE TRIGGER capture_change BEFORE INSERT OR UPDATE ON {target} FOR EACH ROW EXECUTE FUNCTION {q}.capture_change();");
            var triggers = (await service.InspectAsync(schema, table)).Triggers;
            check(triggers.Single(t => t.Name == "capture_change") is { Internal: false, Enabled: "Origin / local" } && triggers.Single(t => t.Name == "capture_change").Definition.Contains("BEFORE INSERT OR UPDATE", StringComparison.Ordinal), "trigger inspection returns user trigger definition and firing mode");
            check(triggers.Any(t => t.Internal), "trigger inspection distinguishes internal FK triggers");
            await session.ExecuteAsync($"ALTER TABLE {target} DISABLE TRIGGER capture_change;");
            check((await service.InspectAsync(schema, table)).Triggers.Single(t => t.Name == "capture_change").Enabled == "Disabled", "trigger reinspection reflects disabled state");
            await session.ExecuteAsync($"ALTER TABLE {target} ENABLE REPLICA TRIGGER capture_change;");
            check((await service.InspectAsync(schema, table)).Triggers.Single(t => t.Name == "capture_change").Enabled == "Replica", "trigger inspection distinguishes replica mode");
            await session.ExecuteAsync($"ALTER TABLE {target} ENABLE ALWAYS TRIGGER capture_change;");
            check((await service.InspectAsync(schema, table)).Triggers.Single(t => t.Name == "capture_change").Enabled == "Always", "trigger inspection distinguishes always mode");
            // Subsequent inspection milestones extend this fixture.
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {q} CASCADE; DROP SCHEMA IF EXISTS {qo} CASCADE; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)};"); }
    }
}
