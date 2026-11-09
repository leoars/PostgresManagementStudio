using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class AiQueryDialog : Window
{
    private readonly AiProviderSettings settings;
    private readonly string connection;
    private readonly string? selectedSql;
    private readonly Action<string> insert;
    private CancellationTokenSource? cancellation;
    private bool ready;
    public string? SentContext { get; private set; }
    public AiQueryResult? Generated { get; private set; }
    public bool WasInserted { get; private set; }

    /// <param name="insert">Puts the reviewed SQL into the editor; called only by Insert into Editor.</param>
    public AiQueryDialog(AiProviderSettings settings, string connection, string? selectedSql, Action<string> insert)
    {
        InitializeComponent();
        this.settings = settings; this.connection = connection; this.selectedSql = selectedSql; this.insert = insert;
        var c = new NpgsqlConnectionStringBuilder(connection);
        Target.Text = $"Generate SQL with AI\nProvider: {settings.Provider} ({(settings.Model.Length > 0 ? settings.Model : "no model chosen")})   Server: {c.Host}:{c.Port}   Database: {c.Database}";
        IncludeSelection.Content = selectedSql is null ? "Send the SQL selected in the editor (no selection)" : $"Send the SQL selected in the editor ({selectedSql.Length:N0} characters) so the request can refer to it";
        IncludeSelection.IsEnabled = selectedSql is not null; IncludeSelection.IsChecked = selectedSql is not null;
        ContextInfo.Text = "Sent to the provider: your request, the structure of this database (schemas, tables, views, columns, types, keys, enum labels and comments)" + (selectedSql is null ? "" : " and, if checked, the selected SQL") + ". Table data is never read or sent.";
        ready = true; Changed(this, new RoutedEventArgs());
        Loaded += (_, _) => Request.Focus();
    }

    private bool Busy => cancellation is not null;
    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        GenerateButton.IsEnabled = !Busy && Request.Text.Trim().Length > 0;
        InsertButton.IsEnabled = CopyButton.IsEnabled = !Busy && Generated is not null;
        ContextButton.IsEnabled = !Busy && SentContext is not null;
    }
    private void SetBusy(bool value)
    {
        Fields.IsEnabled = !value; CloseButton.IsEnabled = !value;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Changed(this, new RoutedEventArgs());
    }

    private async void Generate(object sender, RoutedEventArgs e) => await GenerateAsync();
    public async Task GenerateAsync()
    {
        if (Busy || Request.Text.Trim().Length == 0) return;
        cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        SetBusy(true); Feedback.Foreground = Brushes.DimGray; Feedback.Text = "Reading the database structure…";
        string message; bool failed = false;
        try
        {
            var schema = await new SchemaContextService(connection).BuildAsync(Request.Text, token: token);
            string prompt = AiQueryGenerator.Compose(Request.Text, schema, IncludeSelection.IsChecked == true ? selectedSql : null);
            SentContext = AiQueryGenerator.Instructions + "\n\n" + prompt;
            ContextInfo.Text = $"Sent to {settings.Provider}: your request and the structure of {schema.Database} ({schema.Relations:N0} tables/views, {schema.Columns:N0} columns{(schema.OmittedRelations > 0 ? $", {schema.OmittedRelations:N0} more listed by name only" : "")}, {prompt.Length / 1024.0:N1} KB){(IncludeSelection.IsChecked == true && selectedSql is not null ? " plus the selected SQL" : "")}. No table data.";
            Feedback.Text = $"Waiting for {settings.Provider}…";
            Generated = await AiQueryGenerator.GenerateAsync(settings, prompt, token);
            Result.Text = Generated.Sql;
            message = "Review the SQL, then choose Insert into Editor.";
        }
        catch (OperationCanceledException) { message = "Canceled. Nothing was inserted."; }
        catch (Exception ex) when (ex is AiProviderException or NpgsqlException or IOException or TimeoutException or System.Security.Cryptography.CryptographicException)
        { failed = true; message = ex is PostgresException pg ? $"Could not read the database structure - {pg.SqlState}: {pg.MessageText}" : ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
        Feedback.Foreground = failed ? Brushes.Firebrick : Brushes.DimGray; Feedback.Text = message;
    }

    private void CancelGeneration(object sender, RoutedEventArgs e) { cancellation?.Cancel(); Feedback.Text = "Canceling…"; }

    private void Insert(object sender, RoutedEventArgs e) => InsertResult();
    public void InsertResult()
    {
        if (Generated is null || Busy) return;
        insert($"-- Generated by AI ({Generated.Provider}, {Generated.Model}). Review before running.\n{Generated.Sql}");
        WasInserted = true;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); } // Not shown modally (tests).
    }

    private void Copy(object sender, RoutedEventArgs e) { if (Generated is not null) Clipboard.SetText(Generated.Sql, TextDataFormat.UnicodeText); Feedback.Text = "SQL copied."; }

    private void ViewContext(object sender, RoutedEventArgs e)
    {
        if (SentContext is null) return;
        new Window
        {
            Title = "Context sent to " + settings.Provider, Owner = this, Width = 760, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background,
            Content = new TextBox { Text = SentContext, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8), Margin = new Thickness(12) }
        }.ShowDialog();
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (Busy) { cancellation?.Cancel(); e.Cancel = true; } }
}
