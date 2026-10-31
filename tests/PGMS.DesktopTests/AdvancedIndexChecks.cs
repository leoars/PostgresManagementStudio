using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class AdvancedIndexChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string table = "pgms_adv_ui_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {target}(id int, payload text);");
            var dialog = new RelationEditDialog(connection, "public", table) { ShowInTaskbar = false };
            Exception? error = null;
            dialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    TextBox Field(string name) => (TextBox)dialog.FindName(name);
                    Field("ObjectName").Text = table + "_ix"; Field("Columns").Text = "id";
                    Field("IncludedColumns").Text = "id";
                    check(!((Button)dialog.FindName("ApplyButton")).IsEnabled && Field("Preview").Text == "", "overlapping INCLUDE disables Apply and clears preview");
                    Field("IncludedColumns").Text = "payload";
                    check(Field("Preview").Text.Contains("INCLUDE (\"payload\")"), "INCLUDE preview quotes payload");
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.DropIndex;
                    check(!Field("Preview").Text.Contains("INCLUDE"), "Drop ignores creation-only INCLUDE");
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.CreateIndex;
                    check(Field("IncludedColumns").Text == "payload", "operation switch retains INCLUDE input");
                    var unique = (CheckBox)dialog.FindName("Unique");
                    var nulls = (CheckBox)dialog.FindName("NullsNotDistinct");
                    check(!nulls.IsEnabled && nulls.IsChecked != true, "NULL equality defaults off for ordinary index");
                    unique.IsChecked = true; nulls.IsChecked = true;
                    check(Field("Preview").Text.Contains("NULLS NOT DISTINCT"), "NULL equality updates unique preview");
                    unique.IsChecked = false;
                    check(nulls.IsChecked == false && !Field("Preview").Text.Contains("NULLS NOT DISTINCT"), "turning off uniqueness clears dependent NULL option");
                    unique.IsChecked = true; nulls.IsChecked = true;
                    Field("FillFactor").Text = "75.5";
                    check(!((Button)dialog.FindName("ApplyButton")).IsEnabled, "fractional fillfactor disables Apply");
                    Field("FillFactor").Text = "101";
                    check(Field("Preview").Text == "", "out-of-range fillfactor clears SQL preview");
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.DropIndex;
                    check(Field("Preview").Text.StartsWith("DROP INDEX"), "Drop ignores invalid creation fillfactor");
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.CreateIndex;
                    Field("FillFactor").Text = "75";
                    check(Field("Preview").Text.Contains("WITH (fillfactor = 75)"), "valid fillfactor restores preview");
                    var method = (ComboBox)dialog.FindName("Method");
                    method.SelectedItem = IndexMethod.Hash;
                    check(!((Button)dialog.FindName("ApplyButton")).IsEnabled && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("Hash requires"), "incompatible hash options show correction and disable Apply");
                    check(Field("IncludedColumns").Text == "payload" && unique.IsChecked == true, "method switch preserves proposed options for review");
                    method.SelectedItem = IndexMethod.BTree;
                    check(((Button)dialog.FindName("ApplyButton")).IsEnabled, "returning to BTree restores valid options");
                    ((ComboBox)dialog.FindName("SortOrder")).SelectedItem = IndexSortOrder.Descending;
                    ((ComboBox)dialog.FindName("NullPlacement")).SelectedItem = IndexNullPlacement.Last;
                    check(Field("Preview").Text.Contains("\"id\" DESC NULLS LAST"), "BTree ordering updates exact SQL preview");
                    unique.IsChecked = false; Field("IncludedColumns").Clear(); method.SelectedItem = IndexMethod.Hash;
                    check(Field("Preview").Text == "" && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("ordering requires"), "hash rejects explicit BTree ordering in UI");
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "advanced-index-error-min");
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.DropIndex;
                    check(Field("Preview").Text.StartsWith("DROP INDEX"), "Drop ignores incompatible creation ordering");
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "advanced-index-drop-min");
                    dialog.Width = 780; dialog.Height = 800;
                    ((ComboBox)dialog.FindName("Operation")).SelectedValue = RelationOperation.CreateIndex;
                    method.SelectedItem = IndexMethod.BTree; unique.IsChecked = true; nulls.IsChecked = true; Field("IncludedColumns").Text = "payload";
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "advanced-index");
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(dialog, "advanced-index-min");
                    var root = (FrameworkElement)dialog.Content; var close = (Button)dialog.FindName("CloseButton");
                    check(close.TranslatePoint(new Point(0, close.ActualHeight), root).Y <= root.ActualHeight + 0.5, "advanced index actions fit minimum size");
                    await dialog.ApplyAsync();
                    check(dialog.WasApplied, "advanced index Apply executes actual database operation");
                    if (!dialog.WasApplied) throw new Exception(((TextBlock)dialog.FindName("Feedback")).Text);
                }
                catch (Exception ex) { error = ex; dialog.Close(); }
            };
            dialog.ShowDialog(); if (error is not null) throw error;
            var index = (await new IndexInspectionService(connection).InspectAsync("public", table)).Single();
            check(index.Index.Definition.Contains("NULLS NOT DISTINCT"), "WPF NULL equality persists in PostgreSQL");
            check(index.Index.Definition.Contains("fillfactor='75'"), "WPF fillfactor persists in PostgreSQL");
            check(index.Index.Definition.Contains("DESC NULLS LAST"), "WPF ordering persists in PostgreSQL");
            check(index.Index.Definition.Contains("INCLUDE"), "WPF INCLUDE is persisted in PostgreSQL");
            var hashDialog = new RelationEditDialog(connection, "public", table) { ShowInTaskbar = false };
            error = null;
            hashDialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((TextBox)hashDialog.FindName("ObjectName")).Text = table + "_hash";
                    ((TextBox)hashDialog.FindName("Columns")).Text = "id";
                    ((ComboBox)hashDialog.FindName("Method")).SelectedItem = IndexMethod.Hash;
                    check(((TextBox)hashDialog.FindName("Preview")).Text.Contains("USING hash"), "hash method updates SQL preview");
                    await hashDialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(hashDialog, "advanced-index-hash");
                    hashDialog.Width = hashDialog.MinWidth; hashDialog.Height = hashDialog.MinHeight;
                    await hashDialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    TableEditChecks.Capture(hashDialog, "advanced-index-hash-min");
                    await hashDialog.ApplyAsync();
                    check(hashDialog.WasApplied, "hash Apply succeeds from WPF");
                    if (!hashDialog.WasApplied) throw new Exception(((TextBlock)hashDialog.FindName("Feedback")).Text);
                }
                catch (Exception ex) { error = ex; hashDialog.Close(); }
            };
            hashDialog.ShowDialog(); if (error is not null) throw error;
            check((await new IndexInspectionService(connection).InspectAsync("public", table)).Any(i => i.Index.Method == "hash"), "WPF hash method persists in PostgreSQL");
        }
        finally { await session.ExecuteAsync($"DROP TABLE IF EXISTS {target};"); }
    }
}
