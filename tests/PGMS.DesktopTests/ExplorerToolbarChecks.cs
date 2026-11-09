using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;

// Object Explorer toolbar with the SSMS 22.11 buttons and Add or Remove Buttons (docs/MARCO-M1041.md).
internal static class ExplorerToolbarChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string file = Path.Combine(Path.GetTempPath(), "pgms-toolbar-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ExplorerToolbarPreferences(file);
            check(store.Load().Count == 0, "Without a saved choice every toolbar button shows");
            store.Save(["Stop", "Filter"]);
            check(store.Load().SetEquals(["Stop", "Filter"]), "Hidden toolbar buttons are saved");
            File.WriteAllText(file, "not json");
            check(store.Load().Count == 0, "An unreadable toolbar file falls back to all buttons");
        }
        finally { File.Delete(file); }

        check(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") is not null, "Toolbar checks run on a temporary state directory");
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            string[] names = ["ExplorerConnectMenuButton", "ExplorerConnectButton", "DisconnectExplorerButton", "CancelExplorerLoadButton", "ExplorerFilterButton", "ExplorerRefreshButton", "ExplorerToolbarOptionsButton"];
            var buttons = names.Select(n => (Button)window.FindName(n)).ToArray();
            check(buttons.Select(AutomationProperties.GetName).SequenceEqual(["Connect", "Connect Object Explorer", "Disconnect", "Stop", "Filter", "Refresh", "Object Explorer Toolbar Options"]), "Toolbar buttons follow the SSMS order and keep accessible names");
            check(buttons[0].ActualWidth > buttons[1].ActualWidth && buttons[1..6].All(b => b.ActualWidth <= 28), "Only Connect shows text; the other buttons are icons");
            check(buttons[6].TranslatePoint(new(), window).X > buttons[5].TranslatePoint(new(), window).X + 50, "The options button sits at the right end of the toolbar");
            ExplorerExperienceChecks.Capture(window, "m1041-explorer-toolbar");

            // Database Engine was the only server type, so Connect opens the connection dialog without a menu.
            ConnectionDialog? opened = null;
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                opened = Application.Current.Windows.OfType<ConnectionDialog>().FirstOrDefault();
                opened?.Close();
            });
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(opened is not null && opened.Owner == window && buttons[0].ContextMenu is null, "Connect opens the connection dialog directly, without a server type menu");

            var options = window.CreateExplorerToolbarMenu();
            var addRemove = (MenuItem)options.Items[0];
            var toggles = addRemove.Items.OfType<MenuItem>().Where(i => i.IsCheckable).ToList();
            check((string)addRemove.Header == "Add or Remove Buttons" && toggles.Select(AutomationProperties.GetName).SequenceEqual(["Connect", "Connect Object Explorer…", "Disconnect", "Stop", "Filter", "Refresh"]) && toggles.All(t => t.IsChecked), "Add or Remove Buttons lists the buttons, checked");
            check(toggles[^1].InputGestureText == "F5", "Refresh shows its shortcut in the list");

            var filter = toggles.Single(t => t.Name == "ExplorerToolbarToggleFilter");
            filter.IsChecked = false; filter.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(buttons[4].Visibility == Visibility.Collapsed, "Unchecking a button hides it");
            var reopened = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            check(((Button)reopened.FindName("ExplorerFilterButton")).Visibility == Visibility.Collapsed, "The choice survives a new window");
            reopened.Close();

            var reset = addRemove.Items.OfType<MenuItem>().Single(i => i.Name == "ExplorerToolbarReset");
            reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(buttons[4].Visibility == Visibility.Visible && filter.IsChecked, "Reset Toolbar brings every button back");
            check(new ExplorerToolbarPreferences(ExplorerToolbarPreferences.DefaultPath).Load().Count == 0, "Reset Toolbar clears the saved choice");
        }
        finally { window.Close(); }
    }
}
