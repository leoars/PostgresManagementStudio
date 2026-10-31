using System.ComponentModel;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class RoleAttributeDialog : Window
{
    private readonly AdministrativeEditService service;
    private bool ready, busy;
    public string? ScriptSql { get; private set; }
    public RoleAttributeDialog(string connection)
    {
        InitializeComponent(); service = new(connection);
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Role Properties\nServer: {c.Host}:{c.Port}   Connected role: {c.Username}";
        Attribute.ItemsSource = Enum.GetValues<RoleAttribute>(); ready = true; Attribute.SelectedIndex = 0;
    }
    private RoleAttributeEdit Request => new(RoleName.Text, (RoleAttribute)Attribute.SelectedItem, AttributeValue.Text);
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        Inspection.Clear();
        try { Preview.Text = Request.ToSql(); Feedback.Text = ""; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
    }
    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !value && Preview.Text.Length > 0; }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy) return;
        SetBusy(true); Inspection.Clear(); Feedback.Text = "Loading…";
        try { Inspection.Text = await service.InspectRoleAsync(RoleName.Text); Feedback.Text = "Current attributes loaded. They are not copied into the change request."; }
        catch (Exception ex) when (Handled(ex)) { Feedback.Text = Describe(ex); }
        finally { SetBusy(false); }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var request = Request; SetBusy(true); Feedback.Text = "Applying…";
        try { await service.ApplyAsync(request); Inspection.Clear(); Feedback.Text = "Attribute changed. Inspect to refresh the current state."; }
        catch (Exception ex) when (Handled(ex)) { Feedback.Text = Describe(ex); }
        finally { SetBusy(false); }
    }
    private static bool Handled(Exception ex) => ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException;
    private static string Describe(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
