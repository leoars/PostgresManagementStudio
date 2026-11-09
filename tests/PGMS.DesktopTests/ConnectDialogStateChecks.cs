using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PGMS.Desktop;

// The Connect dialog fits its form without scrolling and remembers the chosen profile and the size the user gave it.
internal static class ConnectDialogStateChecks
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pgms-connect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "connections.json");
        try
        {
            var store = new ConnectionProfileStore(file);
            var first = new ConnectionProfile(Guid.NewGuid(), "First", "first.example", 5432, "postgres", "app", "Prefer");
            var second = new ConnectionProfile(Guid.NewGuid(), "Second", "second.example", 5433, "postgres", "app", "Prefer");
            store.Save(first); store.Save(second);
            async Task<ConnectionDialog> Open(Guid? profile = null, string? group = null)
            {
                var opened = new ConnectionDialog(profile, group, file) { ShowInTaskbar = false, Opacity = 0 };
                opened.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                return opened;
            }
            ConnectionProfile? Selected(ConnectionDialog d) => ((ComboBox)d.FindName("Profiles")).SelectedItem as ConnectionProfile;

            var dialog = await Open();
            var scroll = (ScrollViewer)dialog.FindName("FormScroll");
            check(scroll.ScrollableHeight == 0, $"Default size shows the whole form without a scroll bar (extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight})");
            check(Selected(dialog) is null, "Without a previous choice no profile is selected");
            var profiles = (ComboBox)dialog.FindName("Profiles");
            profiles.SelectedItem = profiles.Items.OfType<ConnectionProfile>().Single(p => p.Id == second.Id);
            dialog.Close();

            dialog = await Open();
            check(Selected(dialog)?.Id == second.Id && ((TextBox)dialog.FindName("Host")).Text == "second.example", "Reopening selects the profile chosen last and fills its settings");
            // The same messages Windows sends around a border drag.
            nint handle = new WindowInteropHelper(dialog).Handle;
            SendMessage(handle, 0x0231, 0, 0);
            dialog.Width = 700; dialog.Height = 760;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SendMessage(handle, 0x0232, 0, 0);
            dialog.Close();

            dialog = await Open();
            check(Math.Abs(dialog.ActualWidth - 700) < 1 && Math.Abs(dialog.ActualHeight - 760) < 1, "Reopening restores the size the user gave the window");
            dialog.WindowState = WindowState.Maximized;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            dialog.SaveSize();
            dialog.Close();
            dialog = await Open();
            check(Math.Abs(dialog.ActualWidth - 700) < 1, "A maximized window does not replace the saved size");
            dialog.Close();

            dialog = await Open(first.Id);
            check(Selected(dialog)?.Id == first.Id, "Editing a registered server opens that profile");
            dialog.Close();
            dialog = await Open();
            check(Selected(dialog)?.Id == second.Id, "Editing a registered server does not change the remembered profile");
            dialog.Close();
            dialog = await Open(group: "Production");
            check(Selected(dialog) is null, "A new registration starts empty");
            dialog.Close();

            store.Delete(second.Id);
            dialog = await Open();
            check(Selected(dialog) is null, "A deleted profile is not selected");
            dialog.Close();

            File.WriteAllText(Path.Combine(directory, "connect-dialog.json"), "not json");
            dialog = await Open();
            check(Selected(dialog) is null && dialog.IsVisible, "An unreadable preferences file does not stop the dialog");
            dialog.Close();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
