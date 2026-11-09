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
