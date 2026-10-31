using PGMS.PostgreSql;

internal static class ObjectPropertyChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var service = new ObjectPropertiesService(connection);
        var catalog = new CatalogService(connection);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        string database = "properties_" + Guid.NewGuid().ToString("N") + " ç";
        string quoted = SqlIdentifier.Quote(database);
        try
        {
            await session.ExecuteAsync($"CREATE DATABASE {quoted};");
            var entry = (await catalog.ChildrenAsync(new(CatalogKind.Databases, "Databases"))).Single(e => e.Name == database);
            var initial = await service.InspectAsync(entry);
            check(Value(initial, "Database") == database && Value(initial, "Encoding") == "UTF8", "Database properties preserve exact quoted name and encoding");
            check(Value(initial, "Owner") == "pgms_test" && Value(initial, "Comment") == "(none)", "Database properties distinguish owner and absent comment");
            await session.ExecuteAsync($"COMMENT ON DATABASE {quoted} IS 'ação ''quoted''';");
            await session.ExecuteAsync($"ALTER DATABASE {quoted} WITH ALLOW_CONNECTIONS false CONNECTION LIMIT 7;");
            var restricted = await service.InspectAsync(entry);
            check(Value(restricted, "Allows connections") == "False" && Value(restricted, "Connection limit (-1 = unlimited)") == "7", "Database inspection does not require connecting to its target database");
            check(Value(restricted, "Comment") == "ação 'quoted'", "Database refresh reads actual Unicode comment");
            check(await RejectedAsync(service, entry with { Name = "wrong" }), "Database inspection refuses mismatched name and OID");
            await session.ExecuteAsync($"DROP DATABASE {quoted};");
            await session.ExecuteAsync($"CREATE DATABASE {quoted};");
            check(await RejectedAsync(service, entry), "Database properties refuse same-name replacement identity");
        }
        finally { await session.ExecuteAsync($"DROP DATABASE IF EXISTS {quoted};"); }
        string schema = "schema_" + Guid.NewGuid().ToString("N") + " ü";
        string role = "property_reader_" + Guid.NewGuid().ToString("N");
        string qschema = SqlIdentifier.Quote(schema);
        try
        {
            await session.ExecuteAsync($"CREATE SCHEMA {qschema}; CREATE ROLE {role} LOGIN; COMMENT ON SCHEMA {qschema} IS 'schema comment';");
            var entry = (await catalog.ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"))).Single(e => e.Name == schema);
            var info = await service.InspectAsync(entry);
            check(Value(info, "Schema") == schema && Value(info, "Comment") == "schema comment", "Schema properties preserve exact name and comment");
            var limitedConnection = new Npgsql.NpgsqlConnectionStringBuilder(connection) { Username = role }.ConnectionString;
            var limited = new ObjectPropertiesService(limitedConnection);
            info = await limited.InspectAsync(entry);
            check(Value(info, "Current role USAGE") == "False" && Value(info, "Current role CREATE") == "False", "Limited role can inspect schema metadata without claiming privileges");
            await session.ExecuteAsync($"GRANT USAGE ON SCHEMA {qschema} TO {role};");
            info = await limited.InspectAsync(entry);
            check(Value(info, "Current role USAGE") == "True" && Value(info, "Current role CREATE") == "False", "Schema refresh reflects effective current-role privilege separately");
            check(await RejectedAsync(service, entry with { Database = "wrong_database" }), "Schema inspection refuses cross-database identity");
            await session.ExecuteAsync($"DROP SCHEMA {qschema}; CREATE SCHEMA {qschema};");
            check(await RejectedAsync(service, entry), "Schema inspection refuses same-name replacement");
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {qschema} CASCADE; DROP ROLE IF EXISTS {role};"); }
        string viewSchema = "views_" + Guid.NewGuid().ToString("N");
        try
        {
            await session.ExecuteAsync($"CREATE SCHEMA {viewSchema}; CREATE SEQUENCE {viewSchema}.counter; CREATE VIEW {viewSchema}.sample WITH (security_barrier=true) AS SELECT nextval('{viewSchema}.counter') AS number; CREATE MATERIALIZED VIEW {viewSchema}.cached AS SELECT 42 AS answer WITH NO DATA;");
            var view = (await catalog.ChildrenAsync(new(CatalogKind.Views, "Views", "postgres", viewSchema))).Single();
            var info = await service.InspectAsync(view);
            check(Value(info, "Kind") == "View" && Value(info, "Options").Contains("security_barrier=true") && info.Definition?.Contains("nextval") == true, "View properties return options and deparsed definition");
            var state = await session.ExecuteAsync($"SELECT is_called FROM {viewSchema}.counter;");
            check(state.Tables.Single().Rows[0][0].ToString() == "f", "View inspection never executes its volatile SELECT");
            var materialized = (await catalog.ChildrenAsync(new(CatalogKind.MaterializedViews, "Materialized Views", "postgres", viewSchema))).Single();
            info = await service.InspectAsync(materialized);
            check(Value(info, "Kind") == "Materialized view" && Value(info, "Materialized data populated") == "false", "Unpopulated materialized view can be inspected without reading or refreshing rows");
            await session.ExecuteAsync($"REFRESH MATERIALIZED VIEW {viewSchema}.cached;");
            check(Value(await service.InspectAsync(materialized), "Materialized data populated") == "true", "Reinspection observes materialized population state");
            check(await RejectedAsync(service, view with { RelationType = "m" }), "View inspection verifies relation kind as well as OID");
            await session.ExecuteAsync($"DROP VIEW {viewSchema}.sample; CREATE VIEW {viewSchema}.sample AS SELECT 1;");
            check(await RejectedAsync(service, view), "View properties refuse same-name replacement");
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {viewSchema} CASCADE;"); }
        string routineSchema = "routines_" + Guid.NewGuid().ToString("N");
        try
        {
            await session.ExecuteAsync($"CREATE SCHEMA {routineSchema}; CREATE SEQUENCE {routineSchema}.counter; CREATE FUNCTION {routineSchema}.sample(integer) RETURNS integer LANGUAGE sql AS $$ SELECT nextval('{routineSchema}.counter')::integer + $1 $$; CREATE FUNCTION {routineSchema}.sample(text) RETURNS text LANGUAGE sql IMMUTABLE STRICT AS $$ SELECT $1 $$; CREATE PROCEDURE {routineSchema}.action() LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('{routineSchema}.counter'); END $$;");
            var routines = await catalog.ChildrenAsync(new(CatalogKind.Functions, "Functions", "postgres", routineSchema));
            var integer = routines.Single(e => e.Name == "sample(integer)");
            var text = routines.Single(e => e.Name == "sample(text)");
            var info = await service.InspectAsync(integer);
            check(Value(info, "Returns") == "integer" && info.Definition?.Contains("nextval") == true, "Function properties identify exact integer overload and definition");
            info = await service.InspectAsync(text);
            check(Value(info, "Returns") == "text" && Value(info, "Volatility") == "Immutable" && Value(info, "Strict (null input)") == "True", "Function properties distinguish overloaded return type and attributes");
            check(await RejectedAsync(service, integer with { Name = text.Name }), "Routine properties reject OID and signature mismatch");
            var procedure = (await catalog.ChildrenAsync(new(CatalogKind.Procedures, "Procedures", "postgres", routineSchema))).Single();
            info = await service.InspectAsync(procedure);
            check(Value(info, "Kind") == "Procedure" && info.Definition?.Contains("CREATE OR REPLACE PROCEDURE") == true, "Procedure properties show catalog kind and reconstructed definition");
            var state = await session.ExecuteAsync($"SELECT is_called FROM {routineSchema}.counter;");
            check(state.Tables.Single().Rows[0][0].ToString() == "f", "Inspecting functions and procedures never executes their bodies");
            await session.ExecuteAsync($"ALTER FUNCTION {routineSchema}.sample(integer) STABLE;");
            check(Value(await service.InspectAsync(integer), "Volatility") == "Stable", "Routine refresh observes changed attributes on same identity");
            await session.ExecuteAsync($"DROP FUNCTION {routineSchema}.sample(integer); CREATE FUNCTION {routineSchema}.sample(integer) RETURNS integer LANGUAGE sql AS $$ SELECT $1 $$;");
            check(await RejectedAsync(service, integer), "Routine properties refuse same-signature replacement OID");
            string large = string.Concat(Enumerable.Repeat("😀", 140000));
            await session.ExecuteAsync($"CREATE FUNCTION {routineSchema}.large() RETURNS text LANGUAGE sql AS $$ SELECT '{large}'::text $$;");
            var largeEntry = (await catalog.ChildrenAsync(new(CatalogKind.Functions, "Functions", "postgres", routineSchema))).Single(e => e.Name == "large()");
            info = await service.InspectAsync(largeEntry);
            check(info.DefinitionTruncated && info.Definition?.Length <= 262144 && !char.IsHighSurrogate(info.Definition![^1]), "Large definition is explicitly limited without splitting Unicode surrogate pair");
            check(Value(info, "Returns") == "text" && info.Target.Oid == largeEntry.Oid, "Definition display limit preserves surrounding catalog metadata");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            bool canceled = false;
            try { await service.InspectAsync(largeEntry, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
            check(canceled, "Property service honors pre-canceled inspection token");
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {routineSchema} CASCADE;"); }
    }
    private static string Value(ObjectProperties info, string name) => info.Values.Single(p => p.Name == name).Value;
    private static async Task<bool> RejectedAsync(ObjectPropertiesService service, CatalogEntry entry)
    {
        try { await service.InspectAsync(entry); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
