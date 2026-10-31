using System.ComponentModel;
using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class DatabaseEditDialog : Window
{
    private readonly string connection;
    private bool ready, busy;
    public string? ScriptSql { get; private set; }
    public bool WasApplied { get; private set; }
    public DatabaseEditDialog(string connection, string? database = null)
    {
        this.connection = connection; InitializeComponent(); var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Database Operations\nServer: {c.Host}:{c.Port}   Maintenance database: postgres   User: {c.Username}";
        Operation.ItemsSource = Enum.GetValues<DatabaseOperation>(); Operation.SelectedItem = DatabaseOperation.Rename;
        DatabaseName.Text = database ?? ""; ready = true; Update();
    }
    private DatabaseEdit Request => new(DatabaseName.Text, (DatabaseOperation)Operation.SelectedItem, NewName.Text);
    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }
    private void Update()
    {
        bool drop = Operation.SelectedItem is DatabaseOperation.Drop; RenameFields.Visibility = drop ? Visibility.Collapsed : Visibility.Visible;
        try { Preview.Text = Request.ToSql(); Feedback.Text = drop ? "DROP DATABASE permanently removes the database. Active sessions cause PostgreSQL to refuse the operation." : "Rename requires no active sessions connected to the target database."; ScriptButton.IsEnabled = ApplyButton.IsEnabled = !busy; }
        catch (ArgumentException ex) { Preview.Clear(); Feedback.Text = ex.Message; ScriptButton.IsEnabled = ApplyButton.IsEnabled = false; }
    }
    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; Update(); }
    private async void Inspect(object sender, RoutedEventArgs e)
    {
        if (busy) return; SetBusy(true); Feedback.Text = "Reading server databases…";
        try { var names = await new DatabaseEditService(connection).ListAsync(); Feedback.Text = "Server databases: " + string.Join(", ", names); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();
    public async Task ApplyAsync()
    {
        if (busy) return; var request = Request;
        var c = new NpgsqlConnectionStringBuilder(connection);
        string warning = request.Operation == DatabaseOperation.Drop
            ? $"Permanently drop database {request.Name}? All data in it will be removed.\n\nServer: {c.Host}:{c.Port}\n\n{request.ToSql()}"
            : $"Rename database {request.Name} to {request.NewName}?\n\nServer: {c.Host}:{c.Port}\n\n{request.ToSql()}";
        if (MessageBox.Show(this, warning, request.Operation == DatabaseOperation.Drop ? "Drop Database" : "Rename Database", MessageBoxButton.YesNo,
            request.Operation == DatabaseOperation.Drop ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); Feedback.Text = "Applying outside a transaction…";
        try { await new DatabaseEditService(connection).ApplyAsync(request); WasApplied = true; busy = false; DialogResult = true; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { SetBusy(false); }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
