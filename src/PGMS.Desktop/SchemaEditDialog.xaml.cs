using System.ComponentModel;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class SchemaEditDialog : Window
{
    private readonly string connection;
    private bool ready, busy;
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }
    public SchemaEditDialog(string connection, string? schema = null)
    {
        this.connection = connection; InitializeComponent();
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Schema Operations\nServer: {c.Host}:{c.Port}   Database: {c.Database}   User: {c.Username}";
        Operation.ItemsSource = Enum.GetValues<SchemaOperation>(); ready = true; Operation.SelectedIndex = 0;
        SchemaName.Text = schema ?? "";
    }
    private SchemaEdit Request => new(SchemaName.Text, (SchemaOperation)Operation.SelectedItem, NewName.Text);
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        NewName.IsEnabled = (SchemaOperation)Operation.SelectedItem == SchemaOperation.Rename;
        try { Preview.Text = Request.ToSql(); Feedback.Text = ""; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
    }
    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !value && Preview.Text.Length > 0; }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return;
        var request = Request;
        if (request.Operation == SchemaOperation.Drop)
        {
            var c = new NpgsqlConnectionStringBuilder(connection);
            if (MessageBox.Show(this, $"Drop schema {request.Name}?\nServer: {c.Host}:{c.Port}\nDatabase: {c.Database}\n\n{request.ToSql()}\n\nThis removes the schema. PostgreSQL will refuse if it contains objects.", "Drop Schema", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        SetBusy(true); Feedback.Text = "Applying…";
        try { await new AdministrativeEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
