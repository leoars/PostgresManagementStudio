using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

// Shared by Set Role Password and the password fields of Role Properties.
internal static class RolePasswordInput
{
    // The unencrypted opt-in is offered only when the profile is neither TLS nor loopback.
    public static void ShowUnencryptedOption(string connection, CheckBox allowUnencrypted) =>
        allowUnencrypted.Visibility = RolePasswordService.TransportOf(connection) == RolePasswordTransport.Unencrypted ? Visibility.Visible : Visibility.Collapsed;

    public static (bool Ready, string Message) Evaluate(string connection, string role, PasswordBox password, PasswordBox confirm, bool allowUnencrypted)
    {
        bool valid = !string.IsNullOrWhiteSpace(role) && password.SecurePassword.Length > 0 && password.SecurePassword.Length == confirm.SecurePassword.Length && password.Password == confirm.Password;
        var transport = RolePasswordService.TransportOf(connection);
        if (transport == RolePasswordTransport.Unencrypted && !allowUnencrypted) return (false, "This connection profile does not guarantee TLS. Change SSL Mode to Require, VerifyCA or VerifyFull and reconnect, or explicitly allow sending without TLS.");
        if (valid) return (true, transport switch
        {
            RolePasswordTransport.Tls => "The secret will not be shown or saved.",
            RolePasswordTransport.Loopback => "Loopback connection without guaranteed TLS: the password verifier does not leave this computer. The secret will not be shown or saved.",
            _ => "Warning: without TLS the password verifier crosses the network unencrypted and can be captured for offline guessing. The secret will not be shown or saved."
        });
        if (password.SecurePassword.Length > 0 && password.Password != confirm.Password) return (false, "Passwords do not match.");
        return (false, "Enter a role and matching non-empty passwords.");
    }

    public static async Task<bool> ApplyAsync(Window owner, string connection, string role, PasswordBox password, PasswordBox confirm, bool allowUnencrypted, Action<bool> setBusy, Action<string> report)
    {
        var transport = RolePasswordService.TransportOf(connection);
        if (transport == RolePasswordTransport.Unencrypted && !allowUnencrypted) { report("This connection profile does not guarantee TLS. Change SSL Mode to Require, VerifyCA or VerifyFull and reconnect, or explicitly allow sending without TLS."); return false; }
        if (password.Password != confirm.Password) { report("Passwords do not match."); return false; }
        using (var candidate = password.SecurePassword.Copy())
        {
            try { RolePasswordService.ValidatePassword(candidate); }
            catch (ArgumentException ex) { report(ex.Message); password.Focus(); return false; }
        }
        string channel = transport switch
        {
            RolePasswordTransport.Tls => "The password verifier is sent only over TLS and the secret is not saved.",
            RolePasswordTransport.Loopback => "The connection is loopback without guaranteed TLS: the password verifier does not leave this computer. The secret is not saved.",
            _ => "WARNING: the connection is not encrypted. The password verifier will cross the network in clear text and can be captured for offline guessing. The secret is not saved."
        };
        if (MessageBox.Show(owner, $"Set a new password for role {role}?\n{channel}", "Set Role Password", MessageBoxButton.YesNo, transport == RolePasswordTransport.Unencrypted ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        SecureString secret = password.SecurePassword.Copy(); setBusy(true); report("Applying password change…");
        bool applied = false; string message;
        try { await RolePasswordService.SetAsync(connection, role, secret, allowUnencrypted: allowUnencrypted); applied = true; message = "Password changed. Inspect or connect to verify access."; password.Clear(); confirm.Clear(); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { message = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { secret.Dispose(); setBusy(false); }
        report(message);
        return applied;
    }
}
