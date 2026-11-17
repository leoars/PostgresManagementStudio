using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;

// PGMS-009: client certificate, client key and key password in the Connect dialog and in saved profiles.
internal static class ClientCertificateDialogChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pgms-client-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string profiles = Path.Combine(directory, "connections.json");
        string certFile = Path.Combine(directory, "client.crt"), keyFile = Path.Combine(directory, "client.key"), encryptedKeyFile = Path.Combine(directory, "client-encrypted.key");
        string keyPassword = "PGMS-synthetic-key-" + Guid.NewGuid().ToString("N")[..8];
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=pgms_client_test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(certFile, certificate.ExportCertificatePem());
            File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(encryptedKeyFile, key.ExportEncryptedPkcs8PrivateKeyPem(keyPassword, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10000)));
        }
        var dialog = new ConnectionDialog(profilePath: profiles) { ShowInTaskbar = false, Opacity = 0 };
        T Find<T>(string name) where T : class => (T)dialog.FindName(name);
        TextBlock Error() => Find<TextBlock>("Error");
        try
        {
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            foreach (var (name, label) in new[] { ("ClientCertificate", "Client certificate"), ("ClientKey", "Client key"), ("KeyPassword", "Key password"), ("BrowseClientCertificateButton", "Browse client certificate"), ("BrowseClientKeyButton", "Browse client key") })
                check(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(name)) == label, "Connect field has an accessible name: " + label);
            check(Find<ScrollViewer>("FormScroll").ScrollableHeight == 0, "The Connect form still fits without scrolling with the three new fields");

            Find<TextBox>("ProfileName").Text = "Certificate";
            Find<TextBox>("Host").Text = "127.0.0.1"; Find<TextBox>("Port").Text = "1";
            Find<TextBox>("ClientCertificate").Text = certFile;
            await dialog.TestConnectionAsync();
            check(Error().Text == "Enter the key of the client certificate.", "A client certificate without its key is refused before connecting");
            Find<TextBox>("ClientCertificate").Text = ""; Find<TextBox>("ClientKey").Text = keyFile;
            await dialog.TestConnectionAsync();
            check(Error().Text == "Enter the client certificate of this key.", "A client key without its certificate is refused before connecting");
            Find<TextBox>("ClientCertificate").Text = Path.Combine(directory, "missing.crt");
            await dialog.TestConnectionAsync();
            check(Error().Text == "The client certificate file was not found.", "A missing client certificate file is refused");
            Find<TextBox>("ClientCertificate").Text = certFile; Find<TextBox>("ClientKey").Text = encryptedKeyFile;
            await dialog.TestConnectionAsync();
            check(Error().Text.Contains("encrypted", StringComparison.Ordinal) && Error().Text.Contains("Key password", StringComparison.Ordinal), "An encrypted key without its password names the missing field");
            check(ConnectionDialog.ClientKeyIsEncrypted(encryptedKeyFile) && !ConnectionDialog.ClientKeyIsEncrypted(keyFile), "Encrypted and plain PEM keys are told apart");

            Find<PasswordBox>("KeyPassword").Password = keyPassword;
            Find<CheckBox>("RememberPassword").IsChecked = true;
            Find<Button>("SaveProfileButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var saved = new ConnectionProfileStore(profiles).Load().Single();
            check(saved.ClientCertificate == certFile && saved.ClientKey == encryptedKeyFile && saved.ProtectedKeyPassword is not null, "The profile saves the certificate, the key and a protected key password");
            check(!File.ReadAllText(profiles).Contains(keyPassword, StringComparison.Ordinal), "The key password is not stored in clear text in the profile file");
            check(saved.RevealKeyPassword() == keyPassword && saved.RevealPassword() == "", "The key password is revealed only for this profile and is kept apart from the login password");
            var built = new NpgsqlConnectionStringBuilder(saved.ToConnectionString(""));
            check(built.SslCertificate == certFile && built.SslKey == encryptedKeyFile && built.SslPassword == keyPassword, "The profile connection carries the certificate, the key and the key password");

            var notRemembered = saved.WithKeyPassword(keyPassword, remember: false);
            check(notRemembered.ProtectedKeyPassword is null && new NpgsqlConnectionStringBuilder(notRemembered.ToConnectionString("")).SslPassword is null or "", "Without Save passwords the key password is not kept");
            bool halfRefused = false; try { (saved with { ClientKey = null }).Validate(); } catch (ArgumentException) { halfRefused = true; }
            check(halfRefused, "A profile with a certificate and no key is refused");

            dialog.Close();
            dialog = new ConnectionDialog(saved.Id, profilePath: profiles) { ShowInTaskbar = false, Opacity = 0 };
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(Find<TextBox>("ClientCertificate").Text == certFile && Find<TextBox>("ClientKey").Text == encryptedKeyFile && Find<PasswordBox>("KeyPassword").Password == keyPassword && Find<CheckBox>("RememberPassword").IsChecked == true,
                "Reopening the profile fills the certificate, the key and the saved key password");
            Find<Button>("NewProfileButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check(Find<PasswordBox>("KeyPassword").Password.Length == 0, "A new profile clears the key password");
            dialog.Prefill(saved.ToConnectionString(""));
            check(Find<TextBox>("ClientCertificate").Text == certFile && Find<TextBox>("ClientKey").Text == encryptedKeyFile && Find<PasswordBox>("KeyPassword").Password.Length == 0,
                "Registering from an existing connection copies the certificate paths but not the key password");
        }
        finally
        {
            dialog.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Directory.Delete(directory, recursive: true);
        }
    }
}
