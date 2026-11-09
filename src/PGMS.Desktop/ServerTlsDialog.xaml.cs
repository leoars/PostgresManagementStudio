using System.ComponentModel;
using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class ServerTlsDialog : Window
{
    private readonly ServerTlsService service;
    private readonly string host, profilePath;
    private readonly int port;
    private ServerTlsState? state;
    private bool ready, busy;
    public ServerTlsResult? Result { get; private set; }
    public string? RootCertificateFile { get; private set; }
    public int UpdatedProfiles { get; private set; }

    public ServerTlsDialog(string connection, string? profilePath = null)
    {
        InitializeComponent(); service = new(connection); this.profilePath = profilePath ?? ConnectionProfileStore.DefaultPath;
        var c = new NpgsqlConnectionStringBuilder(connection); host = c.Host ?? ""; port = c.Port;
        Target.Text = $"Configure Server TLS\nServer: {host}:{port}   Connected role: {c.Username}   TLS: {c.SslMode}";
        Names.Text = RolePasswordService.IsLoopback(host) ? string.Join(", ", new[] { host, "localhost", "127.0.0.1", "::1" }.Distinct(StringComparer.OrdinalIgnoreCase)) : host;
        UpdateProfiles.Content = $"Update saved connection profiles for {host}:{port} to use and verify this certificate";
        ready = true; Changed(this, new RoutedEventArgs());
        Loaded += async (_, _) => await InspectAsync();
    }

    public async Task InspectAsync()
    {
        if (busy) return;
        SetBusy(true); Feedback.Text = "Reading server TLS settings…";
        try
        {
            state = await service.InspectAsync();
            Inspection.Text = $"ssl = {(state.SslOn ? "on" : "off")}\nssl_cert_file = {state.CertificateFile}\nssl_key_file = {state.KeyFile}\ndata_directory = {state.DataDirectory}\nsuperuser = {state.Superuser}   server on Windows = {state.Windows}";
            Feedback.Text = state.Blocker ?? (state.SslOn ? "TLS is already on. Applying replaces the certificate for new connections; open sessions keep theirs." : "TLS is off. Choose a certificate and Apply.");
        }
        catch (Exception ex) when (Handled(ex)) { state = null; Feedback.Text = Describe(ex); }
        finally { SetBusy(false); }
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        GeneratePanel.IsEnabled = GenerateOption.IsChecked == true; ImportPanel.IsEnabled = ImportOption.IsChecked == true;
        ApplyButton.IsEnabled = !busy && state is { Blocker: null } && (GenerateOption.IsChecked == true ? Names.Text.Trim().Length > 0 && Years.Text.Trim().Length > 0 : CertificatePath.Text.Trim().Length > 0 && KeyPath.Text.Trim().Length > 0);
    }

    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; Changed(this, new RoutedEventArgs()); }

    private void Browse(object sender, RoutedEventArgs e)
    {
        bool key = sender == BrowseKey;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = key ? "Private key" : "Certificate", Filter = key ? "PEM keys (*.key;*.pem)|*.key;*.pem|All files (*.*)|*.*" : "PEM certificates (*.crt;*.pem;*.cer)|*.crt;*.pem;*.cer|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) == true) (key ? KeyPath : CertificatePath).Text = picker.FileName;
    }

    internal ServerCertificateBundle CreateBundle()
    {
        if (GenerateOption.IsChecked == true)
        {
            if (!int.TryParse(Years.Text.Trim(), out int years)) throw new ArgumentException("Validity must be a whole number of years.");
            return ServerCertificateBundle.Generate(Names.Text.Split(','), years);
        }
        try { return ServerCertificateBundle.Import(File.ReadAllText(CertificatePath.Text.Trim()), File.ReadAllText(KeyPath.Text.Trim())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ArgumentException("The certificate or key file could not be read: " + ex.Message, ex); }
    }

    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync(confirm: true);
    public async Task ApplyAsync(bool confirm)
    {
        if (busy || state is null) return;
        ServerCertificateBundle bundle;
        try { bundle = CreateBundle(); }
        catch (ArgumentException ex) { Feedback.Text = ex.Message; return; }
        string names = bundle.Names.Count > 0 ? string.Join(", ", bundle.Names) : bundle.Subject;
        if (confirm && MessageBox.Show(this, $"Enable TLS on {host}:{port} with this certificate?\n\nNames: {names}\nValid until: {bundle.NotAfter:yyyy-MM-dd}\n{(bundle.SelfSigned ? "Self-signed" : "Issued by a certificate authority")}\n\nNew connections will use it after a configuration reload; nothing is restarted.", "Configure Server TLS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); Feedback.Text = "Writing certificate files, enabling TLS and verifying a new session…";
        string message;
        try
        {
            Result = await service.ApplyAsync(bundle);
            message = $"TLS is on with {Result.CertificateFile}. A new session presented this certificate.";
            if (UpdateProfiles.IsChecked == true) message += " " + SaveProfiles(bundle, Result);
            message += " Reconnect to use TLS in this window.";
        }
        catch (Exception ex) when (Handled(ex)) { message = Describe(ex); }
        finally { SetBusy(false); }
        await InspectAsync();
        Feedback.Text = message;
    }

    // Self-signed certificates are kept locally as the profile's root so VerifyFull can check them.
    private string SaveProfiles(ServerCertificateBundle bundle, ServerTlsResult result)
    {
        try
        {
            string? root = null;
            if (result.VerifyFull && bundle.SelfSigned)
            {
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath))!, "certificates");
                Directory.CreateDirectory(directory);
                string safeHost = string.Concat(host.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
                root = Path.Combine(directory, $"{safeHost}_{port}_{bundle.Thumbprint[..16].ToLowerInvariant()}.crt");
                File.WriteAllText(root, bundle.CertificatePem);
                RootCertificateFile = root;
            }
            string mode = result.VerifyFull ? "VerifyFull" : "Require";
            UpdatedProfiles = new ConnectionProfileStore(profilePath).UpdateTls(host, port, mode, root);
            return UpdatedProfiles == 0 ? "No saved profile uses this server." : $"{UpdatedProfiles} saved profile(s) now use SSL Mode {mode}{(root is null ? "" : " with the certificate as root")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidDataException)
        { return "Saved profiles were not updated: " + ex.Message; }
    }

    private static bool Handled(Exception ex) => ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException;
    private static string Describe(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
