using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;

// Tools ▸ Options ▸ Environment ▸ General: the main window reopens with the size and position it was closed with (pedido do usuário, 08/11/2026).
internal static class WindowPlacementChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        string state = Path.Combine(Path.GetTempPath(), "pgms-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        try
        {
            var store = new EnvironmentOptions(Path.Combine(state, "environment.json"));
            check(store.Load() is { RestoreWindowPlacement: true, Window: null }, "Restoring the window starts on, with nothing saved yet");

            var first = new MainWindow { ShowInTaskbar = false };
            check(first.WindowStartupLocation == WindowStartupLocation.CenterScreen && ((MenuItem)first.FindName("OptionsMenu")).IsEnabled, "Without a saved place the window opens centered, and Tools ▸ Options is available");
            first.Show(); await Idle();
            first.Left = 120; first.Top = 90; first.Width = 1000; first.Height = 700; await Idle();
            first.SaveWindowPlacement();
            check(store.Load().Window is { Left: 120, Top: 90, Width: 1000, Height: 700, Maximized: false }, "The size and position are saved");
            first.WindowState = WindowState.Maximized; await Idle();
            first.Close();
            for (int i = 0; i < 100 && first.IsVisible; i++) await Task.Delay(50);
            check(!first.IsVisible && store.Load().Window is { Left: 120, Top: 90, Width: 1000, Height: 700, Maximized: true }, "Closing a maximized window saves that it was maximized and keeps its normal size and position");

            var second = new MainWindow { ShowInTaskbar = false };
            check(second.WindowStartupLocation == WindowStartupLocation.Manual && second.Left == 120 && second.Top == 90 && second.Width == 1000 && second.Height == 700 && second.WindowState == WindowState.Maximized,
                "A new window opens with the saved size and position, maximized");

            var dialog = new OptionsDialog(second.EnvironmentSettings) { ShowInTaskbar = false };
            dialog.Show(); await Idle();
            var box = (CheckBox)dialog.FindName("RestoreWindowBox");
            check(box.IsChecked == true && ((TreeViewItem)dialog.FindName("GeneralPage")).IsSelected, "Options opens on Environment ▸ General with the option checked");
            box.IsChecked = false;
            dialog.Accept(dialog, new RoutedEventArgs());
            check(store.Load() is { RestoreWindowPlacement: false, Window: null } && !dialog.IsVisible, "Turning the option off saves it and forgets the saved place");

            var third = new MainWindow { ShowInTaskbar = false };
            check(third.WindowStartupLocation == WindowStartupLocation.CenterScreen && third.WindowState == WindowState.Normal, "With the option off the window opens centered as before");
            third.SaveWindowPlacement();
            check(store.Load().Window is null, "With the option off nothing is saved");

            store.Update(d => d with { RestoreWindowPlacement = true, Window = new(-60000, -60000, 1000, 700, false) });
            var fourth = new MainWindow { ShowInTaskbar = false };
            check(fourth.WindowStartupLocation == WindowStartupLocation.CenterScreen, "A place on a monitor that is no longer there is ignored");
            second.Close(); third.Close(); fourth.Close();

            // Object Explorer ▸ Commands: row counts of Select Top n Rows and Edit Top n Rows.
            check(store.Load() is { SelectTop: 1000, EditTop: 200 }, "Select Top and Edit Top default to 1000 and 200");
            var commands = new OptionsDialog(store) { ShowInTaskbar = false };
            commands.Show(); await Idle();
            var selectBox = (TextBox)commands.FindName("SelectTopBox"); var editBox = (TextBox)commands.FindName("EditTopBox");
            var commandsPage = (TreeViewItem)commands.FindName("CommandsPage");
            check(selectBox.Text == "1000" && editBox.Text == "200" && ((FrameworkElement)commands.FindName("CommandsPanel")).Visibility == Visibility.Collapsed,
                "Options shows the defaults on the Commands page, which starts hidden behind General");
            commandsPage.IsSelected = true; await Idle();
            check(((FrameworkElement)commands.FindName("CommandsPanel")).Visibility == Visibility.Visible && ((FrameworkElement)commands.FindName("GeneralPanel")).Visibility == Visibility.Collapsed,
                "Selecting Object Explorer ▸ Commands shows its page");
            foreach (string bad in new[] { "0", "-5", "abc", "", "2147483648", "12.5", "1.000", "1,000" })
            {
                selectBox.Text = bad;
                commands.Accept(commands, new RoutedEventArgs());
                check(commands.IsVisible && store.Load().SelectTop == 1000 && ((FrameworkElement)commands.FindName("CommandsError")).Visibility == Visibility.Visible,
                    $"Select Top \"{bad}\" is refused, the dialog stays open and nothing is saved");
            }
            selectBox.Text = " 5000 "; editBox.Text = "0";
            commands.Accept(commands, new RoutedEventArgs());
            check(commands.IsVisible && store.Load() is { SelectTop: 1000, EditTop: 200 } && System.Windows.Input.FocusManager.GetFocusedElement(commands) == editBox, "Edit Top 0 is refused and its box gets the focus");
            editBox.Text = "350";
            commands.Accept(commands, new RoutedEventArgs());
            check(!commands.IsVisible && store.Load() is { SelectTopRows: 5000, EditTopRows: 350, RestoreWindowPlacement: true }, "Valid values are saved without touching the other options");
            var reopened = new OptionsDialog(store) { ShowInTaskbar = false };
            check(((TextBox)reopened.FindName("SelectTopBox")).Text == "5000" && ((TextBox)reopened.FindName("EditTopBox")).Text == "350", "Reopening Options shows the saved values");
            reopened.Close();
            store.Update(d => d with { SelectTopRows = 0, EditTopRows = -1 });
            check(store.Load() is { SelectTop: 1000, EditTop: 200 }, "Invalid values in the file fall back to the defaults");
            File.WriteAllText(store.FilePath, "{\"Version\":1,\"RestoreWindowPlacement\":false}");
            check(store.Load() is { SelectTop: 1000, EditTop: 200, RestoreWindowPlacement: false }, "A file saved before these options gets the defaults");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
            try { Directory.Delete(state, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
