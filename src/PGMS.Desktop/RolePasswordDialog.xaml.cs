using System.ComponentModel;
using System.IO;
using System.Security;
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
        Target.Text = $"Role Password\nServer: {c.Host}:{c.Port}   Connected role: {c.Username}   TLS: {c.SslMode}"; RoleName.Text = role ?? ""; ready = true; Update();
    }
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    private void Update()
    {
        bool valid = !string.IsNullOrWhiteSpace(RoleName.Text) && Password.SecurePassword.Length > 0 && Password.SecurePassword.Length == ConfirmPassword.SecurePassword.Length && Password.Password == ConfirmPassword.Password;
        var ssl = new NpgsqlConnectionStringBuilder(connection).SslMode;
        bool tls = ssl is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull;
        ApplyButton.IsEnabled = !busy && valid && tls;
        if (!tls) Feedback.Text = "This connection profile does not guarantee TLS. Change SSL Mode to Require, VerifyCA or VerifyFull and reconnect.";
        else if (valid) Feedback.Text = "The secret will not be shown or saved.";
        else if (Password.SecurePassword.Length > 0 && Password.Password != ConfirmPassword.Password) Feedback.Text = "Passwords do not match.";
        else Feedback.Text = "Enter a role and matching non-empty passwords.";
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
        if (Password.Password != ConfirmPassword.Password) { Feedback.Text = "Passwords do not match."; return; }
        using (var candidate = Password.SecurePassword.Copy())
        {
            try { RolePasswordService.ValidatePassword(candidate); }
            catch (ArgumentException ex) { Feedback.Text = ex.Message; Password.Focus(); return; }
        }
        if (MessageBox.Show(this, $"Set a new password for role {RoleName.Text}?\nThe secret is sent only over TLS and is not saved.", "Set Role Password", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SecureString secret = Password.SecurePassword.Copy(); SetBusy(true); Feedback.Text = "Applying encrypted password change…";
        try { await RolePasswordService.SetAsync(connection, RoleName.Text, secret); WasApplied = true; Feedback.Text = "Password changed. Inspect or connect to verify access."; Password.Clear(); ConfirmPassword.Clear(); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { secret.Dispose(); SetBusy(false); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; return; } Password.Clear(); ConfirmPassword.Clear(); }
}
