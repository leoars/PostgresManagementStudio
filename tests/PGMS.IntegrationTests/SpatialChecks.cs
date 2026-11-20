using System.Globalization;
using Npgsql;
using PGMS.PostgreSql;

// M1124 (PGMS-047): PostGIS values as EWKT and the rest of PGMS with real PostGIS. The parser checks always run; the server checks need the
// postgis extension in the disposable cluster (Test-Integration.ps1 -SpatialOnly -PostgresBin with a PostgreSQL copy that has PostGIS) and are
// skipped with a note otherwise. Each run creates its own database and drops it, so the shared test cluster never keeps PostGIS objects.
internal static class SpatialChecks
{
    /// <summary>EWKT inputs covering every geometry type, dimensions, SRIDs and empties. Coordinates are exact in binary, so ST_AsEWKT and PGMS agree.</summary>
    private static readonly string[] Shapes =
    [
        "SRID=4326;POINT(1 2)", "POINT(1.5 -2.25)", "POINT(1 2 3)", "POINTM(1 2 3)", "POINT(1 2 3 4)", "POINT EMPTY", "SRID=3857;LINESTRING(0 0,10 10,20 25.5)",
        "POLYGON((0 0,10 0,10 10,0 10,0 0),(2 2,4 2,4 4,2 4,2 2))", "MULTIPOINT(0 0,1 1)", "MULTILINESTRING((0 0,1 1),(2 2,3 3))",
        "MULTIPOLYGON(((0 0,1 0,1 1,0 0)),((5 5,6 5,6 6,5 5)))", "GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))", "GEOMETRYCOLLECTION EMPTY",
        "CIRCULARSTRING(0 0,1 1,2 0)", "COMPOUNDCURVE(CIRCULARSTRING(0 0,1 1,2 0),(2 0,3 0))", "CURVEPOLYGON(CIRCULARSTRING(0 0,4 0,4 4,0 4,0 0),(1 1,3 3,3 1,1 1))",
        "MULTICURVE((0 0,5 5),CIRCULARSTRING(4 0,4 4,8 4))", "MULTISURFACE(CURVEPOLYGON(CIRCULARSTRING(0 0,4 0,4 4,0 4,0 0)),((10 10,14 12,11 10,10 10)))",
        "POLYHEDRALSURFACE(((0 0 0,0 0 1,0 1 1,0 1 0,0 0 0)),((0 0 0,0 1 0,1 1 0,1 0 0,0 0 0)))", "TRIANGLE((0 0,0 9,9 0,0 0))", "TIN(((0 0 0,0 0 1,0 1 0,0 0 0)),((0 0 0,0 1 0,1 1 0,0 0 0)))",
        "SRID=4326;MULTIPOLYGON EMPTY", "LINESTRINGM(0 0 1,1 1 2)",
    ];

    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        // Without a server: the parser.
        check(SpatialText.Display(ExtensionTypeChecks.PointHex) == "SRID=4326;POINT(1 2)", "Hexadecimal EWKB of a point becomes SRID=4326;POINT(1 2)");
        check(SpatialText.Display(ExtensionTypeChecks.PointHex[..^2]) == ExtensionTypeChecks.PointHex[..^2] && SpatialText.Display("not hex at all") == "not hex at all"
            && SpatialText.Display(ExtensionTypeChecks.PointHex + "00") == ExtensionTypeChecks.PointHex + "00" && SpatialText.Display("") == "",
            "Cut, extra or non-hexadecimal values stay exactly as the server wrote them");
        check(SpatialText.IsSpatialType("geometry") && SpatialText.IsSpatialType("public.geometry") && SpatialText.IsSpatialType("\"gis\".\"geography\"") && SpatialText.IsSpatialType("geometry(Point,4326)")
            && !SpatialText.IsSpatialType("text") && !SpatialText.IsSpatialType("raster") && !SpatialText.IsSpatialType("box2d") && !SpatialText.IsSpatialType(null),
            "Only geometry and geography columns are converted (raster, box2d and others keep the server text)");
        // A deeply nested collection is refused instead of recursing without end.
        string nested = string.Concat(Enumerable.Repeat("010700000001000000", 40)) + "0101000000000000000000F03F0000000000000040";
        check(SpatialText.Display(nested) == nested, "A collection nested beyond 32 levels stays as hexadecimal");

        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var available = new NpgsqlCommand("SELECT count(*) FROM pg_available_extensions WHERE name = 'postgis'", admin))
        {
            if ((long)(await available.ExecuteScalarAsync())! == 0)
            {
                Console.WriteLine("INFO: PostGIS is not available in this test cluster; the PostGIS server checks were skipped (Test-Integration.ps1 -SpatialOnly -PostgresBin <PostgreSQL with PostGIS>).");
                return;
            }
        }
        string database = "gis_" + Guid.NewGuid().ToString("N")[..10];
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync();
        string gis = new NpgsqlConnectionStringBuilder(connection) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            await using (var setup = new NpgsqlConnection(gis))
            {
                await setup.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    CREATE EXTENSION postgis;
                    CREATE TABLE public.places (id int PRIMARY KEY, name text, geom geometry(Point,4326), area geometry(Polygon,4326), geog geography(Point,4326), shape geometry);
                    INSERT INTO public.places VALUES
                      (1, 'São Paulo', 'SRID=4326;POINT(-46.633308 -23.55052)', 'SRID=4326;POLYGON((0 0,1 0,1 1,0 0))', 'SRID=4326;POINT(-46.633308 -23.55052)', 'CIRCULARSTRING(0 0,1 1,2 0)'),
                      (2, 'nowhere', NULL, NULL, NULL, NULL);
                    """, setup);
                await command.ExecuteNonQueryAsync();
            }
            await using var probe = new NpgsqlConnection(gis);
            await probe.OpenAsync();
            async Task<object?> Scalar(string sql, params object[] values)
            {
                await using var command = new NpgsqlCommand(sql, probe);
                foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
                return await command.ExecuteScalarAsync();
            }
            string version = (string)(await Scalar("SELECT postgis_lib_version()"))!;
            Console.WriteLine($"INFO: PostGIS {version} on {probe.PostgreSqlVersion}");

            // Every geometry type: PGMS gives the text of ST_AsEWKT, and the text read back is the same geometry, byte for byte.
            var differences = new List<string>();
            foreach (var shape in Shapes)
            {
                string hex = (string)(await Scalar("SELECT ST_GeomFromEWKT($1)::text", shape))!;
                string server = (string)(await Scalar("SELECT ST_AsEWKT(ST_GeomFromEWKT($1))", shape))!;
                string shown = SpatialText.Display(hex);
                bool same;
                try { same = (bool)(await Scalar("SELECT $1::geometry::text = $2", shown, hex))!; }
                catch (PostgresException ex) { same = false; shown += " [" + ex.MessageText + "]"; }
                if (shown != server || !same) differences.Add($"{shape}: PGMS {shown} | server {server} | round trip {same}");
            }
            check(differences.Count == 0, $"All {Shapes.Length} shapes are shown as ST_AsEWKT shows them and read back to the same geometry" + (differences.Count > 0 ? ":\n" + string.Join("\n", differences) : ""));

            // Coordinates with many digits: PostGIS writes 15 significant digits, PGMS writes the exact double, so typing it back changes nothing.
            var random = new Random(1124);
            var drifts = new List<string>();
            for (int i = 0; i < 50; i++)
            {
                double x = random.NextDouble() * 360 - 180, y = random.NextDouble() * 180 - 90, z = random.NextDouble() * 1e7;
                string point = string.Create(CultureInfo.InvariantCulture, $"SRID=4326;POINT({x:R} {y:R} {z:R})");
                string hex = (string)(await Scalar("SELECT ST_GeomFromEWKT($1)::text", point))!;
                string shown = SpatialText.Display(hex);
                if (!(bool)(await Scalar("SELECT $1::geometry::text = $2", shown, hex))!) drifts.Add(shown);
            }
            check(drifts.Count == 0, "50 points with random coordinates read back to the same bytes" + (drifts.Count > 0 ? ": " + string.Join(", ", drifts) : ""));
            check(SpatialText.Number(123456789012345.5) == "123456789012345.5" && SpatialText.Number(1e-9) == "1E-09" && SpatialText.Number(-0.0) == "0",
                "Large coordinates are written without an exponent; tiny ones keep it");

            // geography uses the same text.
            string geogHex = (string)(await Scalar("SELECT geog::text FROM public.places WHERE id = 1"))!;
            string geogShown = SpatialText.Display(geogHex);
            check(geogShown == "SRID=4326;POINT(-46.633308 -23.55052)" && (bool)(await Scalar("SELECT $1::geography::text = $2", geogShown, geogHex))!,
                "A geography value is shown as EWKT and reads back as the same geography: " + geogShown);

            // The results grid stream: geometry and geography as EWKT, geom::text (type text) as the server wrote it, NULL kept.
            await using (var session = new QuerySession(gis))
            {
                await session.OpenAsync();
                var rows = new List<object[]>(); IReadOnlyList<string>? types = null;
                // This connection string read the catalog before CREATE EXTENSION: the first result does not know the type and keeps the hexadecimal;
                // PGMS reads the types again afterwards, so the next query shows EWKT without reconnecting.
                await session.StreamAsync("SELECT geom FROM public.places WHERE id = 1", (batch, _) => { rows.AddRange(batch.Rows); types ??= batch.ColumnTypes; return ValueTask.CompletedTask; });
                check(types is { Count: 1 } && types[0].StartsWith("unknown type (oid ", StringComparison.Ordinal) && rows.Count == 1 && rows[0][0] is string first && first.StartsWith("0101000020E6100000", StringComparison.Ordinal),
                    "With PostGIS installed after the connection read the types, the first result keeps the hexadecimal: " + (types is null ? "" : types[0]));
                rows.Clear(); types = null;
                await session.StreamAsync("SELECT geom, geog, area, shape, geom::text AS raw, name FROM public.places ORDER BY id", (batch, _) => { rows.AddRange(batch.Rows); types ??= batch.ColumnTypes; return ValueTask.CompletedTask; });
                check(rows.Count == 2 && Equals(rows[0][0], "SRID=4326;POINT(-46.633308 -23.55052)") && Equals(rows[0][1], "SRID=4326;POINT(-46.633308 -23.55052)")
                    && Equals(rows[0][2], "SRID=4326;POLYGON((0 0,1 0,1 1,0 0))") && Equals(rows[0][3], "CIRCULARSTRING(0 0,1 1,2 0)") && rows[0][4] is string raw && raw.StartsWith("0101000020E6100000", StringComparison.Ordinal)
                    && Equals(rows[0][5], "São Paulo") && rows[1][0] is DBNull && rows[1][1] is DBNull,
                    "The grid shows geometry and geography columns as EWKT, a cast to text as hexadecimal, and keeps NULL: " + string.Join(" | ", rows[0]));
                check(types is { Count: 6 } && SpatialText.IsSpatialType(types[0]) && SpatialText.IsSpatialType(types[1]) && !SpatialText.IsSpatialType(types[4]), "The column types are geometry and geography: " + (types is null ? "" : string.Join(",", types)));

                rows.Clear();
                await session.StreamAsync("SELECT geom FROM public.places WHERE id = 1", (batch, _) => { rows.AddRange(batch.Rows); return ValueTask.CompletedTask; }, new QueryExecutionOptions() { SpatialAsText = false });
                check(rows.Count == 1 && rows[0][0] is string hexOnly && hexOnly.StartsWith("0101000020E6100000", StringComparison.Ordinal), "With the conversion off, the grid gets the server's hexadecimal");

                // Results to Text: a value that fits is converted; one cut at the column limit stays as the cut hexadecimal.
                rows.Clear();
                await session.StreamAsync("SELECT geom FROM public.places WHERE id = 1", (batch, _) => { rows.AddRange(batch.Rows); return ValueTask.CompletedTask; }, new QueryExecutionOptions() { TextCellLimit = 256 });
                check(rows.Count == 1 && Equals(rows[0][0], "SRID=4326;POINT(-46.633308 -23.55052)"), "Results to Text show the EWKT");
                rows.Clear();
                await session.StreamAsync("SELECT geom FROM public.places WHERE id = 1", (batch, _) => { rows.AddRange(batch.Rows); return ValueTask.CompletedTask; }, new QueryExecutionOptions() { TextCellLimit = 20 });
                check(rows.Count == 1 && rows[0][0] is string cut && cut.Length <= 20 && cut.StartsWith("0101000020", StringComparison.Ordinal), "A value cut by the text column limit stays as it was read");
            }

            // Object Explorer: columns with the PostGIS type and modifiers.
            var catalog = new CatalogService(gis);
            var schemaFolders = await catalog.ChildrenAsync(new(CatalogKind.Schema, "public", database, "public"));
            var tables = await catalog.ChildrenAsync(schemaFolders.Single(f => f.Kind == CatalogKind.Tables));
            var places = tables.Single(t => t.Name.Contains("places", StringComparison.Ordinal));
            check(!tables.Any(t => t.Name.Contains("spatial_ref_sys", StringComparison.Ordinal)) || tables.Count >= 2, "Object Explorer lists the tables of the database with PostGIS: " + string.Join(", ", tables.Select(t => t.Name)));
            var columnFolder = (await catalog.ChildrenAsync(places)).Single(f => f.Kind == CatalogKind.Columns);
            var columns = await catalog.ChildrenAsync(columnFolder);
            check(columns.Any(c => c.Name == "geom (geometry(Point,4326), null)") && columns.Any(c => c.Name == "geog (geography(Point,4326), null)") && columns.Any(c => c.Name == "shape (geometry, null)"),
                "Object Explorer shows the geometry and geography columns with type and SRID: " + string.Join("; ", columns.Select(c => c.Name)));

            // Script as CREATE: the column types survive and the script runs in another database with PostGIS.
            string script = await new ObjectScriptService(gis).ScriptAsync(places, ScriptVerb.Create);
            check(script.Contains("geometry(Point,4326)", StringComparison.Ordinal) && script.Contains("geography(Point,4326)", StringComparison.Ordinal), "Script as CREATE keeps the PostGIS column types");
            await using (var other = new NpgsqlCommand("CREATE SCHEMA copy", probe)) await other.ExecuteNonQueryAsync();
            await using (var run = new NpgsqlCommand(script.Replace("\"public\".\"places\"", "copy.places", StringComparison.Ordinal).Replace("public.places", "copy.places", StringComparison.Ordinal), probe))
            {
                bool ran = true; string error = "";
                try { await run.ExecuteNonQueryAsync(); } catch (PostgresException ex) { ran = false; error = ex.MessageText; }
                check(ran && (string?)(await Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'copy.places'::regclass AND attname = 'geom'")) == "geometry(Point,4326)",
                    "The CREATE script runs and makes the same geometry column " + error + (ran ? "" : Environment.NewLine + script));
            }

            // Table Designer: reads the types, and adds a geometry column with a type typed in.
            var designs = new TableDesignService(gis);
            var design = await designs.InspectAsync("public", "places");
            check(design.Columns.Any(c => c.Name == "geom" && c.Type == "geometry(Point,4326)") && design.Columns.Any(c => c.Name == "geog" && c.Type == "geography(Point,4326)"),
                "Table Designer reads the PostGIS column types: " + string.Join(", ", design.Columns.Select(c => c.Name + " " + c.Type)));
            var added = design with { Columns = [.. design.Columns, new DesignColumn(null, "route", "geometry(LineString,4326)", true)] };
            await new AdministrativeEditService(gis).ApplyAsync(new TableDesignEdit(design, added));
            check((string?)(await Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.places'::regclass AND attname = 'route'")) == "geometry(LineString,4326)",
                "Table Designer adds a geometry(LineString,4326) column");
            var extensionTypes = await new DataTypeCatalog(gis).ExtensionBaseTypesAsync();
            check(extensionTypes.Contains(("public", "geometry")) && extensionTypes.Contains(("public", "geography")) && !extensionTypes.Any(t => t.Name.StartsWith('_')) && !extensionTypes.Any(t => t.Name is "text" or "int4"),
                "The Table Designer's type list gets the extension base types, geometry and geography among them: " + string.Join(", ", extensionTypes.Select(t => t.Name)));

            // IntelliSense: PostGIS functions and the typed columns.
            var completion = await SqlCompletionCatalog.LoadAsync(gis);
            check(completion.Functions.Any(f => f.Name == "st_astext") && completion.Functions.Any(f => f.Name == "st_dwithin"), $"IntelliSense loads the PostGIS functions ({completion.Functions.Count} functions)");
            check(completion.Columns.TryGetValue(("public", "places"), out var placeColumns) && placeColumns.Any(c => c.Name == "geom" && c.Type == "geometry(Point,4326)"), "IntelliSense knows the geometry column and its type");
            string typed = "SELECT ST_AsT";
            var offered = SqlCompletionEngine.Complete(typed, typed.Length, completion);
            check(offered is not null && offered.Items.Any(i => i.Text.Equals("st_astext", StringComparison.OrdinalIgnoreCase)), "Typing ST_AsT offers ST_AsText: " + (offered is null ? "nothing" : string.Join(", ", offered.Items.Take(5).Select(i => i.Text))));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
