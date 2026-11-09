using System.ComponentModel;
using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class RolePasswordDialog : Window
{
    private readonly string connection; private bool ready, busy;
    public bool WasApplied { get; private set; }
    public RolePasswordDialog(string connection, string? role = null)
    {
        this.connection = connection; InitializeComponent(); var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Role Password\nServer: {c.Host}:{c.Port}   Connected role: {c.Username}   TLS: {c.SslMode}"; RoleName.Text = role ?? ""; RolePasswordInput.ShowUnencryptedOption(connection, AllowUnencrypted); ready = true; Update();
    }
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    private void Update()
    {
        var (ready, message) = RolePasswordInput.Evaluate(connection, RoleName.Text, Password, ConfirmPassword, AllowUnencrypted.IsChecked == true);
        ApplyButton.IsEnabled = !busy && ready; Feedback.Text = message;
    }
    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; Update(); }
    private async void Inspect(object sender, RoutedEventArgs e)
    {
        if (busy) return; SetBusy(true); Feedback.Text = "Loading roles…";
        try { var roles = await new RoleAdministrationService(connection).ListRolesAsync(); Feedback.Text = roles.Contains(RoleName.Text, StringComparer.Ordinal) ? "Role exists. Enter a new password to continue." : "Role was not found in the catalog."; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return;
        if (await RolePasswordInput.ApplyAsync(this, connection, RoleName.Text, Password, ConfirmPassword, AllowUnencrypted.IsChecked == true, SetBusy, text => Feedback.Text = text)) WasApplied = true;
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; return; } Password.Clear(); ConfirmPassword.Clear(); }
}
