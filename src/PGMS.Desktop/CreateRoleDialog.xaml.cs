using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class CreateRoleDialog : Window
{
    private readonly string connectionString;
    private bool busy;
    private bool initialized;
    public string? ScriptSql { get; private set; }
    public bool WasCreated { get; private set; }

    public CreateRoleDialog(string connectionString)
    {
        this.connectionString = connectionString;
        InitializeComponent();
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        Target.Text = $"Server: {settings.Host}:{settings.Port}\nConnected role: {settings.Username}";
        Loaded += DialogLoaded;
        initialized = true;
        UpdatePreview();
    }

    private async void DialogLoaded(object sender, RoutedEventArgs e)
    {
        RoleName.Focus(); Progress.Text = "Loading available roles…";
        try
        {
            foreach (string role in await new RoleAdministrationService(connectionString).ListRolesAsync())
            {
                var check = new CheckBox { Content = role, Margin = new Thickness(6, 3, 6, 3) };
                check.Checked += InputChanged; check.Unchecked += InputChanged;
                Memberships.Items.Add(check);
            }
            Progress.Text = "";
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException)
        {
            Progress.Text = "Membership list unavailable";
            Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
        }
        UpdatePreview(clearFeedback: false);
    }

    private CreateRoleRequest Request
    {
        get
        {
            if (!int.TryParse(ConnectionLimit.Text, out int limit)) throw new ArgumentException("Connection limit must be a whole number (-1 means no limit).");
            string[] memberships = Memberships.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToArray();
            return new(RoleName.Text, CanLogin.IsChecked == true, Inherit.IsChecked == true,
                CreateDatabase.IsChecked == true, CreateRole.IsChecked == true, Replication.IsChecked == true,
                BypassRls.IsChecked == true, limit, memberships);
        }
    }

    private void InputChanged(object sender, RoutedEventArgs e) { if (initialized) UpdatePreview(); }
    private void UpdatePreview(bool clearFeedback = true)
    {
        try
        {
            SqlPreview.Text = Request.ToSqlScript();
            if (clearFeedback) Feedback.Text = "";
            CreateButton.IsEnabled = ScriptButton.IsEnabled = !busy;
        }
        catch (ArgumentException ex)
        {
            SqlPreview.Clear();
            if (clearFeedback || string.IsNullOrEmpty(Feedback.Text)) Feedback.Text = ex.Message;
            CreateButton.IsEnabled = ScriptButton.IsEnabled = false;
        }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSqlScript(); DialogResult = true; }
    private async void Create(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        CreateRoleRequest request = Request;
        busy = true; Fields.IsEnabled = Memberships.IsEnabled = CreateButton.IsEnabled = ScriptButton.IsEnabled = CancelButton.IsEnabled = false;
        Progress.Text = "Creating role and memberships…"; Feedback.Text = "";
        try { await new RoleAdministrationService(connectionString).CreateAsync(request); WasCreated = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException)
        {
            Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : $"{ex.Message}\nCheck Object Explorer before retrying.";
            Progress.Text = "Creation did not complete normally";
        }
        finally { busy = false; Fields.IsEnabled = Memberships.IsEnabled = CancelButton.IsEnabled = true; UpdatePreview(clearFeedback: false); }
    }
    private void DialogClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
