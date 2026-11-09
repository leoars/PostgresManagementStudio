using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

internal static class ServerTlsDialogChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pgms-tls-ui-" + Guid.NewGuid().ToString("N"));
        string profiles = Path.Combine(directory, "connections.json");
        var store = new ConnectionProfileStore(profiles);
        var same = new ConnectionProfile(Guid.NewGuid(), "same server", "127.0.0.1", 55439, "postgres", "pgms_test", "Require");
        var other = new ConnectionProfile(Guid.NewGuid(), "other server", "127.0.0.1", 55440, "postgres", "pgms_test", "Prefer", RootCertificate: "C:\\keep\\root.crt");
        store.Save(same); store.Save(other);
        check(store.Load().Single(p => p.Id == other.Id).RootCertificate == "C:\\keep\\root.crt", "Connection profiles keep an optional root certificate");

        var service = new ServerTlsService(connection);
        var before = await service.InspectAsync();
        var dialog = new ServerTlsDialog(connection, profiles) { ShowInTaskbar = false, Opacity = 0 };
        T Find<T>(string name) where T : FrameworkElement => (T)dialog.FindName(name);
        try
        {
            dialog.Show();
            for (int i = 0; i < 200 && Find<TextBox>("Inspection").Text.Length == 0; i++) await Task.Delay(50);
            check(Find<TextBox>("Inspection").Text.Contains("ssl = on", StringComparison.Ordinal) && Find<Button>("ApplyButton").IsEnabled, "Server TLS dialog inspects the server and enables Apply for a superuser on Windows");
            check(Find<TextBox>("Names").Text.Contains("127.0.0.1", StringComparison.Ordinal) && Find<TextBox>("Names").Text.Contains("localhost", StringComparison.Ordinal), "Loopback server names are prefilled for the certificate");
            string names = Find<TextBox>("Names").Text;
            Find<TextBox>("Names").Text = "";
            bool emptyNames = !Find<Button>("ApplyButton").IsEnabled;
            Find<TextBox>("Names").Text = names; Find<RadioButton>("ImportOption").IsChecked = true;
            bool emptyImport = !Find<Button>("ApplyButton").IsEnabled && !Find<Grid>("GeneratePanel").IsEnabled;
            Find<RadioButton>("GenerateOption").IsChecked = true;
            check(emptyNames && emptyImport && Find<Button>("ApplyButton").IsEnabled, "Apply requires names to generate or both files to import");

            await dialog.ApplyAsync(confirm: false);
            var saved = store.Load();
            var updated = saved.Single(p => p.Id == same.Id);
            check(dialog.Result is { VerifyFull: true } && dialog.UpdatedProfiles == 1 && updated.SslMode == "VerifyFull" && updated.RootCertificate is { } root && File.Exists(root) && saved.Single(p => p.Id == other.Id) == other,
                "Apply enables TLS and moves only this server's profiles to VerifyFull with the stored root (" + Find<TextBlock>("Feedback").Text + ")");
        }
        finally
        {
            dialog.Close();
            await service.RestoreAsync(before);
            await Task.Delay(1000);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
        check((await service.InspectAsync()).CertificateFile == before.CertificateFile, "Server TLS UI check restores the original certificate settings");

        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var tree = (TreeView)window.FindName("ExplorerTree");
            tree.Items.Clear();
            var catalog = new CatalogService(connection);
            var server = new CatalogNode(new CatalogEntry(CatalogKind.Server, "127.0.0.1:55439"), catalog);
            var role = new CatalogNode(new CatalogEntry(CatalogKind.Role, "pgms_test (login)") { RoleName = "pgms_test" }, catalog);
            tree.Items.Add(server); tree.Items.Add(role);
            async Task<bool> Enabled(CatalogNode node)
            {
                node.IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                bool enabled = menu.Items.OfType<MenuItem>().SingleOrDefault(m => m.Name == "ExplorerServerTls") is { IsEnabled: true };
                menu.IsOpen = false; return enabled;
            }
            check(await Enabled(server) && !await Enabled(role), "Configure Server TLS appears only on the server node");
        }
        finally { window.Close(); }
    }
}
