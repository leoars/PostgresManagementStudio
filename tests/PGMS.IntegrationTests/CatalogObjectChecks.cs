using Npgsql;
using PGMS.PostgreSql;

/// <summary>PGMS-063 (M1099): Composite Types, Foreign Servers and Replication in Object Explorer, with their Properties.
/// Runs in its own throwaway database; the foreign-data wrapper has no handler and the subscription never connects.</summary>
internal static class CatalogObjectChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string database = "pgms_objects_" + Guid.NewGuid().ToString("N")[..8];
        await using (var server = new NpgsqlConnection(connection))
        {
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", server); await create.ExecuteNonQueryAsync();
        }
        string target = new NpgsqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;
        try { await RunInDatabaseAsync(target, database, check); }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var server = new NpgsqlConnection(connection);
            await server.OpenAsync();
            // A subscription must lose its slot reference before the database can go.
            await using (var target2 = new NpgsqlConnection(target))
            {
                try
                {
                    await target2.OpenAsync();
                    await using var detach = new NpgsqlCommand("DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_subscription WHERE subname='pgms_sub') THEN ALTER SUBSCRIPTION pgms_sub DISABLE; ALTER SUBSCRIPTION pgms_sub SET (slot_name = NONE); DROP SUBSCRIPTION pgms_sub; END IF; END $$", target2);
                    await detach.ExecuteNonQueryAsync();
                }
                catch (PostgresException) { }
            }
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", server); await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task RunInDatabaseAsync(string connection, string database, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        var catalog = new CatalogService(connection);
        var properties = new ObjectPropertiesService(connection);
        var databaseEntry = new CatalogEntry(CatalogKind.Database, database, database);
        var folders = await catalog.ChildrenAsync(databaseEntry);
        var servers = folders.Single(f => f.Kind == CatalogKind.ForeignServers);
        var replication = folders.Single(f => f.Kind == CatalogKind.Replication);
        check(servers.Name == "Foreign Servers" && replication.Name == "Replication" && folders.Count == 5 && folders[^2] == servers && folders[^1] == replication, "Foreign Servers and Replication come after Extensions under a database");
        var replicationFolders = await catalog.ChildrenAsync(replication);
        check(replicationFolders.Select(f => (f.Kind, f.Name)).SequenceEqual([(CatalogKind.Publications, "Local Publications"), (CatalogKind.Subscriptions, "Local Subscriptions")]), "Replication holds Local Publications and Local Subscriptions, as in SSMS");
        check((await catalog.ChildrenAsync(servers)).Count == 0 && (await catalog.ChildrenAsync(replicationFolders[0])).Count == 0 && (await catalog.ChildrenAsync(replicationFolders[1])).Count == 0, "The folders are empty in a new database");

        await Sql("""
            CREATE TYPE public.pgms_point AS (x integer, y numeric(10,2), label text COLLATE "C");
            COMMENT ON TYPE public.pgms_point IS 'ponto de teste';
            CREATE TABLE public.pgms_plain (id integer PRIMARY KEY, note text);
            CREATE FOREIGN DATA WRAPPER pgms_dummy_fdw;
            CREATE SERVER pgms_remote FOREIGN DATA WRAPPER pgms_dummy_fdw OPTIONS (host 'remote.invalid', password_hint 'segredo');
            CREATE USER MAPPING FOR CURRENT_USER SERVER pgms_remote OPTIONS (password 'segredo');
            CREATE FOREIGN TABLE public.pgms_far (a integer) SERVER pgms_remote OPTIONS (table_name 'far');
            CREATE PUBLICATION pgms_pub FOR TABLE public.pgms_plain WITH (publish = 'insert, update');
            CREATE SUBSCRIPTION pgms_sub CONNECTION 'host=remote.invalid dbname=x password=segredo' PUBLICATION pgms_pub WITH (connect = false, enabled = false, create_slot = false, slot_name = NONE);
            """);

        var schema = new CatalogEntry(CatalogKind.Schema, "public", database, "public");
        var schemaFolders = await catalog.ChildrenAsync(schema);
        check(schemaFolders[^1] is { Kind: CatalogKind.CompositeTypes, Name: "Composite Types" } && schemaFolders[^2].Kind == CatalogKind.Domains, "Composite Types comes after Domains under a schema");
        var types = await catalog.ChildrenAsync(schemaFolders[^1]);
        check(types.Count == 1 && types[0] is { Kind: CatalogKind.DataType, Name: "pgms_point", DataTypeKind: "c", Schema: "public" } && types[0].CanInspectObjectProperties && !types[0].CanExpand,
            "Only the standalone composite type is listed, not the row types of tables: " + string.Join(", ", types.Select(t => t.Name)));
        var type = await properties.InspectAsync(types[0]);
        string Value(ObjectProperties p, string name) => p.Values.Single(v => v.Name == name).Value;
        check(Value(type, "Owner").Length > 0 && Value(type, "Attributes") == "3" && Value(type, "Comment") == "ponto de teste" && type.Definition is { } definition
              && definition.StartsWith("CREATE TYPE public.pgms_point AS (", StringComparison.Ordinal) && definition.Contains("    y numeric(10,2)", StringComparison.Ordinal) && definition.Contains("label text COLLATE \"C\"", StringComparison.Ordinal),
            "Composite type properties show the attributes and a CREATE TYPE: " + type.Definition);

        var serverList = await catalog.ChildrenAsync(servers);
        check(serverList.Count == 1 && serverList[0] is { Kind: CatalogKind.ForeignServer, Name: "pgms_remote" } && !serverList[0].CanExpand && serverList[0].CanInspectObjectProperties, "Foreign Servers lists the server");
        var serverProperties = await properties.InspectAsync(serverList[0]);
        string all = string.Join("|", serverProperties.Values.Select(v => v.Name + "=" + v.Value));
        check(Value(serverProperties, "Foreign-data wrapper") == "pgms_dummy_fdw" && Value(serverProperties, "Option names") == "host, password_hint" && Value(serverProperties, "Foreign tables") == "1"
              && Value(serverProperties, "User mappings").Length > 0 && Value(serverProperties, "Current role USAGE") == "True", "Foreign server properties show the wrapper, option names, user mappings and tables: " + all);
        check(!all.Contains("segredo", StringComparison.Ordinal) && !all.Contains("remote.invalid", StringComparison.Ordinal), "Option values (which may be passwords) are never shown");

        var tables = await catalog.ChildrenAsync(schemaFolders[0]);
        var far = tables.Single(t => t.Name == "pgms_far");
        check(far.RelationType == "f" && far.CanInspectObjectProperties && !far.CanInspectTable, "The foreign table stays in Tables and has Properties");
        var farProperties = await properties.InspectAsync(far);
        check(Value(farProperties, "Kind") == "Foreign table" && Value(farProperties, "Foreign server") == "pgms_remote" && Value(farProperties, "Options") == "table_name=far", "Foreign table properties show the server and options");

        var publications = await catalog.ChildrenAsync(replicationFolders[0]);
        check(publications.Count == 1 && publications[0] is { Kind: CatalogKind.Publication, Name: "pgms_pub" }, "Local Publications lists the publication");
        var publication = await properties.InspectAsync(publications[0]);
        check(Value(publication, "All tables") == "False" && Value(publication, "Publish INSERT") == "True" && Value(publication, "Publish DELETE") == "False" && Value(publication, "Publish TRUNCATE") == "false"
              && Value(publication, "Tables") == "1" && publication.Definition == "public.pgms_plain", "Publication properties show what it publishes and its tables");
        var subscriptions = await catalog.ChildrenAsync(replicationFolders[1]);
        check(subscriptions.Count == 1 && subscriptions[0] is { Kind: CatalogKind.Subscription, Name: "pgms_sub" }, "Local Subscriptions lists the subscription of this database");
        var subscription = await properties.InspectAsync(subscriptions[0]);
        string subAll = string.Join("|", subscription.Values.Select(v => v.Name + "=" + v.Value));
        check(Value(subscription, "Enabled") == "False" && Value(subscription, "Publications") == "pgms_pub" && Value(subscription, "Slot name") == "(none)" && !subAll.Contains("segredo", StringComparison.Ordinal) && !subAll.Contains("remote.invalid", StringComparison.Ordinal),
            "Subscription properties show its publications and never the connection string: " + subAll);
        check((await catalog.ChildrenAsync(replicationFolders[1] with { Database = "postgres" })).Count == 0, "Other databases do not list this database's subscription");

        await Sql("DROP TYPE public.pgms_point");
        string? stale = null;
        try { await properties.InspectAsync(types[0]); } catch (InvalidOperationException ex) { stale = ex.Message; }
        check(stale is not null && stale.Contains("Refresh", StringComparison.Ordinal), "Properties of a dropped object ask for a refresh");
    }
}
