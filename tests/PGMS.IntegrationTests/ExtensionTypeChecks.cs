using Npgsql;
using PGMS.PostgreSql;

// M1116 (PGMS-047): columns of types PGMS and Npgsql do not know, as PostGIS geometry. The isolated cluster has no PostGIS, so a base type named
// geometry is built from the internal text I/O functions (needs the cluster's superuser); its text output is what PostGIS gives for a geometry
// (hex EWKB), so the checks see the same shape of value without installing anything.
internal static class ExtensionTypeChecks
{
    internal const string PointHex = "0101000020E6100000000000000000F03F0000000000000040"; // POINT(1 2), SRID 4326

    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "gis_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        await Sql($"""
            CREATE SCHEMA {schema};
            CREATE TYPE {schema}.geometry;
            CREATE FUNCTION {schema}.geometry_in(cstring) RETURNS {schema}.geometry LANGUAGE internal IMMUTABLE STRICT AS 'textin';
            CREATE FUNCTION {schema}.geometry_out({schema}.geometry) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS 'textout';
            CREATE TYPE {schema}.geometry (INPUT = {schema}.geometry_in, OUTPUT = {schema}.geometry_out, INTERNALLENGTH = VARIABLE, STORAGE = extended);
            CREATE TABLE {schema}.places (id int PRIMARY KEY, name text, geom {schema}.geometry);
            INSERT INTO {schema}.places VALUES (1, 'origin', '{PointHex}'), (2, 'nowhere', NULL);
            """);
        try
        {
            await using var session = new QuerySession(connection);
            await session.OpenAsync();
            var data = await session.ExecuteAsync($"SELECT id, name, geom FROM {schema}.places ORDER BY id");
            var table = data.Tables[0];
            check(table.Rows.Count == 2 && Equals(table.Rows[0][2], PointHex) && table.Rows[1][2] is DBNull,
                $"A geometry column is read as the server's text (hex EWKB) without error, NULL stays NULL: {table.Rows[0][2]}");
            var text = await session.ExecuteAsync($"SELECT geom::text AS wkb, pg_typeof(geom)::text AS type FROM {schema}.places WHERE id = 1");
            check(Equals(text.Tables[0].Rows[0][0], PointHex) && Equals(text.Tables[0].Rows[0][1], schema + ".geometry"), "Casting to text gives the same value and the type keeps its name");

            // The path of the results grid: every column is asked as text (AllResultTypesAreUnknown), so any extension type is shown as the server writes it.
            var rows = new List<object[]>(); IReadOnlyList<string>? types = null;
            await session.StreamAsync($"SELECT id, geom FROM {schema}.places ORDER BY id", (batch, _) => { rows.AddRange(batch.Rows); types ??= batch.ColumnTypes; return ValueTask.CompletedTask; });
            // M1124: the first query (above) met a type Npgsql did not know, so the session read the types again; this one knows the name, and a
            // type named geometry is shown as EWKT.
            check(rows.Count == 2 && Equals(rows[0][1], "SRID=4326;POINT(1 2)") && rows[1][1] is DBNull or null && types is { Count: 2 } && types[1] == schema + ".geometry",
                $"After a result with an unknown type the session reads the types again: the next stream names the geometry type and shows EWKT: {(types is null ? "" : string.Join(",", types))} {(rows.Count > 0 ? rows[0][1] : "")}");
            // A connection that reads the catalog after the type exists (PostGIS installed before connecting) knows its name.
            await using (var fresh = new QuerySession(connection + ";Application Name=pgms-gis-" + Guid.NewGuid().ToString("N")[..8]))
            {
                await fresh.OpenAsync();
                types = null;
                await fresh.StreamAsync($"SELECT geom FROM {schema}.places WHERE id = 1", (batch, _) => { types ??= batch.ColumnTypes; return ValueTask.CompletedTask; });
                check(types is { Count: 1 } && types[0].Contains("geometry", StringComparison.Ordinal), "With the type known when connecting, the column type is geometry: " + (types is null ? "" : types[0]));
            }

            var catalog = await new SchemaContextService(connection).LoadAsync();
            var context = catalog.Build("places geometry", 200_000, null);
            check(context.Text.Contains("geom", StringComparison.Ordinal) && context.Text.Contains("geometry", StringComparison.Ordinal),
                "The structure sent to the AI assistant names the geometry column and its type");
        }
        finally { await Sql($"DROP SCHEMA {schema} CASCADE"); }
    }
}
