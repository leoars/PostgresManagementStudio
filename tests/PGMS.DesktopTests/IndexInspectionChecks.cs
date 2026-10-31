using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class IndexInspectionChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_idx_ui_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", table);
        string index = table + "_ix";
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int); CREATE INDEX {SqlIdentifier.Quote(index)} ON {target}(id);");
            var dialog = new RelationEditDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error = null;
            dialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.DropIndex;
                    await dialog.InspectAsync();
                    var list = (ListBox)dialog.FindName("ExistingIndexes"); list.SelectedIndex = 0;
                    check(((TextBox)dialog.FindName("ObjectName")).Text == index && ((TextBox)dialog.FindName("IndexDefinition")).Text.Contains("CREATE INDEX"), "index selection fills exact name and full definition");
                    RelationEdit Request() => (RelationEdit)typeof(RelationEditDialog).GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                    check(Request().ExpectedIdentity?.IndexOid == ((InspectedIndex)list.SelectedItem).Index.Oid, "index selection carries inspected OID into request");
                    ((TextBox)dialog.FindName("ObjectName")).Text = index + "_manual";
                    check(Request().ExpectedIdentity is null, "unmatched index name does not reuse snapshot identity");
                    ((TextBox)dialog.FindName("ObjectName")).Text = index;
                    TableEditChecks.Capture(dialog, "m66-index-identity");
                    TableEditChecks.Capture(dialog, "m65-index-selection");
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "m65-index-selection-min");
                    TableEditChecks.Capture(dialog, "m66-index-identity-min");
                    var close = (Button)dialog.FindName("CloseButton"); var root = (FrameworkElement)dialog.Content;
                    check(close.TransformToAncestor(root).Transform(new Point(0, close.ActualHeight)).Y <= root.ActualHeight + 0.5, $"index Drop actions fit minimum window (bottom={close.TransformToAncestor(root).Transform(new Point(0, close.ActualHeight)).Y}, height={root.ActualHeight})");
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.CreateIndex;
                    ((TextBox)dialog.FindName("Columns")).Text = "id";
                    await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "m65-index-create-min");
                    check(close.TransformToAncestor(root).Transform(new Point(0, close.ActualHeight)).Y <= root.ActualHeight + 0.5, $"index Create actions fit minimum window (bottom={close.TransformToAncestor(root).Transform(new Point(0, close.ActualHeight)).Y}, height={root.ActualHeight})");
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.DropIndex;
                    await session.ExecuteAsync($"ALTER TABLE {target} RENAME TO {SqlIdentifier.Quote(table + "_away")};");
                    await dialog.InspectAsync();
                    check(list.Items.Count == 1 && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("not found"), "failed index inspection retains snapshot and feedback");
                    await session.ExecuteAsync($"ALTER TABLE {ObjectSql.Relation("public", table + "_away")} RENAME TO {SqlIdentifier.Quote(table)};");
                    ((TextBox)dialog.FindName("TableName")).Text = table + "_missing";
                    check(list.Items.Count == 0 && ((TextBox)dialog.FindName("IndexDefinition")).Text.Length == 0, "changing target clears index snapshot and definition");
                    check(Request().ExpectedIdentity is null, "changing table clears index identity");
                    ((TextBox)dialog.FindName("TableName")).Text = table;
                    await dialog.InspectAsync(); list.SelectedIndex = 0;
                    ((Button)dialog.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { error = ex; dialog.Close(); }
            };
            dialog.ShowDialog(); if (error is not null) throw error;
            check(dialog.ScriptSql?.Contains("DROP INDEX") == true && (await new IndexInspectionService(connection).InspectAsync("public", table)).Count == 1, "selected index Script does not execute deletion");
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target}; DROP TABLE IF EXISTS {ObjectSql.Relation("public", table + "_away")};"); }
    }
}
