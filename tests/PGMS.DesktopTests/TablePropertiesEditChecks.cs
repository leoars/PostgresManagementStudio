using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

// Table Properties from Object Explorer edits column name, type and nulls like the Table Designer, and Apply runs the reviewed script
// (pedido do usuário, 08/11/2026).
internal static class TablePropertiesEditChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "tp_" + Guid.NewGuid().ToString("N")[..8];
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.parent (id integer PRIMARY KEY, code text NOT NULL, seq_no integer DEFAULT 0); CREATE TABLE {schema}.child (id integer PRIMARY KEY, parent_id integer CONSTRAINT child_parent REFERENCES {schema}.parent (id));");
        async Task<string> Text(string sql) { await using var command = new Npgsql.NpgsqlCommand(sql, new Npgsql.NpgsqlConnection(connection)); await command.Connection!.OpenAsync(); try { return (string)(await command.ExecuteScalarAsync())!; } finally { await command.Connection.CloseAsync(); } }
        var dialog = new TablePropertiesDialog(connection, schema, "parent") { ShowInTaskbar = false, Opacity = 0 };
        dialog.Show();
        try
        {
            await dialog.InspectAsync();
            var apply = (Button)dialog.FindName("ApplyButton"); var feedback = (TextBlock)dialog.FindName("Feedback");
            var key = dialog.ColumnRows.Single(r => r.Name == "id"); var code = dialog.ColumnRows.Single(r => r.Name == "code");
            check(!((DataGrid)dialog.FindName("ColumnsGrid")).IsReadOnly && !key.CanAllowNulls && code.CanAllowNulls && !apply.IsEnabled && !dialog.HasPendingColumns, "Columns of an ordinary table can be edited; the key column cannot allow nulls");
            code.Name = "label"; dialog.CommitColumns();
            dialog.ColumnRows.Single(r => r.Name == "label").Type = "varchar(40)"; dialog.CommitColumns();
            dialog.ColumnRows.Single(r => r.Name == "label").Nullable = true; dialog.CommitColumns();
            check(dialog.HasPendingColumns && apply.IsEnabled, "Edited columns wait for Apply");
            dialog.ColumnRows.Single(r => r.Name == "id").Name = "label"; dialog.CommitColumns();
            check(dialog.ColumnRows.Any(r => r.Name == "id") && feedback.Text.Contains("more than once"), "A name used twice is refused and goes back");
            dialog.ColumnRows.Single(r => r.Name == "label").Type = "not a type;"; dialog.CommitColumns();
            check(dialog.ColumnRows.Single(r => r.Name == "label").Type == "varchar(40)" && feedback.Text.Contains("data type"), "An invalid type is refused and goes back");

            TableDesignChange? reviewed = null;
            dialog.ReviewPrompt = c => { reviewed = c; return false; };
            check(!await dialog.ApplyColumnsAsync() && dialog.HasPendingColumns && await Text($"SELECT string_agg(attname, ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid='{schema}.parent'::regclass AND attnum > 0") == "id,code,seq_no", "Refusing the script changes nothing");
            check(reviewed!.Sql.Contains("RENAME COLUMN \"code\" TO \"label\"") && reviewed.Sql.Contains("TYPE varchar(40)") && reviewed.Sql.Contains("\"label\" DROP NOT NULL"), "The script renames, changes the type and drops NOT NULL");
            dialog.ReviewPrompt = _ => true;
            check(await dialog.ApplyColumnsAsync() && !dialog.HasPendingColumns && dialog.ColumnRows.Any(r => r.Name == "label"), "Apply runs the script and reads the table again");
            check(await Text($"SELECT format_type(atttypid, atttypmod) || ' ' || (NOT attnotnull)::text FROM pg_attribute WHERE attrelid='{schema}.parent'::regclass AND attname='label'") == "character varying(40) true", "The database has the new name, type and nulls");

            dialog.ColumnRows.Single(r => r.Name == "id").Name = "parent_key"; dialog.CommitColumns();
            dialog.ReviewPrompt = c => { reviewed = c; return true; };
            check(await dialog.ApplyColumnsAsync() && reviewed!.Sql.Contains("RENAME COLUMN \"id\" TO \"parent_key\"") && !reviewed.Sql.Contains("DROP CONSTRAINT"), "Renaming the key column renames it without rebuilding the key");
            check((await Text($"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='child_parent' AND conrelid='{schema}.child'::regclass")).Contains("parent_key"), "The foreign key of another table follows the renamed column");

            // Identity and nulls through the grid itself, as the mouse does: the row is still being edited when the change is checked.
            static T? Child<T>(DependencyObject root) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var c = VisualTreeHelper.GetChild(root, i); if (c is T found) return found; if (Child<T>(c) is { } deeper) return deeper; }
                return null;
            }
            static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)dialog.FindName("ColumnsGrid");
            ((TabControl)dialog.FindName("Details")).SelectedIndex = 1; await Idle();
            var seq = dialog.ColumnRows.Single(r => r.Name == "seq_no");
            check(seq.Identity == "No" && seq.CanSetIdentity && seq.Nullable, "Identity shows No for a plain column and can be set");
            grid.SelectedItem = seq; grid.CurrentCell = new DataGridCellInfo(seq, grid.Columns[4]); grid.BeginEdit(); await Idle();
            var identityBox = Child<ComboBox>(grid.Columns[4].GetCellContent(seq)!);
            identityBox!.SelectedItem = "By Default"; await Idle();
            grid.CommitEdit(DataGridEditingUnit.Row, true); await Idle();
            check(dialog.HasPendingColumns && !seq.Nullable && !seq.CanAllowNulls && seq.ToDesign() is { Identity: ColumnIdentity.ByDefault, Default: null } && feedback.Text.Contains("replaces the default of seq_no"),
                "Identity chosen in the grid makes the column NOT NULL and replaces its default");
            var label = dialog.ColumnRows.Single(r => r.Name == "label");
            var nulls = Child<CheckBox>(grid.Columns[3].GetCellContent(label)!);
            ((IToggleProvider)new CheckBoxAutomationPeer(nulls!).GetPattern(PatternInterface.Toggle)).Toggle(); await Idle();
            check(!label.Nullable && dialog.ColumnRows.Contains(label), "The Nullable box of the grid changes the column and the rows stay");
            label.Identity = "Always"; dialog.CommitColumns();
            check(dialog.ColumnRows.Single(r => r.Name == "label").Identity == "No" && feedback.Text.Contains("identity requires"), "Identity on a text column is refused and goes back");
            check(await dialog.ApplyColumnsAsync() && reviewed!.Sql.Contains("\"seq_no\" DROP DEFAULT") && reviewed.Sql.Contains("\"seq_no\" ADD GENERATED BY DEFAULT AS IDENTITY") && reviewed.Sql.Contains("\"label\" SET NOT NULL"),
                "Apply drops the default, adds the identity and sets NOT NULL");
            check(await Text($"SELECT attidentity::text || ' ' || attnotnull::text || ' ' || (SELECT count(*) FROM pg_attrdef d WHERE d.adrelid=a.attrelid AND d.adnum=a.attnum)::text FROM pg_attribute a WHERE attrelid='{schema}.parent'::regclass AND attname='seq_no'") == "d true 0",
                "The database has the identity column without the old default");
            check(dialog.ColumnRows.Single(r => r.Name == "seq_no").Identity == "By Default", "After Apply the grid shows the identity read back");

            dialog.ColumnRows.Single(r => r.Name == "label").Nullable = true; dialog.CommitColumns();
            bool asked = false; dialog.ConfirmPrompt = _ => { asked = true; return false; };
            dialog.Close();
            check(asked && dialog.IsVisible, "Closing with changes not applied asks first, and No keeps the window open");
        }
        finally
        {
            dialog.ConfirmPrompt = _ => true; dialog.Close();
            await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE;");
        }
    }
}
