using System.IO;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PGMS.Desktop.ViewModels;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ObjectPropertyChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var catalog = new CatalogService(connection);
        var entry = (await catalog.ChildrenAsync(new(CatalogKind.Databases, "Databases"))).Single(e => e.Name == "postgres");
        var dialog = new ObjectPropertiesDialog(connection, entry) { ShowInTaskbar = false };
        dialog.Show();
        try
        {
            check(dialog.Snapshot is null && ((TextBox)dialog.FindName("Summary")).IsReadOnly, "Object Properties opens read-only without pretending to have inspected");
            await dialog.InspectAsync();
            check(dialog.Snapshot?.Target.Oid == entry.Oid && ((TextBox)dialog.FindName("Summary")).Text.Contains("Database: postgres"), "Database Properties displays actual inspected identity");
            check(((Button)dialog.FindName("InspectButton")).IsEnabled && !dialog.IsBusy, "Properties inspection restores retry action");
            await CaptureSizesAsync(dialog, "database", check);
            check(entry.CanInspectObjectProperties && !new CatalogEntry(CatalogKind.Tables, "Tables").CanInspectObjectProperties, "Object Properties is available only for supported Explorer kinds");
        }
        finally { dialog.Close(); }
        string schema = "schema_ui_" + Guid.NewGuid().ToString("N");
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE SCHEMA {schema};");
        try
        {
            var schemaEntry = (await catalog.ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"))).Single(e => e.Name == schema);
            var schemaDialog = new ObjectPropertiesDialog(connection, schemaEntry) { ShowInTaskbar = false };
            schemaDialog.Show();
            try
            {
                await schemaDialog.InspectAsync();
                var snapshot = schemaDialog.Snapshot;
                check(snapshot?.Target.Kind == CatalogKind.Schema && ((TextBox)schemaDialog.FindName("Summary")).Text.Contains("Current role USAGE: True"), "Schema Properties shows actual current-role capabilities");
                await CheckInteractionAsync(schemaDialog, connection, session, schema, check);
                snapshot = schemaDialog.Snapshot;
                await session.ExecuteAsync($"DROP SCHEMA {schema};");
                await schemaDialog.InspectAsync();
                check(ReferenceEquals(snapshot, schemaDialog.Snapshot) && ((TextBlock)schemaDialog.FindName("Feedback")).Text.Contains("may be outdated"), "Failed properties refresh preserves snapshot with explicit warning");
            }
            finally { schemaDialog.Close(); }
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema};"); }
        string viewName = "view_ui_" + Guid.NewGuid().ToString("N");
        try
        {
            await session.ExecuteAsync($"CREATE VIEW public.{viewName} AS SELECT 42 AS answer;");
            var viewEntry = (await catalog.ChildrenAsync(new(CatalogKind.Views, "Views", "postgres", "public"))).Single(e => e.Name == viewName);
            var viewDialog = new ObjectPropertiesDialog(connection, viewEntry) { ShowInTaskbar = false };
            viewDialog.Show();
            try
            {
                await viewDialog.InspectAsync();
                check(((TabItem)viewDialog.FindName("DefinitionTab")).Visibility == Visibility.Visible && ((TextBox)viewDialog.FindName("Definition")).Text.Contains("42"), "View Properties exposes read-only deparsed Definition tab");
                ((TabControl)viewDialog.FindName("Details")).SelectedIndex = 1;
                await CaptureSizesAsync(viewDialog, "view-definition", check);
                check(viewEntry.CanInspectObjectProperties && !new CatalogEntry(CatalogKind.Relation, "table") { RelationType = "r" }.CanInspectObjectProperties, "View properties route does not replace table properties");
            }
            finally { viewDialog.Close(); }
        }
        finally { await session.ExecuteAsync($"DROP VIEW IF EXISTS public.{viewName};"); }
        string routineName = "routine_ui_" + Guid.NewGuid().ToString("N");
        try
        {
            await session.ExecuteAsync($"CREATE FUNCTION public.{routineName}(integer) RETURNS integer LANGUAGE sql AS $$ SELECT $1 $$;");
            var routine = (await catalog.ChildrenAsync(new(CatalogKind.Functions, "Functions", "postgres", "public"))).Single(e => e.Name == routineName + "(integer)");
            var routineDialog = new ObjectPropertiesDialog(connection, routine) { ShowInTaskbar = false };
            routineDialog.Show();
            try
            {
                await routineDialog.InspectAsync();
                check(((TextBox)routineDialog.FindName("Summary")).Text.Contains("Identity arguments: integer"), "Function Properties displays selected overload identity");
                check(((TextBox)routineDialog.FindName("Definition")).Text.Contains("CREATE OR REPLACE FUNCTION"), "Function Properties displays server reconstruction without executing it");
                ((TabControl)routineDialog.FindName("Details")).SelectedIndex = 1;
                await CaptureSizesAsync(routineDialog, "function-definition", check);
            }
            finally { routineDialog.Close(); }
        }
        finally { await session.ExecuteAsync($"DROP FUNCTION IF EXISTS public.{routineName}(integer);"); }
        await CheckAdditionalKindsAsync(connection, check);
        await CheckExplorerRouteAsync(connection, entry, check);
    }

    private static async Task CheckInteractionAsync(ObjectPropertiesDialog dialog, string connection, QuerySession session, string schema, Action<bool, string> check)
    {
        var inspect = (Button)dialog.FindName("InspectButton");
        var cancel = (Button)dialog.FindName("CancelInspectionButton");
        var summary = (TextBox)dialog.FindName("Summary");
        var feedback = (TextBlock)dialog.FindName("Feedback");
        check(new ButtonAutomationPeer(inspect).GetName() == "Inspect object properties" && new ButtonAutomationPeer(cancel).GetName() == "Cancel property inspection", "Inspection controls expose stable automation names");
        inspect.Focus();
        var before = dialog.Snapshot;
        await using (var blocker = new Npgsql.NpgsqlConnection(connection))
        {
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using var command = new Npgsql.NpgsqlCommand("LOCK TABLE pg_catalog.pg_namespace IN ACCESS EXCLUSIVE MODE", blocker, transaction);
            await command.ExecuteNonQueryAsync();
            var pending = dialog.InspectAsync(); await Task.Delay(200);
            check(dialog.IsBusy && cancel.IsKeyboardFocused && !inspect.IsEnabled && ReferenceEquals(before, dialog.Snapshot), "Pending properties inspection focuses cancel and retains existing snapshot");
            await dialog.InspectAsync();
            check(dialog.IsBusy, "Duplicate inspection does not replace active operation");
            dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await pending.WaitAsync(TimeSpan.FromSeconds(20));
            check(!dialog.IsBusy && dialog.IsVisible && feedback.Text.Contains("canceled") && feedback.Text.Contains("outdated"), "Escape cancels inspection with explicit retained-snapshot warning");
            check(inspect.IsKeyboardFocused && ReferenceEquals(before, dialog.Snapshot), "Cancellation restores initiating focus and snapshot");
            await CaptureSizesAsync(dialog, "schema-canceled", check);
            pending = dialog.InspectAsync(); await Task.Delay(200); summary.Focus();
            dialog.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(20));
            check(summary.IsKeyboardFocused, "Completion preserves user-selected summary focus");
            pending = dialog.InspectAsync(); await Task.Delay(200); dialog.Close();
            await pending.WaitAsync(TimeSpan.FromSeconds(20));
            check(dialog.IsVisible && !dialog.IsBusy, "Closing busy properties requests cancellation and keeps dialog open for recovery");
            await transaction.RollbackAsync();
        }
        await session.ExecuteAsync($"COMMENT ON SCHEMA {schema} IS 'refreshed with F5';");
        dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.F5) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        for (int i = 0; i < 100 && !summary.Text.Contains("refreshed with F5"); i++) await Task.Delay(20);
        check(summary.Text.Contains("refreshed with F5") && !dialog.IsBusy, "F5 retries inspection and updates catalog snapshot");
    }
    private static async Task CheckAdditionalKindsAsync(string connection, Action<bool, string> check)
    {
        string schema = "properties_ui_" + Guid.NewGuid().ToString("N");
        await using var session = new QuerySession(connection); await session.OpenAsync();
        try
        {
            await session.ExecuteAsync($"CREATE SCHEMA {schema}; CREATE MATERIALIZED VIEW {schema}.cached AS SELECT 1 AS answer WITH NO DATA; CREATE PROCEDURE {schema}.action() LANGUAGE plpgsql AS $$ BEGIN NULL; END $$; CREATE VIEW {schema}.large AS SELECT '{new string('x', 280000)}'::text AS large;");
            var catalog = new CatalogService(connection);
            foreach (var (kind, name) in new[] { (CatalogKind.MaterializedViews, "materialized"), (CatalogKind.Procedures, "procedure"), (CatalogKind.Views, "truncated") })
            {
                var entry = (await catalog.ChildrenAsync(new(kind, "Objects", "postgres", schema))).Single();
                var dialog = new ObjectPropertiesDialog(connection, entry) { ShowInTaskbar = false }; dialog.Show();
                try
                {
                    await dialog.InspectAsync();
                    check(dialog.Snapshot?.Target.Oid == entry.Oid, "Properties supports actual " + name + " selection");
                    if (kind != CatalogKind.MaterializedViews) ((TabControl)dialog.FindName("Details")).SelectedIndex = 1;
                    if (kind == CatalogKind.Views)
                        check(dialog.Snapshot?.DefinitionTruncated == true && ((TextBlock)dialog.FindName("DefinitionNotice")).Text.Contains("incomplete SQL"), "Truncated definition has explicit incomplete-SQL warning");
                    await CaptureSizesAsync(dialog, name, check);
                }
                finally { dialog.Close(); }
            }
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE;"); }
    }
    private static async Task CheckExplorerRouteAsync(string connection, CatalogEntry database, Action<bool, string> check)
    {
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var tree = (TreeView)window.FindName("ExplorerTree");
            tree.Items.Clear();
            var node = new CatalogNode(database, new CatalogService(connection)); tree.Items.Add(node); node.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var action = menu.Items.OfType<MenuItem>().Single(m => m.Name == "ExplorerObjectProperties");
            check(action.IsEnabled, "Explorer enables Object Properties on database selection");
            menu.IsOpen = false;
            var completion = new TaskCompletionSource();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += async (_, _) =>
            {
                if (Application.Current.Windows.OfType<ObjectPropertiesDialog>().FirstOrDefault() is not { } properties) return;
                timer.Stop();
                try
                {
                    await properties.InspectAsync();
                    check(properties.Snapshot?.Target == database, "Explorer command opens exact selected object identity");
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { properties.Close(); }
            };
            timer.Start();
            try { action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { timer.Stop(); }
        }
        finally { window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }
    private static async Task CaptureSizesAsync(ObjectPropertiesDialog dialog, string name, Action<bool, string> check)
    {
        foreach (bool small in new[] { false, true })
        {
            dialog.Width = small ? 600 : 800; dialog.Height = small ? 440 : 600;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
            var surface = (FrameworkElement)dialog.Content;
            var close = (Button)dialog.FindName("CloseButton");
            var point = close.TranslatePoint(new Point(0, 0), surface);
            check(point.Y >= 0 && point.Y + close.ActualHeight <= surface.ActualHeight + 1 && point.X + close.ActualWidth <= surface.ActualWidth + 1, name + " actions fit " + (small ? "minimum" : "normal") + " viewport");
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
                drawing.DrawRectangle(dialog.Background, null, bounds); drawing.DrawRectangle(new VisualBrush(surface), null, bounds);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts/review");
            using var file = File.Create("artifacts/review/m117-" + name + (small ? "-small" : "") + ".png"); encoder.Save(file);
        }
    }
}
