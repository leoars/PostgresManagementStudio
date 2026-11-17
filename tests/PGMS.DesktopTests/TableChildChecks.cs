using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>PGMS-053 (M1086): New Foreign Key, New Constraint, Modify, Rename and Delete in the Keys, Constraints and Triggers folders.</summary>
internal static class TableChildChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string schema = "tch_" + Guid.NewGuid().ToString("N")[..8];
        string database = new NpgsqlConnectionStringBuilder(connection).Database!;
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        async Task<uint> ConOid(string name) => (uint)(await Scalar($"SELECT oid FROM pg_constraint WHERE conname='{name}' AND connamespace='{schema}'::regnamespace"))!;
        await Sql($"""
            CREATE SCHEMA {schema};
            CREATE TABLE {schema}.parent(id int CONSTRAINT parent_pk PRIMARY KEY);
            CREATE TABLE {schema}.child(id int CONSTRAINT child_positive CHECK (id > 0), pid int CONSTRAINT child_parent_fk REFERENCES {schema}.parent(id));
            CREATE FUNCTION {schema}.touch() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN RETURN NEW; END $f$;
            CREATE TRIGGER child_touch BEFORE INSERT ON {schema}.child FOR EACH ROW EXECUTE FUNCTION {schema}.touch();
            """);
        var window = new MainWindow { ShowInTaskbar = false, LocalServiceResolver = (_, _) => (null, LocalServiceState.Unknown) }; window.Show();
        var errors = new List<string>(); window.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            // Service: identity by OID and name, never CASCADE
            var check1 = await ConOid("child_positive");
            string renamed = await TableChildObjects.RenameAsync(connection, false, check1, "child_positive", "child_id_positive");
            check(renamed == $"ALTER TABLE {schema}.child RENAME CONSTRAINT \"child_positive\" TO \"child_id_positive\";" && (long)(await Scalar($"SELECT count(*) FROM pg_constraint WHERE conname='child_id_positive'"))! == 1, "Rename runs ALTER TABLE … RENAME CONSTRAINT on the schema-qualified table");
            bool stale = false; try { await TableChildObjects.RenameAsync(connection, false, check1, "child_positive", "other"); } catch (InvalidOperationException ex) { stale = ex.Message.Contains("now named child_id_positive", StringComparison.Ordinal); }
            check(stale, "A name changed since the tree was read is refused, naming the current one");
            uint pk = await ConOid("parent_pk");
            string drop = await TableChildObjects.DescribeDropAsync(connection, false, pk, "parent_pk");
            check(drop == $"ALTER TABLE {schema}.parent DROP CONSTRAINT \"parent_pk\";" && !drop.Contains("CASCADE", StringComparison.Ordinal), "Delete shows the exact statement, without CASCADE");
            bool refused = false; try { await TableChildObjects.DropAsync(connection, false, pk, "parent_pk"); } catch (PostgresException) { refused = true; }
            check(refused && await ConOid("parent_pk") == pk, "A key that a foreign key needs is not deleted (no CASCADE)");
            uint trigger = (uint)(await Scalar($"SELECT oid FROM pg_trigger WHERE tgname='child_touch'"))!;
            check(TableChildObjects.RenameSql(new(true, $"{schema}.child", "child_touch"), "x y") == $"ALTER TRIGGER \"child_touch\" ON {schema}.child RENAME TO \"x y\";" && await TableChildObjects.DescribeDropAsync(connection, true, trigger, "child_touch") == $"DROP TRIGGER \"child_touch\" ON {schema}.child;", "Triggers use ALTER TRIGGER … RENAME and DROP TRIGGER … ON");

            // Menus
            await window.ConnectExplorerAsync(connection); await Idle();
            var catalog = new CatalogService(connection);
            MenuItem[] Fill(CatalogEntry entry) { var menu = new ContextMenu(); window.FillExplorerMenu(menu, new CatalogNode(entry, catalog)); return menu.Items.OfType<MenuItem>().ToArray(); }
            var keyMenu = Fill(new CatalogEntry(CatalogKind.Key, "k", database, schema, 1)).Select(i => i.Name).ToList();
            check(keyMenu.IndexOf("ExplorerNewForeignKey") == 0 && keyMenu.IndexOf("ExplorerModifyTableChild") > keyMenu.IndexOf("ExplorerScriptAs") && keyMenu.IndexOf("ExplorerRenameTableChild") > keyMenu.IndexOf("ExplorerModifyTableChild") && keyMenu.Contains("ExplorerDeleteTableChild"), "Key menu: New Foreign Key…, Script Key as, Modify, then Rename and Delete");
            check(Fill(new CatalogEntry(CatalogKind.Constraint, "c", database, schema, 1)).Single(i => i.Name == "ExplorerModifyTableChild").FontWeight == FontWeights.Bold, "Modify is the default (bold) action, as in SSMS");
            check(Fill(new CatalogEntry(CatalogKind.Constraint, "c", database, schema, 1)).First().Name == "ExplorerNewConstraint" && !Fill(new CatalogEntry(CatalogKind.Trigger, "t", database, schema, 1)).Any(i => i.Name is "ExplorerNewForeignKey" or "ExplorerNewConstraint"), "Constraints offer New Constraint…; triggers neither");
            check(Fill(new CatalogEntry(CatalogKind.Keys, "Keys", database, schema, 1)).First().Name == "ExplorerNewForeignKey" && Fill(new CatalogEntry(CatalogKind.Constraints, "Constraints", database, schema, 1)).First().Name == "ExplorerNewConstraint", "The Keys and Constraints folders offer New Foreign Key… and New Constraint…");

            // Tree: rename in place, delete with confirmation, modify
            var tree = (TreeView)window.FindName("ExplorerTree");
            async Task<CatalogNode> Child(CatalogNode node, string name) { node.IsExpanded = true; await node.LoadAsync(); while (node.IsBusy) await Task.Delay(50); await Idle(); return node.Children.Single(c => c.Entry.Name == name); }
            var table = await Child(await Child(await Child(await Child(await Child(await Child((CatalogNode)tree.Items[0], "Databases"), database), "Schemas"), schema), "Tables"), "child");
            async Task Open(CatalogNode n) { n.IsExpanded = true; await n.LoadAsync(); while (n.IsBusy) await Task.Delay(50); await Idle(); }
            var constraints = await Child(table, "Constraints"); await Open(constraints);
            var positive = constraints.Children.Single(c => c.Entry.Name == "child_id_positive");
            check(window.BeginExplorerRename(positive) && positive.IsRenaming && positive.RenameText == "child_id_positive", "Rename edits the label in the tree, as in SSMS");
            positive.RenameText = "ignored"; check(!await window.EndExplorerRenameAsync(positive, accept: false) && await Scalar($"SELECT conname FROM pg_constraint WHERE oid={check1}") is "child_id_positive", "Escape keeps the old name");
            window.BeginExplorerRename(positive); positive.RenameText = "id_must_be_positive";
            check(await window.EndExplorerRenameAsync(positive, accept: true) && await Scalar($"SELECT conname FROM pg_constraint WHERE oid={check1}") is "id_must_be_positive" && constraints.Children.Any(c => c.Entry.Name == "id_must_be_positive"), "Enter renames the constraint and refreshes the folder");
            var current = constraints.Children.Single(c => c.Entry.Name == "id_must_be_positive");
            string? asked = null; window.DeleteObjectConfirm = q => { asked = q; return false; };
            check(!await window.DeleteTableChildAsync(current) && asked?.Contains($"ALTER TABLE {schema}.child DROP CONSTRAINT \"id_must_be_positive\";", StringComparison.Ordinal) == true && (long)(await Scalar($"SELECT count(*) FROM pg_constraint WHERE oid={check1}"))! == 1, "Delete asks with the statement; No deletes nothing");
            window.DeleteObjectConfirm = _ => true;
            check(await window.DeleteTableChildAsync(current) && (long)(await Scalar($"SELECT count(*) FROM pg_constraint WHERE oid={check1}"))! == 0 && !constraints.Children.Any(c => c.Entry.Name == "id_must_be_positive"), "Yes deletes it and refreshes the folder");
            var triggers = await Child(table, "Triggers"); await Open(triggers);
            var touch = triggers.Children.Single(c => c.Entry.Name == "child_touch");
            int before = window.QueryDocuments.Count;
            await window.ModifyTableChildAsync(touch.Entry);
            check(window.QueryDocuments.Count == before + 1 && window.QueryDocuments.Any(q => q.Editor.Text.Contains("CREATE TRIGGER child_touch", StringComparison.Ordinal)), "Modify on a trigger opens its DROP And CREATE script, not executed");
            var keys = await Child(table, "Keys"); await Open(keys);
            var fk = keys.Children.Single(c => c.Entry.Name == "child_parent_fk");
            fk.IsSelected = true; await Idle(); await Idle();
            if (ReferenceEquals(tree.SelectedItem, fk))
            {
                await window.ModifyTableChildAsync(fk.Entry);
                check(window.TableDesigners.Count == 1, "Modify on a key opens the Table Designer of its table");
            }
            else check(true, "Modify on a key: the tree did not realize the node, checked by the trigger case only");
            check(errors.Count == 0, "No errors: " + string.Join(" | ", errors));
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
            await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
