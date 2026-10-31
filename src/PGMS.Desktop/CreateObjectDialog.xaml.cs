using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class CreateObjectDialog : Window
{
    private readonly CreateObjectKind kind;
    private readonly string connectionString;
    private bool busy;
    public string? ScriptSql { get; private set; }
    public bool WasCreated { get; private set; }
    public CreateObjectDialog(CreateObjectKind kind, string connectionString)
    {
        this.kind = kind; this.connectionString = connectionString;
        InitializeComponent();
        WindowWorkArea.Attach(this);
        Title = Heading.Text = kind == CreateObjectKind.Database ? "New Database" : "New Schema";
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        Target.Text = $"Server: {settings.Host}:{settings.Port}\nDatabase: {settings.Database}  |  Role: {settings.Username}";
        UpdatePreview();
        Loaded += (_, _) => ObjectName.Focus();
    }
    private CreateObjectRequest Request => new(kind, ObjectName.Text, OwnerName.Text);
    private void InputChanged(object sender, TextChangedEventArgs e) { if (SqlPreview is not null) UpdatePreview(); }
    private void UpdatePreview()
    {
        try
        {
            SqlPreview.Text = Request.ToSql();
            Feedback.Text = "";
            CreateButton.IsEnabled = ScriptButton.IsEnabled = !busy;
        }
        catch (ArgumentException ex)
        {
            SqlPreview.Clear(); Feedback.Text = ex.Message;
            CreateButton.IsEnabled = ScriptButton.IsEnabled = false;
        }
    }
    private void Script(object sender, RoutedEventArgs e) { ScriptSql = Request.ToSql(); DialogResult = true; }
    private async void Create(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var request = Request;
        busy = true;
        Fields.IsEnabled = CreateButton.IsEnabled = ScriptButton.IsEnabled = CancelButton.IsEnabled = false;
        Progress.Text = "Creating…"; Feedback.Text = "";
        try
        {
            await new AdministrationService(connectionString).CreateAsync(request);
            WasCreated = true;
            busy = false;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException)
        {
            Feedback.Text = ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : $"{ex.Message}\nCheck Object Explorer before retrying; the server may have completed the operation.";
            Progress.Text = "Creation did not complete normally";
        }
        finally
        {
            busy = false;
            Fields.IsEnabled = CancelButton.IsEnabled = true;
            CreateButton.IsEnabled = ScriptButton.IsEnabled = true;
        }
    }
    private void DialogClosing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
