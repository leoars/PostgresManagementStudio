using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using PGMS.Desktop;

internal static class ConnectionExperienceChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var dialog = new ConnectionDialog { ShowInTaskbar = false };
        dialog.Show();
        await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            foreach (var name in new[] { "ProfileName", "Host", "Port", "Database", "Username", "Password", "Ssl", "Profiles" })
            {
                var element = (UIElement)dialog.FindName(name);
                check(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(element)?.GetName()), "M93 accessible connection field " + name);
            }
            check(Keyboard.FocusedElement == dialog.FindName("Host"), "M93 initial server focus");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "L", false);
            check(Keyboard.FocusedElement == dialog.FindName("Ssl"), "M93 SSL shortcut has a distinct key");
            var settings = new Npgsql.NpgsqlConnectionStringBuilder(connection);
            ((TextBox)dialog.FindName("Host")).Text = settings.Host;
            ((TextBox)dialog.FindName("Port")).Text = settings.Port.ToString();
            ((TextBox)dialog.FindName("Username")).Text = settings.Username;
            ((TextBox)dialog.FindName("Database")).Text = settings.Database;
            ((ComboBox)dialog.FindName("Ssl")).SelectedIndex = 3;
            ((Button)dialog.FindName("TestButton")).Focus();
            var pending = dialog.TestConnectionAsync();
            check(Keyboard.FocusedElement == dialog.FindName("CancelTestButton"), "M96 pending test focuses cancel action");
            check(new[] { "Profiles", "ProfileName", "NewProfileButton", "Host", "Port", "Database", "Username", "Password", "Ssl", "RememberPassword", "SaveProfileButton", "DeleteProfileButton", "ConnectButton", "TestButton" }.All(n => !((UIElement)dialog.FindName(n)).IsEnabled), "M94 settings and profile mutations disabled during test");
            await dialog.TestConnectionAsync();
            check(!pending.IsCompleted, "M94 duplicate Test does not start another operation");
            await pending;
            check(Keyboard.FocusedElement == dialog.FindName("TestButton"), "M96 successful test restores initiating focus");
            check(((TextBlock)dialog.FindName("Error")).Text.Contains("Connection succeeded"), "M94 real isolated connection succeeds");
            check(((TextBox)dialog.FindName("Host")).IsEnabled && !((Button)dialog.FindName("DeleteProfileButton")).IsEnabled, "M94 controls restored respecting absent profile");
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                ((TextBox)dialog.FindName("Port")).Text = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString();
                pending = dialog.TestConnectionAsync();
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
                check(!pending.IsCompleted && ((Button)dialog.FindName("CancelTestButton")).IsVisible, "M95 cancellation available during stalled backend startup");
                AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "X", false);
                string cancellationFeedback=((TextBlock)dialog.FindName("Error")).Text;
                check(cancellationFeedback.Contains("10 seconds",StringComparison.Ordinal) ||
                    (pending.IsCompleted && cancellationFeedback.Contains("test canceled.",StringComparison.Ordinal)),
                    "M97 cancellation explains pending network timeout or already completed cancellation");
                await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(dialog, "m97-connect-canceling");
                check(!((Button)dialog.FindName("CancelTestButton")).IsEnabled, "M95 cancel request disables repeated cancellation");
                await pending.WaitAsync(TimeSpan.FromSeconds(15));
                check(dialog.IsVisible && ((TextBlock)dialog.FindName("Error")).Text.Contains("test canceled."), "M95 cancellation keeps dialog open with explicit feedback");
                check(((TextBox)dialog.FindName("Host")).IsEnabled, "M95 canceled startup releases controls");
            }
            finally { listener.Stop(); }
            ((TextBox)dialog.FindName("Port")).Text = settings.Port.ToString();
            await dialog.TestConnectionAsync();
            check(((TextBlock)dialog.FindName("Error")).Text.Contains("Connection succeeded"), "M95 retry after cancellation succeeds");
            ((TextBox)dialog.FindName("Database")).Text = "pgms_missing_connection_experience";
            ((Button)dialog.FindName("TestButton")).Focus();
            await dialog.TestConnectionAsync();
            check(((TextBlock)dialog.FindName("Error")).Text.Contains("3D000") && Keyboard.FocusedElement == dialog.FindName("TestButton"), "M96 server error keeps SQLSTATE and restores focus");
            ((TextBox)dialog.FindName("Port")).Text = "70000";
            await dialog.TestConnectionAsync();
            check(Keyboard.FocusedElement == dialog.FindName("Port") && ((TextBox)dialog.FindName("Port")).SelectedText == "70000", "M96 validation focuses and selects invalid port");
            check(((TextBlock)dialog.FindName("Error")).Text.Contains("1 to 65535"), "M96 validation names required correction");
            await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(dialog, "m97-connect-normal");
            dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
            foreach (double font in new[] { 12.0, 18.0, 24.0 })
            {
                dialog.FontSize = font; dialog.UpdateLayout();
                var actions = new[] { "TestButton", "ConnectButton" }.Select(n => (Button)dialog.FindName(n));
                check(actions.All(b => { var point = b.TranslatePoint(new Point(), dialog); return point.X >= 0 && point.Y >= 0 && point.X + b.ActualWidth <= dialog.ActualWidth && point.Y + b.ActualHeight <= dialog.ActualHeight; }), $"M97 fixed actions fit minimum window at font {font}");
                var scroll = (ScrollViewer)dialog.FindName("FormScroll");
                check(scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 0, $"M97 form scrolls at font {font}");
                scroll.ScrollToBottom(); dialog.UpdateLayout();
                await dialog.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(dialog, $"m97-connect-small-{font}");
            }
            listener.Start();
            try
            {
                ((TextBox)dialog.FindName("Port")).Text = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString();
                pending = dialog.TestConnectionAsync();
                using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
                dialog.Close();
                check(dialog.IsVisible, "M97 close waits for active connection cleanup");
                await pending.WaitAsync(TimeSpan.FromSeconds(15));
                check(!dialog.IsVisible, "M97 requested close completes after test ends");
            }
            finally { listener.Stop(); }
        }
        finally { dialog.Close(); }
    }
    private static void Capture(Window window, string name)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/review");
        using var file = File.Create($"artifacts/review/{name}.png"); encoder.Save(file);
    }
}
