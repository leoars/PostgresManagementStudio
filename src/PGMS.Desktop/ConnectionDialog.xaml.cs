using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using Npgsql;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using PGMS.PostgreSql;
namespace PGMS.Desktop;
public partial class ConnectionDialog : Window
{
    public string ConnectionString { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    private readonly ConnectionProfileStore store = new(ConnectionProfileStore.DefaultPath);
    private Guid? selectedId;
    private CancellationTokenSource? testCancellation;
    private bool closeAfterTest;
    private bool testCanceledByUser;
    private readonly DialogFocus testFocus = new();
    public ConnectionDialog()
    {
        InitializeComponent(); WindowWorkArea.Attach(this); Loaded += (_, _) => Host.Focus();
        try { ReloadProfiles(); }
        catch (Exception ex) when (IsProfileError(ex)) { Error.Text = "Profiles could not be loaded. The existing file was preserved. " + ex.Message; }
    }
    private static bool IsProfileError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or CryptographicException or FormatException;
    private void ReloadProfiles(Guid? selected = null)
    {
        Profiles.ItemsSource = store.Load();
        Profiles.SelectedItem = Profiles.Items.OfType<ConnectionProfile>().FirstOrDefault(p => p.Id == selected);
    }
    private void NewProfile(object sender, RoutedEventArgs e)
    {
        Profiles.SelectedItem = null; selectedId = null; ProfileName.Clear(); Password.Clear(); RememberPassword.IsChecked = false; DeleteProfileButton.IsEnabled = false; Error.Text = "New profile. Enter settings and choose Save Profile.";
    }
    private void SelectProfile(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not ConnectionProfile p) { selectedId = null; DeleteProfileButton.IsEnabled = false; return; }
        selectedId = p.Id; ProfileName.Text = p.Name; Host.Text = p.Host; Port.Text = p.Port.ToString(); Database.Text = p.Database; Username.Text = p.Username;
        Ssl.SelectedItem = Ssl.Items.OfType<ComboBoxItem>().First(i => (string)i.Content == p.SslMode);
        Password.Clear(); RememberPassword.IsChecked = false; DeleteProfileButton.IsEnabled = true; Error.Text = "";
        try { Password.Password = p.RevealPassword(); RememberPassword.IsChecked = p.ProtectedPassword is not null; }
        catch (Exception ex) when (IsProfileError(ex)) { Error.Text = "Saved password is unavailable. Re-enter it and save the profile to replace it."; }
    }
    private void SaveProfile(object sender, RoutedEventArgs e)
    {
        if (testCancellation is not null || !TrySettings(out var settings)) return;
        try
        {
            var profile = new ConnectionProfile(selectedId ?? Guid.NewGuid(), ProfileName.Text.Trim(), settings.Host!, settings.Port, settings.Database!, settings.Username!, settings.SslMode.ToString());
            profile.Validate();
            profile = profile.WithPassword(Password.Password, RememberPassword.IsChecked == true);
            string sessionPassword = Password.Password;
            store.Save(profile); ReloadProfiles(profile.Id); Password.Password = sessionPassword; Error.Text = "Profile saved.";
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
        foreach (var control in new Control[] { Profiles, ProfileName, NewProfileButton, Host, Port, Database, Username, Password, Ssl, RememberPassword, SaveProfileButton, TestButton, ConnectButton })
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
        settings = new NpgsqlConnectionStringBuilder
        {
            Host = Host.Text.Trim(), Port = port, Database = Database.Text.Trim(), Username = Username.Text.Trim(),
            Password = Password.Password, SslMode = Enum.Parse<SslMode>((string)((ComboBoxItem)Ssl.SelectedItem).Content),
            Timeout = 10, ApplicationName = "PGMS", Pooling = false, IncludeErrorDetail = false
        };
        return true;
    }
}
