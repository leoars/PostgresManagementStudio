using System.ComponentModel;
using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class RoleRenameDialog : Window
{
    private readonly string connection; private bool ready, busy;
    public string? ScriptSql { get; private set; } public bool WasApplied { get; private set; }
    public RoleRenameDialog(string connection, string? role = null)
    {
        this.connection = connection; InitializeComponent(); var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Role Operations\nServer: {c.Host}:{c.Port}   Connected role: {c.Username}"; RoleName.Text = role ?? ""; ready = true; Update();
    }
    private RoleRenameEdit Request => new(RoleName.Text, NewName.Text);
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    private void Update() { try { Preview.Text = Request.ToSql(); Feedback.Text = "Rename changes the role identifier; saved connection profiles are not updated."; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; } catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; } }
    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; Update(); }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return; var request = Request; var c = new NpgsqlConnectionStringBuilder(connection);
        if (MessageBox.Show(this, $"Rename role {request.Name} to {request.NewName}?\nServer: {c.Host}:{c.Port}\n\n{request.ToSql()}", "Rename Role", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); Feedback.Text = "Applying…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
