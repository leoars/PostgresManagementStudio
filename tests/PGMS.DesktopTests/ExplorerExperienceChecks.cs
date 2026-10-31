using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

internal static class ExplorerExperienceChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var service = new CatalogService(connection);
        string schema = "explorer_" + Guid.NewGuid().ToString("N");
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.alpha(id int); CREATE TABLE {schema}.beta(id int); CREATE TABLE {schema}.\"literal_%\"(id int);");
        try
        {
            await session.ExecuteAsync($"CREATE TYPE {schema}.\"ação_%\" AS ENUM('a'); CREATE DOMAIN {schema}.positive AS int CHECK(VALUE>0)");
            var enums = new CatalogNode(new(CatalogKind.Enums, "Enums", "postgres", schema), service);
            await enums.LoadAsync();
            var enumBefore = enums.Children.Single();
            check(enumBefore.Entry.Kind == CatalogKind.DataType && enumBefore.Children.Count == 0 && enumBefore.LoadTarget == enums, "Enum leaf refresh targets its folder without fake descendants");
            enums.SetFilter("_%"); await enums.LoadAsync(refresh: true);
            check(enums.CanFilter && ReferenceEquals(enumBefore, enums.Children.Single()), "Enum folder supports literal filtering and preserves unchanged identity");
            await session.ExecuteAsync($"DROP TYPE {schema}.\"ação_%\"; CREATE TYPE {schema}.\"ação_%\" AS ENUM('b')");
            await enums.LoadAsync(refresh: true);
            check(enums.Filter == "_%" && !ReferenceEquals(enumBefore, enums.Children.Single()) && enumBefore.Entry.Oid != enums.Children.Single().Entry.Oid, "Recreated enum replaces stale Explorer node while preserving filter");
            var domains = new CatalogNode(new(CatalogKind.Domains, "Domains", "postgres", schema), service);
            await domains.LoadAsync(); domains.SetFilter("POS");
            check(domains.Children.Single().Name == "positive", "Domains have their own case-insensitive filter");
            var node = new CatalogNode(new(CatalogKind.Tables, "Tables", "postgres", schema), service);
            await node.LoadAsync();
            check(node.Children.Count == 3, "Explorer loads direct children");
            node.SetFilter("ALP");
            check(node.Children.Single().Name == "alpha" && node.Name.Contains("filtered"), "Explorer literal filter ignores case and marks folder");
            node.SetFilter("_%");
            check(node.Children.Single().Name == "literal_%", "Explorer treats SQL wildcard characters literally");
            node.SetFilter("absent");
            check(node.Children.Single().Name == "(no matching items)", "Explorer distinguishes no matches from empty folder");
            node.SetFilter("");
            check(node.Children.Count == 3 && node.Name == "Tables", "Clearing filter restores all children without querying");
            node.SetFilter("beta"); await node.LoadAsync(refresh: true);
            check(node.Children.Single().Name == "beta", "Refresh keeps folder filter");
            node.SetFilter("");
            var alpha = node.Children.Single(n => n.Name == "alpha");
            await alpha.LoadAsync();
            var columns = alpha.Children.First(); await columns.LoadAsync();
            await session.ExecuteAsync($"CREATE TABLE {schema}.aardvark(id int); DROP TABLE {schema}.beta;");
            await node.LoadAsync(refresh: true);
            check(ReferenceEquals(alpha, node.Children.Single(n => n.Name == "alpha")) && ReferenceEquals(columns, alpha.Children.First()), "Refresh preserves unchanged nodes and loaded descendants");
            check(node.Children.First().Name == "aardvark" && node.Children.All(n => n.Name != "beta"), "Refresh inserts in server order and removes dropped objects");
            await session.ExecuteAsync($"DROP TABLE {schema}.alpha; CREATE TABLE {schema}.alpha(id int);");
            await node.LoadAsync(refresh: true);
            check(!ReferenceEquals(alpha, node.Children.Single(n => n.Name == "alpha")), "Recreated object with same name gets new node identity");
            var retained = node.Children.ToArray();
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); await node.LoadAsync(canceled.Token, refresh: true);
                check(node.WasCanceled && !node.IsBusy && node.Children.SequenceEqual(retained), "Canceled refresh preserves snapshot and releases busy state");
            }
            await node.LoadAsync();
            check(!node.WasCanceled, "Expansion retries canceled refresh");
            await using (var blocker = new Npgsql.NpgsqlConnection(connection))
            {
                await blocker.OpenAsync();
                await using var transaction = await blocker.BeginTransactionAsync();
                await using var command = new Npgsql.NpgsqlCommand("LOCK TABLE pg_catalog.pg_namespace IN ACCESS EXCLUSIVE MODE", blocker, transaction);
                await command.ExecuteNonQueryAsync();
                var pending = node.LoadAsync(refresh: true);
                await Task.Delay(200);
                check(node.IsBusy, "Blocked catalog read exposes busy state");
                node.CancelLoad();
                await pending.WaitAsync(TimeSpan.FromSeconds(20));
                check(node.WasCanceled && !node.IsBusy && node.Children.SequenceEqual(retained), "Cancel Load interrupts pending catalog read without discarding snapshot");
                await transaction.RollbackAsync();
            }
            await node.LoadAsync();
            check(!node.WasCanceled && node.Error is null, "Catalog recovers after in-flight cancellation");
            await CheckSurfaceAsync(node, connection, session, schema, check);
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA {schema} CASCADE;"); }
        string database = "explorer_db_" + Guid.NewGuid().ToString("N");
        try
        {
            var failed = new CatalogNode(new(CatalogKind.Schemas, "Schemas", database), service);
            await failed.LoadAsync();
            check(failed.Error?.StartsWith("3D000:") == true && failed.Name.Contains("load failed"), "Initial catalog failure exposes SQLSTATE and retry state");
            await session.ExecuteAsync($"CREATE DATABASE {database};");
            await failed.LoadAsync();
            check(failed.Error is null && failed.Children.Any(n => n.Name == "public"), "Expansion retries failed initial load");
            var previous = failed.Children.Single(n => n.Name == "public");
            await session.ExecuteAsync($"DROP DATABASE {database};");
            await failed.LoadAsync(refresh: true);
            check(failed.Error is not null && failed.Children.Contains(previous) && failed.FilterSummary.Contains("outdated"), "Failed refresh preserves snapshot and explicitly marks it outdated");
            await session.ExecuteAsync($"CREATE DATABASE {database};");
            await failed.LoadAsync();
            check(failed.Error is null && !failed.Name.Contains("load failed"), "Retry after failed refresh clears stale error on success");
        }
        finally { await session.ExecuteAsync($"DROP DATABASE IF EXISTS {database};"); }
    }

    private static async Task CheckSurfaceAsync(CatalogNode node, string connection, QuerySession session, string schema, Action<bool, string> check)
    {
        var window = new MainWindow { Width = 1100, Height = 700, ShowInTaskbar = false };
        window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var database = (TextBox)window.FindName("DatabaseName");
            var dock = (AvalonDock.DockingManager)window.FindName("DockManager");
            var execute = (Button)window.FindName("ExecuteButton");
            ApplicationCommands.New.Execute(null, window);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var query = (QueryView)dock.ActiveContent;
            check(database.Text == "Not connected" && !execute.IsEnabled,
                "Disconnected query reports its own session state even with connected Explorer");
            await query.ConnectAsync(connection);
            check(database.Text == query.Database && execute.IsEnabled,
                "Connected active query exposes its database and enables execution");
            var explorer = (FrameworkElement)window.FindName("ExplorerContent");
            dock.ActiveContent = explorer;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(database.Text == "No active query" && database.ToolTip.ToString()!.Contains("separate connection") && !execute.IsEnabled,
                "Explorer focus distinguishes inactive query from disconnected session without enabling Execute");
            check(query.IsConnected, "Explorer activation preserves the open query connection");
            Capture(window, "m961-explorer-session-status");
            dock.ActiveContent = query;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(database.Text == query.Database && execute.IsEnabled && database.ToolTip.ToString() == "Database of the active query session",
                "Returning to query restores its database, execution and session tooltip");
            query.Editor.Text = "SELECT 961 AS focus_roundtrip";
            await query.ExecuteAsync();
            check(query.IsConnected && !query.IsBusy && execute.IsEnabled && ((TextBlock)query.FindName("QueryStatus")).Text.StartsWith("Query completed"),
                "Query executes after Explorer focus roundtrip using the retained session");
            query.IsDirty = false; // Synthetic SQL must not open a save prompt during fixture cleanup.
            dock.ActiveContent = explorer;
            var tree = (TreeView)window.FindName("ExplorerTree");
            tree.Items.Clear(); tree.Items.Add(node);
            node.IsExpanded = true; node.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var filter = (TextBox)window.FindName("ExplorerFilter");
            var apply = (Button)window.FindName("ApplyExplorerFilterButton");
            var clear = (Button)window.FindName("ClearExplorerFilterButton");
            check(((FrameworkElement)window.FindName("ExplorerFilterPanel")).IsEnabled, "Selecting object folder enables filter controls");
            filter.Text = "alpha";
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(node.Filter == "alpha" && node.Children.Single().Name == "alpha", "Apply Filter button filters selected folder");
            check(((TextBlock)window.FindName("ExplorerFilterSummary")).Text.Contains("1 of 3"), "Filter count binds to current snapshot");
            check(new TextBoxAutomationPeer(filter).GetName() == "Filter selected folder by name" && new ButtonAutomationPeer(apply).GetName() == "Apply folder name filter", "Filter controls expose automation names");
            Capture(window, "m112-explorer-filter");
            window.Width = 900; window.Height = 600;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, "m112-explorer-small");
            check(filter.ActualWidth > 120 && clear.ActualHeight > 15, "Filter remains usable in minimum shell viewport");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(node.Filter == "" && node.Children.Count == 3, "Clear Filter button restores folder contents");
            var alpha = node.Children.Single(n => n.Name == "alpha");
            await alpha.LoadAsync(); alpha.IsExpanded = true; alpha.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await node.LoadAsync(refresh: true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(ReferenceEquals(tree.SelectedItem, alpha) && alpha.IsExpanded, "Real tree selection and expansion survive parent refresh");
            var folderItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(node);
            var tableItem = (TreeViewItem)folderItem.ItemContainerGenerator.ContainerFromItem(alpha);
            var tableHeader = FindHeader(tableItem, alpha.Name) ?? throw new Exception("Nested table header not realized");
            node.IsSelected = true;
            tableHeader.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            check(ReferenceEquals(tree.SelectedItem, alpha), "Right-click on a nested table header selects the table instead of its root folder");
            var context = tree.ContextMenu;
            context.PlacementTarget = tree; context.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            check(context.Items.OfType<MenuItem>().Single(m => m.Name == "ExplorerTableProperties").IsEnabled,
                "Nested right-click enables properties for the clicked table");
            context.IsOpen = false;
            node.IsSelected = true;
            filter.Focus();
            await session.ExecuteAsync($"CREATE TABLE {schema}.gamma(id int);");
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.F5) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            filter.RaiseEvent(key);
            for (int i = 0; i < 100 && !node.Children.Any(n => n.Name == "gamma"); i++) await Task.Delay(20);
            check(key.Handled && tree.Items.Contains(node) && node.Children.Any(n => n.Name == "gamma"), "F5 from filter refreshes selected folder without recreating root");
            filter.Text = "gamma";
            filter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(node.Children.Single().Name == "gamma", "Enter applies name filter");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await alpha.LoadAsync();
            var columns = alpha.Children.First(); await columns.LoadAsync();
            columns.IsExpanded = true;
            var column = columns.Children.First(); column.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await session.ExecuteAsync($"ALTER TABLE {schema}.alpha ADD COLUMN extra text;");
            await window.RefreshSelectedExplorerAsync();
            check(columns.Children.Count == 2, "Refresh on leaf reloads its containing folder");
            await using (var leafBlocker = new Npgsql.NpgsqlConnection(connection))
            {
                await leafBlocker.OpenAsync();
                await using var leafTransaction = await leafBlocker.BeginTransactionAsync();
                await using var leafCommand = new Npgsql.NpgsqlCommand("LOCK TABLE pg_catalog.pg_attribute IN ACCESS EXCLUSIVE MODE", leafBlocker, leafTransaction);
                await leafCommand.ExecuteNonQueryAsync();
                var leafPending = window.RefreshSelectedExplorerAsync();
                await Task.Delay(200);
                var leafCancel = (Button)window.FindName("CancelExplorerLoadButton");
                check(ReferenceEquals(tree.SelectedItem, column) && columns.IsBusy && leafCancel.IsEnabled, "Leaf selection enables cancellation of its busy parent folder");
                leafCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await leafPending.WaitAsync(TimeSpan.FromSeconds(20));
                check(columns.WasCanceled && !columns.IsBusy, "Cancel Load from a leaf cancels the same target as Refresh");
                leafPending = window.RefreshSelectedExplorerAsync();
                await Task.Delay(200);
                tree.Focus();
                tree.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await leafPending.WaitAsync(TimeSpan.FromSeconds(20));
                check(columns.WasCanceled && !columns.IsBusy, "Escape from a leaf cancels its parent folder load");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Capture(window, "m112-explorer-leaf-canceled");
                await leafTransaction.RollbackAsync();
            }
            node.IsSelected = true;
            await using var blocker = new Npgsql.NpgsqlConnection(connection); await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using var command = new Npgsql.NpgsqlCommand("LOCK TABLE pg_catalog.pg_namespace IN ACCESS EXCLUSIVE MODE", blocker, transaction);
            await command.ExecuteNonQueryAsync();
            var pending = node.LoadAsync(refresh: true);
            await Task.Delay(200);
            var cancel = (Button)window.FindName("CancelExplorerLoadButton");
            check(cancel.IsEnabled, "Cancel Load enables for selected busy folder");
            filter.Focus();
            filter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await pending.WaitAsync(TimeSpan.FromSeconds(20));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(node.WasCanceled && !cancel.IsEnabled, "Escape cancels selected folder and restores button state");
            Capture(window, "m112-explorer-canceled");
            await transaction.RollbackAsync();
            await node.LoadAsync();
            // Render all catalog glyph families together, using the production item template.
            CatalogEntry[] iconEntries = [
                new(CatalogKind.Server, "PostgreSQL server"), new(CatalogKind.Databases, "Databases"),
                new(CatalogKind.Database, "comparison"), new(CatalogKind.Schema, "acceptance"),
                new(CatalogKind.Relation, "items") { RelationType = "r" },
                new(CatalogKind.Relation, "partitioned_items") { RelationType = "p" },
                new(CatalogKind.Relation, "items_view") { RelationType = "v" },
                new(CatalogKind.Relation, "materialized_items") { RelationType = "m" },
                new(CatalogKind.Relation, "item_sequence") { RelationType = "S" },
                new(CatalogKind.Routine, "calculate_total(integer)"), new(CatalogKind.Column, "id", Oid: 1),
                new(CatalogKind.Index, "items_pkey"), new(CatalogKind.Role, "reader (login)"),
                new(CatalogKind.DataType, "item_status") { DataTypeKind = "e" },
                new(CatalogKind.DataType, "positive_amount") { DataTypeKind = "d" }
            ];
            tree.Items.Clear();
            foreach (var entry in iconEntries) tree.Items.Add(new CatalogNode(entry, new CatalogService(connection)));
            window.Width = 1100; window.Height = 700;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, "m954-explorer-icons");
            window.Width = 900; window.Height = 600;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, "m954-explorer-icons-small");
        }
        finally
        {
            window.Close(); await window.CloseCompletion;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(!window.IsVisible, "Explorer-only shell closes without reentrant Closing exception");
        }
    }
    private static TextBlock? FindHeader(DependencyObject parent, string name)
    {
        if (parent is TextBlock text && text.Text == name) return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindHeader(VisualTreeHelper.GetChild(parent, i), name);
            if (found is not null) return found;
        }
        return null;
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
            drawing.DrawRectangle(window.Background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(surface), null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/review");
        using var file = File.Create("artifacts/review/" + name + ".png"); encoder.Save(file);
    }
}
