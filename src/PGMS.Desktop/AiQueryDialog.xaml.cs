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
        ContextInfo.Text = "Sent to the provider: your request, the structure of this database (schemas, tables, views, columns, types, keys, enum labels and comments)" + (selectedSql is null ? "" : " and, if checked, the selected SQL") + ". A database too large for one request is sent in part: the provider first picks the tables from a list of names. Table data is never read or sent.";
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
            int limit = settings.Validate().ContextCharacters;
            string? current = IncludeSelection.IsChecked == true ? selectedSql : null;
            // Words of the selected SQL count for choosing relations too: it usually names the tables the request is about.
            string wording = current is null ? Request.Text : Request.Text + "\n" + current;
            var catalog = await new SchemaContextService(connection).LoadAsync(token);
            IReadOnlyList<string>? chosen = null; string? selection = null;
            if (!catalog.Fits(limit))
            {
                // Too large to send whole: the provider picks the relations from their names before the real request.
                selection = AiQueryGenerator.ComposeSelection(Request.Text, catalog.NameList(wording, limit), current);
                SentContext = AiQueryGenerator.SelectionInstructions + "\n\n" + selection;
                Feedback.Text = $"The database does not fit in one request. Asking {settings.Provider} which of its {catalog.Relations:N0} tables and views are needed…";
                chosen = await AiQueryGenerator.SelectRelationsAsync(settings.Validate(), selection, token);
            }
            var schema = catalog.Build(wording, limit, chosen);
            string prompt = AiQueryGenerator.Compose(Request.Text, schema, current);
            SentContext = (selection is null ? "" : "-- First request: choosing the relations\n" + AiQueryGenerator.SelectionInstructions + "\n\n" + selection + "\n\n-- Second request: writing the SQL\n") + AiQueryGenerator.Instructions + "\n\n" + prompt;
            string omitted = schema.OmittedRelations == 0 ? "" : $"; {schema.OmittedRelations:N0} more without columns, {(schema.ListedByName == schema.OmittedRelations ? "all" : schema.ListedByName.ToString("N0"))} listed by name";
            string picked = selection is null ? "" : schema.ChosenRelations > 0 ? $" {settings.Provider} chose {schema.ChosenRelations:N0} of them from a list of names in a first request; the rest were ranked by the words of your request."
                : $" {settings.Provider} named no existing relation in a first request, so they were ranked by the words of your request.";
            ContextInfo.Text = $"Sent to {settings.Provider}: your request and the structure of {schema.Database} ({schema.Relations:N0} tables/views with {schema.Columns:N0} columns{omitted}; {prompt.Length / 1024.0:N1} KB){(current is not null ? " plus the selected SQL" : "")}.{picked} No table data.";
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
