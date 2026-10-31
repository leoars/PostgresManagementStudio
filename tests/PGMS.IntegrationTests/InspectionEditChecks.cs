using PGMS.PostgreSql;

internal static class InspectionEditChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_inspected_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var service = new AdministrativeEditService(connection);
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int, note text);");
            var snapshot = await new TablePropertiesService(connection).InspectAsync("public", table);
            await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentTable, Comment: "same table") { ExpectedTableOid = snapshot.Oid });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Comment == "same table", "inspected identity accepts unchanged table");
            await session.ExecuteAsync($"DROP TABLE {target}; CREATE TABLE {target}(id int, note text);");
            bool rejected = false;
            try { await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentTable, Comment: "wrong table") { ExpectedTableOid = snapshot.Oid }); } catch (InvalidOperationException ex) when (ex.Message.Contains("identity", StringComparison.Ordinal)) { rejected = true; }
            check(rejected && (await new TablePropertiesService(connection).InspectAsync("public", table)).Comment is null, "inspection guard rejects replacement table with same name");
            snapshot = await new TablePropertiesService(connection).InspectAsync("public", table);
            short notePosition = snapshot.Columns.Single(c => c.Name == "note").Position;
            await session.ExecuteAsync($"ALTER TABLE {target} DROP COLUMN note; ALTER TABLE {target} ADD COLUMN note text;");
            bool columnRejected = false;
            try { await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentColumn, Column: "note", Comment: "stale") { ExpectedTableOid = snapshot.Oid, ExpectedColumnPosition = notePosition }); } catch (InvalidOperationException ex) when (ex.Message.Contains("Column identity", StringComparison.Ordinal)) { columnRejected = true; }
            check(columnRejected, "column identity guard rejects dropped and re-added name");
            snapshot = await new TablePropertiesService(connection).InspectAsync("public", table);
            await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentColumn, Column: "note", Comment: "fresh") { ExpectedTableOid = snapshot.Oid, ExpectedColumnPosition = snapshot.Columns.Single(c => c.Name == "note").Position });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Comment == "fresh", "fresh column inspection permits edit");
            await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentTable, Comment: "other writer"));
            bool commentRejected = false;
            try { await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentTable, Comment: "overwrite") { ExpectedTableOid = snapshot.Oid, CheckComment = true, ExpectedComment = snapshot.Comment }); } catch (InvalidOperationException ex) when (ex.Message.Contains("Comment changed", StringComparison.Ordinal)) { commentRejected = true; }
            check(commentRejected && (await new TablePropertiesService(connection).InspectAsync("public", table)).Comment == "other writer", "table comment conflict preserves concurrent writer");
            await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentTable, Comment: "reviewed") { ExpectedTableOid = snapshot.Oid, CheckComment = true, ExpectedComment = "other writer" });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Comment == "reviewed", "reviewed comment version can be changed");
            await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentColumn, Column: "note", Comment: "concurrent column"));
            bool columnCommentRejected = false;
            try { await service.ApplyAsync(new TableEdit("public", table, TableOperation.CommentColumn, Column: "note", RemoveComment: true) { ExpectedTableOid = snapshot.Oid, ExpectedColumnPosition = snapshot.Columns.Single(c => c.Name == "note").Position, CheckComment = true, ExpectedComment = "fresh" }); } catch (InvalidOperationException ex) when (ex.Message.Contains("Comment changed", StringComparison.Ordinal)) { columnCommentRejected = true; }
            check(columnCommentRejected, "column comment removal refuses changed comment");
            await session.ExecuteAsync($"CREATE VIEW public.{SqlIdentifier.Quote(table + "_view")} AS SELECT id FROM {target}; CREATE SEQUENCE public.{SqlIdentifier.Quote(table + "_seq")}; CREATE TABLE public.{SqlIdentifier.Quote(table + "_part")}(id int) PARTITION BY RANGE(id);");
            var catalog = new CatalogService(connection);
            var tables = await catalog.ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", "public"));
            check(tables.Single(t => t.Name == table).CanEditTable && tables.Single(t => t.Name == table + "_part").CanInspectTable && !tables.Single(t => t.Name == table + "_part").CanEditTable, "catalog distinguishes ordinary and partitioned table actions");
            var views = await catalog.ChildrenAsync(new(CatalogKind.Views, "Views", "postgres", "public"));
            var sequences = await catalog.ChildrenAsync(new(CatalogKind.Sequences, "Sequences", "postgres", "public"));
            check(!views.Single(t => t.Name == table + "_view").CanInspectTable && !sequences.Single(t => t.Name == table + "_seq").CanEditTable, "catalog does not expose table actions on view or sequence");
            var defaultSnapshot = await new TablePropertiesService(connection).InspectAsync("public", table);
            var defaultRequest = new TableEdit("public", table, TableOperation.DropDefault, Column: "note")
            { ExpectedTableOid = defaultSnapshot.Oid, ExpectedColumnPosition = defaultSnapshot.Columns.Single(c => c.Name == "note").Position, CheckDefault = true, ExpectedDefaultExpression = null };
            await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note SET DEFAULT 'concurrent default';");
            bool defaultRejected = false;
            try { await service.ApplyAsync(defaultRequest); } catch (InvalidOperationException ex) when (ex.Message.Contains("Default changed", StringComparison.Ordinal)) { defaultRejected = true; }
            var currentDefault = (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").DefaultExpression;
            check(defaultRejected && currentDefault == "'concurrent default'::text", "default removal conflict preserves concurrently added default");
            await service.ApplyAsync(defaultRequest with { Operation = TableOperation.SetDefault, DefaultValue = "reviewed default", ExpectedDefaultExpression = currentDefault });
            currentDefault = (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").DefaultExpression;
            check(currentDefault == "'reviewed default'::text", "reviewed default can be replaced");
            await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note DROP DEFAULT;");
            bool removedDefaultRejected = false;
            try { await service.ApplyAsync(defaultRequest with { Operation = TableOperation.SetDefault, DefaultValue = "stale", ExpectedDefaultExpression = currentDefault }); } catch (InvalidOperationException ex) when (ex.Message.Contains("Default changed", StringComparison.Ordinal)) { removedDefaultRejected = true; }
            check(removedDefaultRejected && (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").DefaultExpression is null, "default replacement refuses concurrently removed expression");
            await service.ApplyAsync(defaultRequest with { Operation = TableOperation.SetDefault, DefaultValue = "from absent" });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").DefaultExpression == "'from absent'::text", "unchanged absent default permits inspected assignment");
            var nullableRequest = defaultRequest with { Operation = TableOperation.DropNotNull, CheckDefault = false, ExpectedNullable = true };
            await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note SET NOT NULL;");
            bool nullableRejected = false;
            try { await service.ApplyAsync(nullableRequest); } catch (InvalidOperationException ex) when (ex.Message.Contains("Nullability changed", StringComparison.Ordinal)) { nullableRejected = true; }
            check(nullableRejected && !(await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Nullable, "DROP NOT NULL conflict preserves concurrent restriction");
            await service.ApplyAsync(nullableRequest with { ExpectedNullable = false });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Nullable, "reviewed NOT NULL state can be relaxed");
            await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note SET NOT NULL; ALTER TABLE {target} ALTER COLUMN note DROP NOT NULL;");
            bool notNullRejected = false;
            try { await service.ApplyAsync(nullableRequest with { Operation = TableOperation.SetNotNull, ExpectedNullable = false }); } catch (InvalidOperationException ex) when (ex.Message.Contains("Nullability changed", StringComparison.Ordinal)) { notNullRejected = true; }
            check(notNullRejected && (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Nullable, "SET NOT NULL conflict preserves concurrent relaxation");
            await service.ApplyAsync(nullableRequest with { Operation = TableOperation.SetNotNull });
            check(!(await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Nullable, "reviewed nullable state can be restricted");
            var typeSnapshot = await new TablePropertiesService(connection).InspectAsync("public", table);
            var typeColumn = typeSnapshot.Columns.Single(c => c.Name == "note");
            var typeRequest = new TableEdit("public", table, TableOperation.CommentColumn, Column: "note", Comment: "type reviewed")
            { ExpectedTableOid = typeSnapshot.Oid, ExpectedColumnPosition = typeColumn.Position, ExpectedColumnType = typeColumn.TypeIdentity };
            await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note TYPE varchar(20);");
            bool typeRejected = false;
            try { await service.ApplyAsync(typeRequest); } catch (InvalidOperationException ex) when (ex.Message.Contains("Column type changed", StringComparison.Ordinal)) { typeRejected = true; }
            check(typeRejected, "inspected column edit refuses changed type OID with same column position");
            typeColumn = (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note");
            typeRequest = typeRequest with { ExpectedColumnType = typeColumn.TypeIdentity };
            await service.ApplyAsync(typeRequest);
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Comment == "type reviewed", "reinspected type permits column edit");
            await session.ExecuteAsync($"ALTER TABLE {target} ALTER COLUMN note TYPE varchar(30);");
            bool modifierRejected = false;
            try { await service.ApplyAsync(typeRequest with { Comment = "stale modifier" }); } catch (InvalidOperationException ex) when (ex.Message.Contains("Column type changed", StringComparison.Ordinal)) { modifierRejected = true; }
            typeColumn = (await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note");
            check(modifierRejected && typeColumn.Comment == "type reviewed" && typeColumn.TypeIdentity!.Oid == typeRequest.ExpectedColumnType!.Oid, "type modifier change is refused even when type OID is unchanged");
            await service.ApplyAsync(typeRequest with { ExpectedColumnType = typeColumn.TypeIdentity, Comment = "modifier reviewed" });
            check((await new TablePropertiesService(connection).InspectAsync("public", table)).Columns.Single(c => c.Name == "note").Comment == "modifier reviewed", "reinspected type modifier permits edit");
            // Further inspected editing checks.
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target} CASCADE; DROP SEQUENCE IF EXISTS public.{SqlIdentifier.Quote(table + "_seq")}; DROP TABLE IF EXISTS public.{SqlIdentifier.Quote(table + "_part")};"); }
    }
}
