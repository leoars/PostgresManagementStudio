using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using Npgsql;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Interop;
using PGMS.PostgreSql;
namespace PGMS.Desktop;
/// <summary>Last profile chosen and window size of the Connect dialog. Losing them only costs the convenience, so failures are ignored.</summary>
public sealed class ConnectionDialogPreferences(string path)
{
    public sealed record Document(int Version = 1, Guid? LastProfile = null, double? Width = null, double? Height = null);
    public Document Load()
    {
        try { return JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { Version: 1 } document ? document : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Update(Func<Document, Document> change)
    {
        try
        {
            string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(change(Load())));
            File.Move(temporary, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
public partial class ConnectionDialog : Window
{
    public string ConnectionString { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    private readonly ConnectionProfileStore store = new(ConnectionProfileStore.DefaultPath);
    /// <summary>Id of the last profile saved in this dialog, so Registered Servers can select it.</summary>
    public Guid? SavedProfileId { get; private set; }
    private readonly ConnectionDialogPreferences preferences;
    // Editing a registered server or registering a new one is not a choice of what to connect to next.
    private bool rememberChoice;
    private Guid? selectedId;
    private CancellationTokenSource? testCancellation;
    private bool closeAfterTest;
    private bool testCanceledByUser;
    private readonly DialogFocus testFocus = new();
    // Registered Servers group given to profiles saved as new from this dialog; edited profiles keep their own group.
    private readonly string? newProfileGroup;
    public ConnectionDialog(Guid? profileId = null, string? newProfileGroup = null, string? profilePath = null)
    {
        InitializeComponent(); WindowWorkArea.Attach(this); this.newProfileGroup = newProfileGroup;
        if (profilePath is not null) store = new(profilePath);
        preferences = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath ?? ConnectionProfileStore.DefaultPath))!, "connect-dialog.json"));
        var saved = preferences.Load();
        if (saved.Width is { } width && saved.Height is { } height) { Width = Math.Max(width, MinWidth); Height = Math.Max(height, MinHeight); }
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WatchUserResize);
        Loaded += (_, _) => { if (Profiles.SelectedItem is not null && Password.Password.Length == 0) Password.Focus(); else Host.Focus(); };
        try { ReloadProfiles(profileId ?? (newProfileGroup is null ? saved.LastProfile : null)); }
        catch (Exception ex) when (IsProfileError(ex)) { Error.Text = "Profiles could not be loaded. The existing file was preserved. " + ex.Message; }
        rememberChoice = profileId is null && newProfileGroup is null;
        if (newProfileGroup is not null && profileId is null) Error.Text = $"New registration in group \"{newProfileGroup}\". Enter settings and choose Save Profile.";
    }
    /// <summary>New profile with the settings of an existing connection (Object Explorer ▸ Register…). The password is not copied.</summary>
    public void Prefill(string connection)
    {
        var c = new NpgsqlConnectionStringBuilder(connection);
        NewProfile(this, new RoutedEventArgs());
        ProfileName.Text = $"{c.Host}:{c.Port}"; Host.Text = c.Host; Port.Text = c.Port.ToString(System.Globalization.CultureInfo.InvariantCulture); Database.Text = c.Database; Username.Text = c.Username;
        Ssl.SelectedItem = Ssl.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == c.SslMode.ToString()) ?? Ssl.SelectedItem;
        RootCertificate.Text = c.RootCertificate ?? "";
        Error.Text = "New server registration from Object Explorer. Enter the password to save it, then choose Save Profile.";
    }
    // Only a resize by the user counts: WindowWorkArea also shrinks the window on small screens, and that must not become the saved size.
    private Size? sizeAtDragStart;
    private nint WatchUserResize(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0231) sizeAtDragStart = new(ActualWidth, ActualHeight);
        if (message == 0x0232 && sizeAtDragStart is { } start && (Math.Abs(start.Width - ActualWidth) >= 1 || Math.Abs(start.Height - ActualHeight) >= 1)) SaveSize();
        return 0;
    }
    internal void SaveSize()
    {
        if (WindowState == WindowState.Normal) preferences.Update(p => p with { Width = ActualWidth, Height = ActualHeight });
    }
    private static bool IsProfileError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or CryptographicException or FormatException;
    private void ReloadProfiles(Guid? selected = null)
    {
        Profiles.ItemsSource = store.Load();
        Profiles.SelectedItem = Profiles.Items.OfType<ConnectionProfile>().FirstOrDefault(p => p.Id == selected);
    }
    private void NewProfile(object sender, RoutedEventArgs e)
    {
        Profiles.SelectedItem = null; selectedId = null; ProfileName.Clear(); Password.Clear(); RememberPassword.IsChecked = false; DisableHistory.IsChecked = false; DeleteProfileButton.IsEnabled = false; Error.Text = "New profile. Enter settings and choose Save Profile.";
    }
    private void SelectProfile(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not ConnectionProfile p) { selectedId = null; DeleteProfileButton.IsEnabled = false; return; }
        if (rememberChoice) preferences.Update(d => d with { LastProfile = p.Id });
        selectedId = p.Id; ProfileName.Text = p.Name; Host.Text = p.Host; Port.Text = p.Port.ToString(); Database.Text = p.Database; Username.Text = p.Username;
        Ssl.SelectedItem = Ssl.Items.OfType<ComboBoxItem>().First(i => (string)i.Content == p.SslMode); RootCertificate.Text = p.RootCertificate ?? "";
        DisableHistory.IsChecked = p.DisableQueryHistory;
        Password.Clear(); RememberPassword.IsChecked = false; DeleteProfileButton.IsEnabled = true; Error.Text = "";
        try { Password.Password = p.RevealPassword(); RememberPassword.IsChecked = p.ProtectedPassword is not null; }
        catch (Exception ex) when (IsProfileError(ex)) { Error.Text = "Saved password is unavailable. Re-enter it and save the profile to replace it."; }
    }
    private void SaveProfile(object sender, RoutedEventArgs e)
    {
        if (testCancellation is not null || !TrySettings(out var settings)) return;
        try
        {
            string? group = selectedId is { } existing ? store.Load().FirstOrDefault(p => p.Id == existing)?.Group : newProfileGroup;
            var profile = new ConnectionProfile(selectedId ?? Guid.NewGuid(), ProfileName.Text.Trim(), settings.Host!, settings.Port, settings.Database!, settings.Username!, settings.SslMode.ToString(), RootCertificate: string.IsNullOrEmpty(settings.RootCertificate) ? null : settings.RootCertificate, Group: group, DisableQueryHistory: DisableHistory.IsChecked == true);
            profile.Validate();
            profile = profile.WithPassword(Password.Password, RememberPassword.IsChecked == true);
            string sessionPassword = Password.Password;
            store.Save(profile); SavedProfileId = profile.Id; ReloadProfiles(profile.Id); Password.Password = sessionPassword; Error.Text = "Profile saved.";
        }
        catch (Exception ex) when (IsProfileError(ex)) { Error.Text = "Profile was not saved. " + ex.Message; }
    }
    private void DeleteProfile(object sender, RoutedEventArgs e)
    {
        if (selectedId is not { } id || MessageBox.Show(this, "Delete this saved profile and its protected password?", "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { store.Delete(id); ReloadProfiles(); NewProfile(sender, e); Error.Text = "Profile deleted."; }
        catch (Exception ex) when (IsProfileError(ex)) { Error.Text = "Profile was not deleted. " + ex.Message; }
    }
    private void Accept(object sender, RoutedEventArgs e)
    {
        if (testCancellation is not null || !TrySettings(out var settings)) return;
        ConnectionString = settings.ConnectionString;
        DisplayName = $"{settings.Host}:{settings.Port} ({settings.Database})";
        DialogResult = true;
    }
    private async void TestConnection(object sender, RoutedEventArgs e) => await TestConnectionAsync();
    public async Task TestConnectionAsync()
    {
        if (testCancellation is not null || !TrySettings(out var settings)) return;
        testCanceledByUser = false;
        testCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        testFocus.Capture();
        SetTesting(true);
        CancelTestButton.Focus();
        Error.Foreground = System.Windows.Media.Brushes.DimGray; Error.Text = "Testing connection...";
        try
        {
            var result = await new ConnectionTestService(settings.ConnectionString).TestAsync(testCancellation.Token);
            testCancellation.Token.ThrowIfCancellationRequested();
            Error.Foreground = System.Windows.Media.Brushes.SeaGreen;
            Error.Text = $"Connection succeeded.\nDatabase: {result.Database}   User: {result.User}   TLS: {(result.Tls ? "Yes" : "No")}\n{result.Version}";
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or OperationCanceledException)
        { Error.Foreground = System.Windows.Media.Brushes.Firebrick; Error.Text = ex is PostgresException pg ? $"Connection failed - {pg.SqlState}: {pg.MessageText}" : testCanceledByUser ? "Connection test canceled. You can edit settings or test again." : ex is OperationCanceledException ? "Connection test timed out. Check the server and try again." : "Connection failed: " + ex.Message; }
        finally
        {
            testCancellation.Dispose(); testCancellation = null;
            SetTesting(false);
            if (!closeAfterTest) testFocus.Restore(this, CancelTestButton);
            if (closeAfterTest) Close();
        }
    }
    private void SetTesting(bool testing)
    {
        foreach (var control in new Control[] { Profiles, ProfileName, NewProfileButton, Host, Port, Database, Username, Password, Ssl, RootCertificate, BrowseRootButton, RememberPassword, DisableHistory, SaveProfileButton, TestButton, ConnectButton })
            control.IsEnabled = !testing;
        DeleteProfileButton.IsEnabled = !testing && selectedId is not null;
        CancelTestButton.Visibility = testing ? Visibility.Visible : Visibility.Collapsed;
        CancelTestButton.IsEnabled = testing;
    }
    private void CancelTest(object sender, RoutedEventArgs e)
    {
        if (testCancellation is null) return;
        testCanceledByUser = true;
        CancelTestButton.IsEnabled = false;
        Error.Text = "Canceling connection test... Waiting for the network operation to finish (connection timeout: 10 seconds).";
        testCancellation.Cancel();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (testCancellation is not null) { e.Cancel = true; closeAfterTest = true; testCanceledByUser = true; testCancellation.Cancel(); }
    }
    private bool TrySettings(out NpgsqlConnectionStringBuilder settings)
    {
        settings = new();
        bool Invalid(TextBox field, string message)
        {
            Error.Foreground = System.Windows.Media.Brushes.Firebrick;
            Error.Text = message;
            field.Focus(); field.SelectAll();
            return false;
        }
        if (string.IsNullOrWhiteSpace(Host.Text)) return Invalid(Host, "Enter a server name.");
        if (!int.TryParse(Port.Text, out var port) || port is < 1 or > 65535) return Invalid(Port, "Enter a port from 1 to 65535.");
        if (string.IsNullOrWhiteSpace(Database.Text)) return Invalid(Database, "Enter a database name.");
        if (string.IsNullOrWhiteSpace(Username.Text)) return Invalid(Username, "Enter a user name.");
        string root = RootCertificate.Text.Trim();
        if (root.Length > 0 && !File.Exists(root)) return Invalid(RootCertificate, "The root certificate file was not found.");
        settings = new NpgsqlConnectionStringBuilder
        {
            Host = Host.Text.Trim(), Port = port, Database = Database.Text.Trim(), Username = Username.Text.Trim(),
            Password = Password.Password, SslMode = Enum.Parse<SslMode>((string)((ComboBoxItem)Ssl.SelectedItem).Content),
            Timeout = 10, ApplicationName = "PGMS", Pooling = false, IncludeErrorDetail = false
        };
        if (root.Length > 0) settings.RootCertificate = root;
        return true;
    }
    private void BrowseRootCertificate(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Root certificate", Filter = "PEM certificates (*.crt;*.pem;*.cer)|*.crt;*.pem;*.cer|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) == true) RootCertificate.Text = picker.FileName;
    }
}
