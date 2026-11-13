using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

internal static class ExplorerContextChecks
{
    public static async Task RunAsync(MainWindow window, string connection, Action<bool, string> check)
    {
        string table = "pgms_context_" + Guid.NewGuid().ToString("N");
        string target = "public." + SqlIdentifier.Quote(table);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE TABLE {target}(id int); CREATE VIEW public.{SqlIdentifier.Quote(table + "_view")} AS SELECT * FROM {target}; CREATE TABLE public.{SqlIdentifier.Quote(table + "_part")}(id int) PARTITION BY RANGE(id);");
        var tree = (TreeView)window.FindName("ExplorerTree");
        var menu = tree.ContextMenu;
        try
        {
            await window.ConnectExplorerAsync(connection);
            var catalog = new CatalogService(connection);
            var tables = await catalog.ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", "public"));
            var views = await catalog.ChildrenAsync(new(CatalogKind.Views, "Views", "postgres", "public"));
            CatalogEntry[] entries = [tables.Single(t => t.Name == table), tables.Single(t => t.Name == table + "_part"), views.Single(t => t.Name == table + "_view"), new(CatalogKind.Server, "Synthetic server")];
            tree.Items.Clear(); foreach (var entry in entries) tree.Items.Add(new CatalogNode(entry, catalog));
            window.UpdateLayout(); tree.UpdateLayout();
            // Menus are built per node kind when opened (M1036): table actions exist only where they apply.
            MenuItem? properties = null, operations = null;
            for (int index = 0; index < entries.Length; index++)
            {
                ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(index)).IsSelected = true;
                menu.PlacementTarget = tree; menu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var items = menu.Items.OfType<MenuItem>().ToList();
                var shownProperties = items.SingleOrDefault(m => m.Name == "ExplorerTableProperties");
                var shownOperations = items.SingleOrDefault(m => m.Name == "ExplorerTableOperations");
                check((shownProperties is { IsEnabled: true }) == (index < 2) && (shownOperations is { IsEnabled: true }) == (index == 0), "Explorer context lists table actions only for supported selection " + index + " (" + string.Join(",", items.Select(m => m.Name + (m.IsEnabled ? "" : "[off]"))) + ")");
                if (index == 0) { CaptureMenu(menu); properties = shownProperties; operations = shownOperations; }
                menu.IsOpen = false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
            if (properties is null || operations is null) throw new InvalidOperationException("Table menu items were not built.");
            await OpenAsync<TablePropertiesDialog>(properties, async dialog =>
            {
                for (int i = 0; i < 200 && dialog.Snapshot is null; i++) await Task.Delay(50);
                check(dialog.Snapshot?.Oid == entries[0].Oid, "Table Properties from Object Explorer reads the table when it opens, without Inspect");
                await dialog.InspectAsync();
                check(dialog.Snapshot?.Oid == entries[0].Oid && ((TextBlock)dialog.FindName("Target")).Text.Contains("Database: postgres", StringComparison.Ordinal), "Explorer Properties opens selected table in its database");
                TableEditChecks.Capture(dialog, "m42-context-properties");
            });
            await OpenAsync<TableEditDialog>(operations, dialog =>
            {
                check(dialog.InspectedTable?.Oid == entries[0].Oid && ((TextBox)dialog.FindName("TableName")).Text == table, "Explorer Operations passes fresh inspected identity");
                return Task.CompletedTask;
            });

            // Tools and Permissions items follow the selection: the table, or the table above a selected child, as the table menu does.
            MenuItem Main(string header) => FindMenuItems(window).First(m => m.Header as string == header);
            await OpenAsync<TableEditDialog>(Main("Table Operations…"), dialog =>
            {
                check(dialog.InspectedTable?.Oid == entries[0].Oid, "Tools ▸ Table Operations… opens the selected table inspected, as the table menu does");
                return Task.CompletedTask;
            });
            await OpenAsync<PrivilegeDialog>(Main("Object Permissions…"), dialog =>
            {
                check(((ComboBox)dialog.FindName("ObjectType")).SelectedItem is PrivilegeTarget.Table && ((TextBox)dialog.FindName("SchemaName")).Text == "public" && ((TextBox)dialog.FindName("ObjectName")).Text == table,
                    "Object Permissions… starts on the selected table");
                return Task.CompletedTask;
            });
            await OpenAsync<DefaultPrivilegeDialog>(Main("Default Permissions…"), dialog =>
            {
                check(((CheckBox)dialog.FindName("GlobalScope")).IsChecked == false && ((TextBox)dialog.FindName("SchemaName")).Text == "public", "Default Permissions… starts on the schema of the selection");
                return Task.CompletedTask;
            });
            var tableNode = (CatalogNode)tree.Items[0];
            await tableNode.LoadAsync();
            var tableItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            tableItem.IsExpanded = true; tree.UpdateLayout(); tableItem.UpdateLayout();
            var child = tableNode.Children.First();
            ((TreeViewItem)tableItem.ItemContainerGenerator.ContainerFromItem(child)).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(tree.SelectedItem == child, "A folder under the table is selected");
            await OpenAsync<RelationEditDialog>(Main("Index Operations…"), async dialog =>
            {
                check(((TextBox)dialog.FindName("TableName")).Text == table, "Index Operations… with a folder of the table selected names the table above it");
                // It inspects the table as it opens, and refuses to close meanwhile.
                for (int i = 0; i < 200 && !((Button)dialog.FindName("CloseButton")).IsEnabled; i++) await Task.Delay(50);
                check(((Button)dialog.FindName("CloseButton")).IsEnabled, "Index Operations… inspected the table above the folder");
            });
            await OpenAsync<TableEditDialog>(Main("Table Operations…"), dialog =>
            {
                check(dialog.InspectedTable?.Oid == entries[0].Oid, "Table Operations… with a folder of the table selected opens that table inspected");
                return Task.CompletedTask;
            });
            await OpenAsync<PrivilegeDialog>(Main("Object Permissions…"), dialog =>
            {
                check(((ComboBox)dialog.FindName("ObjectType")).SelectedItem is PrivilegeTarget.Table && ((TextBox)dialog.FindName("ObjectName")).Text == table, "Object Permissions… with a folder of the table selected starts on that table");
                return Task.CompletedTask;
            });
            ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(2)).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await OpenAsync<PrivilegeDialog>(Main("Object Permissions…"), dialog =>
            {
                check(((ComboBox)dialog.FindName("ObjectType")).SelectedItem is PrivilegeTarget.Table && ((TextBox)dialog.FindName("ObjectName")).Text == table + "_view", "Object Permissions… on a view starts on it, as a table-like object");
                return Task.CompletedTask;
            });
            // Restore Database… asks for the database when the selection has none (the server, or the Databases folder), never using the login database.
            bool? asks = null;
            window.BackupShown = dialog => { asks = ((StackPanel)dialog.FindName("DatabaseFields")).Visibility == Visibility.Visible; dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(dialog.Close); };
            try
            {
                ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(3)).IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Main("Restore Database…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                check(asks == true, "Restore Database… with the server selected asks for the database");
                asks = null;
                ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Main("Restore Database…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                check(asks == false, "Restore Database… with an object of a database selected restores into that database, without asking");
            }
            finally { window.BackupShown = null; }
        }
        finally
        {
            menu.IsOpen = false;
            await session.ExecuteAsync($"DROP TABLE IF EXISTS {target} CASCADE; DROP TABLE IF EXISTS public.{SqlIdentifier.Quote(table + "_part")};");
        }
    }
    private static async Task OpenAsync<T>(MenuItem action, Func<T, Task> inspect) where T : Window
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += async (_, _) =>
        {
            if (Application.Current.Windows.OfType<T>().FirstOrDefault() is not { } dialog) return;
            timer.Stop();
            try { await inspect(dialog); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { dialog.Close(); }
        };
        timer.Start();
        try { action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { timer.Stop(); }
    }
    private static IEnumerable<MenuItem> FindMenuItems(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is MenuItem item) yield return item;
            foreach (var nested in FindMenuItems(child)) yield return nested;
        }
    }
    private static void CaptureMenu(ContextMenu menu)
    {
        menu.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, menu.ActualWidth, menu.ActualHeight);
            drawing.DrawRectangle(SystemColors.MenuBrush, null, bounds);
            drawing.DrawRectangle(new VisualBrush(menu), null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create("artifacts/review/m42-explorer-menu.png"); encoder.Save(file);
    }
}
