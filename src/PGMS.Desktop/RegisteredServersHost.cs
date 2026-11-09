using System.IO;
using System.Security.Cryptography;
using System.Windows;
using AvalonDock.Layout;

namespace PGMS.Desktop;

// Registered Servers tool window (M1039): docked next to Object Explorer, opened from View ▸ Registered Servers or Ctrl+Alt+G as in SSMS 22.11.
public partial class MainWindow
{
    private const string RegisteredServersId = "registered";
    private RegisteredServersView? registeredServers;
    /// <summary>Profile file for Registered Servers and the dialogs it opens; tests point it at a temporary file.</summary>
    internal string? RegisteredServersProfilePath { get; set; }
    /// <summary>Password prompt used when a profile has no saved password; replaceable in tests. Returns the connection string or null when cancelled.</summary>
    internal Func<ConnectionProfile, string?>? RegisteredPasswordPrompt { get; set; }

    internal RegisteredServersView RegisteredServers => registeredServers ??= new RegisteredServersView(RegisteredServersProfilePath)
    {
        ConnectObjectExplorer = ConnectRegisteredExplorerAsync,
        NewQuery = NewRegisteredQueryAsync,
        ControlService = (profile, action) => ControlLocalServiceAsync(profile.Host, profile.Port, action),
        ResolveService = (host, port) => LocalServiceResolver(host, port),
        EditProfile = EditRegisteredProfile,
        PromptGroupName = parent => new ServerGroupDialog(parent) { Owner = this } is var dialog && dialog.ShowDialog() == true ? dialog.GroupNameValue : null,
        Confirm = message => MessageBox.Show(this, message, "Registered Servers", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes
    };

    private void ShowRegisteredServers(object sender, RoutedEventArgs e) => ShowRegisteredServers();
    internal void ShowRegisteredServers()
    {
        var anchorable = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == RegisteredServersId);
        if (anchorable is null)
        {
            anchorable = new LayoutAnchorable { Title = "Registered Servers", ContentId = RegisteredServersId, Content = RegisteredServers, CanClose = false, CanHide = true };
            var explorer = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == "explorer");
            if (explorer?.Parent is LayoutAnchorablePane pane) pane.Children.Insert(pane.Children.IndexOf(explorer), anchorable);
            else anchorable.AddToLayout(DockManager, AnchorableShowStrategy.Left);
        }
        RegisteredServers.Reload();
        anchorable.Show(); anchorable.IsActive = true;
    }

    private string? RegisteredConnectionString(ConnectionProfile profile)
    {
        if (profile.ProtectedPassword is not null)
            try { return profile.ToConnectionString(profile.RevealPassword()); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { } // Saved by another Windows user or damaged: ask instead.
        if (RegisteredPasswordPrompt is { } prompt) return prompt(profile);
        var dialog = new ConnectionDialog(profile.Id, profilePath: RegisteredServersProfilePath) { Owner = this };
        bool accepted = dialog.ShowDialog() == true;
        registeredServers?.Reload();
        return accepted ? dialog.ConnectionString : null;
    }

    internal async Task ConnectRegisteredExplorerAsync(ConnectionProfile profile)
    {
        if (RegisteredConnectionString(profile) is not { } connection) return;
        await ConnectExplorerAsync(connection);
        ShowExplorer(this, new RoutedEventArgs());
    }

    internal async Task NewRegisteredQueryAsync(ConnectionProfile profile)
    {
        if (RegisteredConnectionString(profile) is not { } connection) return;
        var query = NewQuery();
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
    }

    // Properties and New Server Registration open the Connect dialog on the profile; choosing Connect there also connects Object Explorer.
    private Guid? EditRegisteredProfile(Guid? profileId, string? group)
    {
        var dialog = new ConnectionDialog(profileId, group, RegisteredServersProfilePath) { Owner = this };
        if (dialog.ShowDialog() == true) _ = ConnectExplorerAsync(dialog.ConnectionString);
        return dialog.SavedProfileId ?? profileId;
    }
}
