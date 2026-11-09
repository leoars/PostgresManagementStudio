using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;

internal static class ExplorerDisconnectChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            var tree = (TreeView)window.FindName("ExplorerTree");
            var button = (Button)window.FindName("DisconnectExplorerButton");
            check(!button.IsEnabled, "Disconnect is disabled before Object Explorer connects");
            await window.ConnectExplorerAsync(connection);
            var query = await window.OpenQueryTextAsync(connection, "SELECT 1");
            check(button.IsEnabled && tree.Items.Count == 1 && query.IsConnected, "Connecting Object Explorer enables Disconnect");

            var server = (CatalogNode)tree.Items[0]; server.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var items = menu.Items.OfType<MenuItem>().ToList();
            var disconnect = items.Single(m => m.Name == "ExplorerDisconnect");
            check(items[0].Header as string == "_Connect…" && items[1] == disconnect && disconnect.IsEnabled, "Server context menu starts with Connect… and Disconnect, as in SSMS");
            menu.IsOpen = false;

            disconnect.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var status = (TextBlock)window.FindName("Status");
            check(tree.Items.Count == 0 && !button.IsEnabled && !((MenuItem)window.FindName("NewDatabaseMenu")).IsEnabled && !((MenuItem)window.FindName("BackupMenu")).IsEnabled
                && status.Text.StartsWith("Object Explorer disconnected", StringComparison.Ordinal), "Disconnect clears the tree and disables Explorer-dependent commands");
            check(query.IsConnected, "Query windows stay connected after Object Explorer disconnects");

            await window.ConnectExplorerAsync(connection);
            check(tree.Items.Count == 1 && button.IsEnabled, "Object Explorer reconnects after Disconnect");
        }
        finally { window.Close(); }
    }
}
