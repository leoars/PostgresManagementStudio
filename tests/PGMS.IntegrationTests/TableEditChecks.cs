using Npgsql;
using PGMS.PostgreSql;

internal static class TableEditChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "pgms_table_edits_" + Guid.NewGuid().ToString("N");
        string destination = schema + "_dest\"";
        string destinationSql = SqlIdentifier.Quote(destination);
        string q = SqlIdentifier.Quote(schema);
        string limitedRole = schema + "_reader";
        await using var session = new QuerySession(connection); await session.OpenAsync();
        var service = new AdministrativeEditService(connection);
        try
        {
            await session.ExecuteAsync($"CREATE SCHEMA {q}; CREATE TABLE {q}.original(id integer PRIMARY KEY, note text); INSERT INTO {q}.original VALUES (1,'ação'); CREATE VIEW {q}.kept_view AS SELECT * FROM {q}.original;");
            const string renamed = "renamed\"table";
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.Rename, renamed));
            var value = await session.ExecuteAsync($"SELECT note FROM {q}.{SqlIdentifier.Quote(renamed)}; SELECT note FROM {q}.kept_view;");
            check((string)value.Tables[0].Rows[0][0] == "ação" && (string)value.Tables[1].Rows[0][0] == "ação", "table rename preserves data and dependent view with quoted name");
            await service.ApplyAsync(new TableEdit(schema, renamed, TableOperation.Rename, "original"));
            bool viewRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "kept_view", TableOperation.Rename, "wrong")); } catch (InvalidOperationException) { viewRefused = true; }
            check(viewRefused && (await session.ExecuteAsync($"SELECT * FROM {q}.kept_view;")).Tables[0].Rows.Count == 1, "table operations refuse a view without changing it");
            await session.ExecuteAsync($"CREATE TABLE {q}.parent(id int); CREATE TABLE {q}.child() INHERITS ({q}.parent);");
            bool inheritanceRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "parent", TableOperation.Rename, "wrong")); } catch (InvalidOperationException) { inheritanceRefused = true; }
            check(inheritanceRefused, "table operations reject inheritance hierarchies");
            bool protectedSchema = false;
            try { new TableEdit("pg_catalog", "pg_class", TableOperation.Rename, "wrong").ToSql(); } catch (ArgumentException) { protectedSchema = true; }
            check(protectedSchema, "table operations protect system schemas");
            await session.ExecuteAsync($"CREATE TABLE {q}.nullability(value text); INSERT INTO {q}.nullability VALUES (NULL);");
            bool existingNullRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "nullability", TableOperation.SetNotNull, Column: "value")); } catch (PostgresException ex) when (ex.SqlState == "23502") { existingNullRefused = true; }
            check(existingNullRefused && (await session.ExecuteAsync($"SELECT value FROM {q}.nullability;")).Tables[0].Rows[0][0] is DBNull, "SET NOT NULL refuses existing NULL and preserves data");
            await session.ExecuteAsync($"UPDATE {q}.nullability SET value='kept';");
            await service.ApplyAsync(new TableEdit(schema, "nullability", TableOperation.SetNotNull, Column: "value"));
            bool enforced = false;
            try { await session.ExecuteAsync($"INSERT INTO {q}.nullability VALUES (NULL);"); } catch (PostgresException ex) when (ex.SqlState == "23502") { enforced = true; }
            check(enforced, "SET NOT NULL enforces future writes");
            await service.ApplyAsync(new TableEdit(schema, "nullability", TableOperation.DropNotNull, Column: "value"));
            await session.ExecuteAsync($"INSERT INTO {q}.nullability VALUES (NULL);");
            check((await session.ExecuteAsync($"SELECT value FROM {q}.nullability WHERE value IS NULL;")).Tables[0].Rows.Count == 1, "DROP NOT NULL permits actual NULL writes");
            bool primaryNullRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.DropNotNull, Column: "id")); } catch (PostgresException ex) when (ex.SqlState == "42P16") { primaryNullRefused = true; }
            check(primaryNullRefused, "DROP NOT NULL preserves primary-key requirement");
            const string literal = "ação'\\path; DROP TABLE ignored; --";
            await service.ApplyAsync(new TableEdit(schema, "nullability", TableOperation.SetDefault, Column: "value", DefaultValue: literal));
            var defaultRow = await session.ExecuteAsync($"INSERT INTO {q}.nullability DEFAULT VALUES RETURNING value;");
            check((string)defaultRow.Tables[0].Rows[0][0] == literal, "literal default preserves Unicode, quotes, backslashes and SQL-like text");
            check((await session.ExecuteAsync($"SELECT value FROM {q}.nullability WHERE value='kept';")).Tables[0].Rows.Count == 1, "SET DEFAULT leaves existing rows unchanged");
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.SetDefault, Column: "id", DefaultValue: "42"));
            bool badDefaultRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.SetDefault, Column: "id", DefaultValue: "bad integer")); } catch (PostgresException ex) when (ex.SqlState == "22P02") { badDefaultRefused = true; }
            check(badDefaultRefused && (string)(await session.ExecuteAsync($"INSERT INTO {q}.original DEFAULT VALUES RETURNING id;")).Tables[0].Rows[0][0] == "42", "invalid default conversion rolls back and preserves previous default");
            await session.ExecuteAsync($"DELETE FROM {q}.original WHERE id=42;");
            await service.ApplyAsync(new TableEdit(schema, "nullability", TableOperation.SetDefault, Column: "value", DefaultValue: ""));
            check((string)(await session.ExecuteAsync($"INSERT INTO {q}.nullability DEFAULT VALUES RETURNING value;")).Tables[0].Rows[0][0] == "", "empty literal default stays empty text");
            await service.ApplyAsync(new TableEdit(schema, "nullability", TableOperation.DropDefault, Column: "value"));
            check((await session.ExecuteAsync($"INSERT INTO {q}.nullability DEFAULT VALUES RETURNING value;")).Tables[0].Rows[0][0] is DBNull && (await session.ExecuteAsync($"SELECT value FROM {q}.nullability WHERE value='kept';")).Tables[0].Rows.Count == 1, "DROP DEFAULT affects future inserts and preserves previous rows");
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.DropDefault, Column: "id"));
            bool stillRequired = false;
            try { await session.ExecuteAsync($"INSERT INTO {q}.original DEFAULT VALUES;"); } catch (PostgresException ex) when (ex.SqlState == "23502") { stillRequired = true; }
            check(stillRequired, "DROP DEFAULT preserves NOT NULL and primary key");
            await session.ExecuteAsync($"CREATE SCHEMA {destinationSql}; CREATE TABLE {q}.moving(id serial PRIMARY KEY, value text); INSERT INTO {q}.moving(value) VALUES ('kept'); CREATE VIEW {q}.moving_view AS SELECT * FROM {q}.moving;");
            await service.ApplyAsync(new TableEdit(schema, "moving", TableOperation.SetSchema, destination));
            check((string)(await session.ExecuteAsync($"SELECT value FROM {destinationSql}.moving;")).Tables[0].Rows[0][0] == "kept" && (await session.ExecuteAsync($"SELECT * FROM {q}.moving_view;")).Tables[0].Rows.Count == 1, "SET SCHEMA preserves rows and dependent view with quoted schema");
            check((string)(await session.ExecuteAsync($"INSERT INTO {destinationSql}.moving(value) VALUES ('next') RETURNING id;")).Tables[0].Rows[0][0] == "2" && (await session.ExecuteAsync($"SELECT * FROM {destinationSql}.moving_id_seq;")).Tables[0].Rows.Count == 1, "SET SCHEMA moves owned sequence and preserves its default");
            await session.ExecuteAsync($"CREATE TABLE {q}.moving(id int);");
            bool collisionRefused = false;
            try { await service.ApplyAsync(new TableEdit(destination, "moving", TableOperation.SetSchema, schema)); } catch (PostgresException ex) when (ex.SqlState == "42P07") { collisionRefused = true; }
            check(collisionRefused && (await session.ExecuteAsync($"SELECT * FROM {destinationSql}.moving;")).Tables[0].Rows.Count == 2, "SET SCHEMA collision rolls back and leaves source intact");
            bool systemDestinationRefused = false;
            try { new TableEdit(schema, "original", TableOperation.SetSchema, "pg_catalog").ToSql(); } catch (ArgumentException) { systemDestinationRefused = true; }
            check(systemDestinationRefused, "SET SCHEMA protects system destination");
            const string tableComment = "Documentação 'quoted'\\path\n; SQL remains text";
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.CommentTable, Comment: tableComment));
            check((await new TablePropertiesService(connection).InspectAsync(schema, "original")).Comment == tableComment, "table comment preserves Unicode, quotes, backslashes and newlines");
            bool emptyCommentRefused = false;
            try { new TableEdit(schema, "original", TableOperation.CommentTable).ToSql(); } catch (ArgumentException) { emptyCommentRefused = true; }
            check(emptyCommentRefused, "empty comment requires explicit removal choice");
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.CommentTable, RemoveComment: true));
            check((await new TablePropertiesService(connection).InspectAsync(schema, "original")).Comment is null && (await session.ExecuteAsync($"SELECT * FROM {q}.original;")).Tables[0].Rows.Count == 1, "explicit comment removal preserves table data");
            const string commentColumn = "note\"value";
            await session.ExecuteAsync($"CREATE TABLE {q}.comment_target({SqlIdentifier.Quote(commentColumn)} text); INSERT INTO {q}.comment_target VALUES ('kept');");
            await service.ApplyAsync(new TableEdit(schema, "comment_target", TableOperation.CommentColumn, Column: commentColumn, Comment: tableComment));
            check((await new TablePropertiesService(connection).InspectAsync(schema, "comment_target")).Columns.Single().Comment == tableComment, "column comment quotes exact identifier and preserves literal content");
            bool missingCommentColumn = false;
            try { await service.ApplyAsync(new TableEdit(schema, "comment_target", TableOperation.CommentColumn, Column: "absent", Comment: "wrong")); } catch (PostgresException ex) when (ex.SqlState == "42703") { missingCommentColumn = true; }
            check(missingCommentColumn && (await new TablePropertiesService(connection).InspectAsync(schema, "comment_target")).Columns.Single().Comment == tableComment, "missing column comment fails without altering other metadata");
            await service.ApplyAsync(new TableEdit(schema, "comment_target", TableOperation.CommentColumn, Column: commentColumn, RemoveComment: true));
            check((await new TablePropertiesService(connection).InspectAsync(schema, "comment_target")).Columns.Single().Comment is null && (string)(await session.ExecuteAsync($"SELECT * FROM {q}.comment_target;")).Tables[0].Rows[0][0] == "kept", "column comment removal preserves stored values");
            // More table operation checks are added by subsequent milestones.
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.RenameColumn, "renamed\"note", "note"));
            var renamedColumnData = await session.ExecuteAsync($"SELECT {SqlIdentifier.Quote("renamed\"note")} FROM {q}.original; SELECT note FROM {q}.kept_view;");
            check((string)renamedColumnData.Tables[0].Rows[0][0] == "ação" && (string)renamedColumnData.Tables[1].Rows[0][0] == "ação", "column rename preserves values and dependent view");
            bool duplicateColumnRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.RenameColumn, "id", "renamed\"note")); } catch (PostgresException ex) when (ex.SqlState == "42701") { duplicateColumnRefused = true; }
            check(duplicateColumnRefused && (await session.ExecuteAsync($"SELECT {SqlIdentifier.Quote("renamed\"note")} FROM {q}.original;")).Tables[0].Rows.Count == 1, "duplicate column rename rolls back without losing the original column");
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.RenameColumn, "note", "renamed\"note"));
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.AddColumn, Column: "extra\"text", Type: ColumnType.Text));
            check((await session.ExecuteAsync($"SELECT {SqlIdentifier.Quote("extra\"text")} FROM {q}.original;")).Tables[0].Rows[0][0] is DBNull, "ADD COLUMN preserves rows and fills nullable quoted column with NULL");
            bool notNullRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.AddColumn, Column: "required", Type: ColumnType.Integer, Nullable: false)); } catch (PostgresException ex) when (ex.SqlState == "23502") { notNullRefused = true; }
            var original = (await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", schema))).Single(t => t.Name == "original");
            check(notNullRefused && !(await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Columns, "Columns", "postgres", schema, original.Oid))).Any(c => c.Name.StartsWith("required (", StringComparison.Ordinal)), "failed NOT NULL addition leaves no partial column");
            await session.ExecuteAsync($"CREATE TABLE {q}.empty_table(id int);");
            await service.ApplyAsync(new TableEdit(schema, "empty_table", TableOperation.AddColumn, Column: "required", Type: ColumnType.Integer, Nullable: false));
            bool nullRejected = false;
            try { await session.ExecuteAsync($"INSERT INTO {q}.empty_table(id) VALUES (1);"); } catch (PostgresException ex) when (ex.SqlState == "23502") { nullRejected = true; }
            check(nullRejected, "NOT NULL added to an empty table is enforced by PostgreSQL");
            bool columnDependencyRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.DropColumn, Column: "note")); } catch (PostgresException ex) when (ex.SqlState == "2BP01") { columnDependencyRefused = true; }
            check(columnDependencyRefused && (string)(await session.ExecuteAsync($"SELECT note FROM {q}.kept_view;")).Tables[0].Rows[0][0] == "ação", "DROP COLUMN RESTRICT preserves column and dependent view");
            await session.ExecuteAsync($"ALTER TABLE {q}.original ADD COLUMN disposable int UNIQUE; UPDATE {q}.original SET disposable=7;");
            await service.ApplyAsync(new TableEdit(schema, "original", TableOperation.DropColumn, Column: "disposable"));
            var remainingColumns = await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Columns, "Columns", "postgres", schema, original.Oid));
            check(!remainingColumns.Any(c => c.Name.StartsWith("disposable (", StringComparison.Ordinal)) && (await session.ExecuteAsync($"SELECT id FROM {q}.original;")).Tables[0].Rows.Count == 1, "DROP COLUMN removes column and preserves other row values");
            check(!(await new TableConstraintService(connection).InspectAsync(schema, "original")).Any(c => c.Name.Contains("disposable", StringComparison.Ordinal)), "DROP COLUMN also removes its local UNIQUE constraint");
            await session.ExecuteAsync($"CREATE ROLE {SqlIdentifier.Quote(limitedRole)} LOGIN; GRANT USAGE ON SCHEMA {q} TO {SqlIdentifier.Quote(limitedRole)}; GRANT SELECT ON {q}.original TO {SqlIdentifier.Quote(limitedRole)};");
            bool ownershipRefused = false;
            var limited = new NpgsqlConnectionStringBuilder(connection) { Username = limitedRole }.ConnectionString;
            try { await new AdministrativeEditService(limited).ApplyAsync(new TableEdit(schema, "original", TableOperation.DropColumn, Column: "id")); } catch (PostgresException ex) when (ex.SqlState == "42501") { ownershipRefused = true; }
            check(ownershipRefused, "table edit guard respects non-owner PostgreSQL permissions");
            bool commentPermissionRefused = false;
            try { await new AdministrativeEditService(limited).ApplyAsync(new TableEdit(schema, "original", TableOperation.CommentTable, Comment: "denied")); } catch (PostgresException ex) when (ex.SqlState == "42501") { commentPermissionRefused = true; }
            check(commentPermissionRefused, "table comment respects non-owner permissions");
            bool columnCommentPermissionRefused = false;
            try { await new AdministrativeEditService(limited).ApplyAsync(new TableEdit(schema, "original", TableOperation.CommentColumn, Column: "id", Comment: "denied")); } catch (PostgresException ex) when (ex.SqlState == "42501") { columnCommentPermissionRefused = true; }
            check(columnCommentPermissionRefused, "column comment respects non-owner permissions");
            await session.ExecuteAsync($"GRANT CREATE ON SCHEMA {q} TO {SqlIdentifier.Quote(limitedRole)}; CREATE TABLE {q}.limited_owned(id int); ALTER TABLE {q}.limited_owned OWNER TO {SqlIdentifier.Quote(limitedRole)};");
            bool destinationPermissionRefused = false;
            try { await new AdministrativeEditService(limited).ApplyAsync(new TableEdit(schema, "limited_owned", TableOperation.SetSchema, destination)); } catch (PostgresException ex) when (ex.SqlState == "42501") { destinationPermissionRefused = true; }
            check(destinationPermissionRefused, "SET SCHEMA requires CREATE permission on destination even for owner");
            await session.ExecuteAsync($"CREATE TABLE {q}.drop_me(id int); INSERT INTO {q}.drop_me VALUES (7); CREATE VIEW {q}.drop_dependency AS SELECT * FROM {q}.drop_me;");
            bool dependencyRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "drop_me", TableOperation.Drop)); } catch (PostgresException ex) when (ex.SqlState == "2BP01") { dependencyRefused = true; }
            check(dependencyRefused && (await session.ExecuteAsync($"SELECT * FROM {q}.drop_me;")).Tables[0].Rows.Count == 1, "DROP TABLE RESTRICT preserves rows when a view depends on the table");
            await session.ExecuteAsync($"DROP VIEW {q}.drop_dependency;");
            await service.ApplyAsync(new TableEdit(schema, "drop_me", TableOperation.Drop));
            check(!(await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", schema))).Any(t => t.Name == "drop_me"), "DROP TABLE removes dependency-free table from actual catalog");
            bool dropViewRefused = false;
            try { await service.ApplyAsync(new TableEdit(schema, "kept_view", TableOperation.Drop)); } catch (InvalidOperationException) { dropViewRefused = true; }
            check(dropViewRefused, "DROP TABLE guard rejects a view");
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {q} CASCADE; DROP SCHEMA IF EXISTS {destinationSql} CASCADE; DROP ROLE IF EXISTS {SqlIdentifier.Quote(limitedRole)};"); }
    }
}
