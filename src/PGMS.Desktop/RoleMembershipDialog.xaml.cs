using System.ComponentModel;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class RoleMembershipDialog : Window
{
    private readonly string connection;
    private bool ready, busy;
    public string? ScriptSql { get; private set; }
    public RoleMembershipDialog(string connection)
    {
        this.connection = connection; InitializeComponent();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Role Memberships\nServer: {c.Host}:{c.Port}   Connected role: {c.Username}";
        OptionChoice.ItemsSource = new object[] { "Membership only", MembershipOption.Admin, MembershipOption.Inherit, MembershipOption.Set };
        OptionChoice.SelectedIndex = 0;
        ready = true; Update();
    }
    private RoleMembershipEdit Request => new(GrantedRole.Text, MemberRole.Text, Revoke.IsChecked == true, OptionChoice.SelectedItem is MembershipOption option ? option : null, OptionEnabled.IsChecked == true);
    private void Changed(object sender, RoutedEventArgs e) { if (ready) { Inspection.Clear(); Update(); } }
    private void Update()
    {
        bool selected=OptionChoice.SelectedItem is MembershipOption;
        OptionEnabled.IsEnabled=selected && Revoke.IsChecked != true;
        Revoke.Content=selected ? "Revo_ke selected option only (RESTRICT)" : "Revo_ke direct membership (RESTRICT)";
        try { Preview.Text = Request.ToSql(); Feedback.Text = ""; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
    }
    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !value && Preview.Text.Length > 0; }
    private async void Inspect(object sender, RoutedEventArgs e) => await InspectAsync();
    public async Task InspectAsync()
    {
        if (busy) return;
        SetBusy(true); Inspection.Clear(); Feedback.Text = "Loading…";
        try { Inspection.Text = await new RoleMembershipService(connection).InspectAsync(MemberRole.Text); Feedback.Text = "Direct membership snapshot loaded."; }
        catch (Exception ex) when (Handled(ex)) { Feedback.Text = Describe(ex); }
        finally { SetBusy(false); }
    }
    private void Script(object sender, RoutedEventArgs e) { if(busy || !ScriptButton.IsEnabled)return; ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy || !ApplyButton.IsEnabled) return;
        var request = Request; SetBusy(true); Feedback.Text = "Applying…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request); Inspection.Clear(); Feedback.Text = "Membership command completed. Inspect to refresh; effective access may use other paths."; }
        catch (Exception ex) when (Handled(ex)) { Feedback.Text = Describe(ex); }
        finally { SetBusy(false); }
    }
    private static bool Handled(Exception ex) => ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException;
    private static string Describe(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
