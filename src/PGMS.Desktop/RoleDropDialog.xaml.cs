using System.ComponentModel;
using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class RoleDropDialog : Window
{
    private readonly string connection; private bool ready, busy;
    public string? ScriptSql { get; private set; } public bool WasApplied { get; private set; }
    public RoleDropDialog(string connection, string? role = null)
    {
        this.connection = connection; InitializeComponent(); var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Role Operations\nServer: {c.Host}:{c.Port}   Connected role: {c.Username}"; RoleName.Text = role ?? ""; ready = true; Update();
    }
    private RoleDropEdit Request => new(RoleName.Text);
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    private void Update() { try { Preview.Text = Request.ToSql(); Feedback.Text = "No CASCADE or automatic ownership/privilege removal is used."; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; } catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; } }
    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; Update(); }
    private async void Inspect(object sender, RoutedEventArgs e)
    {
        if (busy) return; SetBusy(true); Feedback.Text = "Loading roles…";
        try { var roles = await new RoleAdministrationService(connection).ListRolesAsync(); Feedback.Text = roles.Contains(RoleName.Text, StringComparer.Ordinal) ? "Role exists. Inspect dependencies before dropping." : "Role was not found in the catalog."; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return; var request = Request; var c = new NpgsqlConnectionStringBuilder(connection);
        if (MessageBox.Show(this, $"Drop role {request.Name}? No owned objects or grants will be removed automatically.\nServer: {c.Host}:{c.Port}\n\n{request.ToSql()}", "Drop Role", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); Feedback.Text = "Applying…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
