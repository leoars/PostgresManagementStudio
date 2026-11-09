using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>A standard report as a document tab, like the SSMS 22.11 Reports ▸ Standard Reports (docs/MARCO-M1035.md). Read-only; F5 or Refresh reads it again.</summary>
public partial class ReportView : UserControl
{
    private readonly ServerReportService service;
    private readonly NpgsqlConnectionStringBuilder target;
    private CancellationTokenSource? reading;
    public ServerReportDefinition Definition { get; }
    public ServerReport? Report { get; private set; }
    public string Title { get; }

    public ReportView(string connection, ServerReportKind kind)
    {
        InitializeComponent();
        service = new(connection); target = new(connection); Definition = ServerReportService.Definition(kind);
        string where = Definition.DatabaseScope ? $"{target.Database} ({target.Host}:{target.Port})" : $"{target.Host}:{target.Port}";
        Title = $"{Definition.Title} - {where}";
        ReportTitle.Text = Definition.Title; ReportDescription.Text = Definition.Description;
        ReportTarget.Text = Definition.DatabaseScope ? $"Database {target.Database} on {target.Host}:{target.Port}" : $"Server {target.Host}:{target.Port}";
        AutomationProperties.SetName(this, Title);
    }

    /// <summary>Same report, server, port, user and (for database reports) database.</summary>
    public bool Targets(string connection, ServerReportKind kind)
    {
        var other = new NpgsqlConnectionStringBuilder(connection);
        return kind == Definition.Kind && string.Equals(other.Host, target.Host, StringComparison.OrdinalIgnoreCase) && other.Port == target.Port && other.Username == target.Username
            && (!Definition.DatabaseScope || other.Database == target.Database);
    }

    private bool loadedOnce;
    private async void OnLoaded(object sender, RoutedEventArgs e) { if (loadedOnce) return; loadedOnce = true; await LoadAsync(); }
    private async void Refresh(object sender, RoutedEventArgs e) => await LoadAsync();

    public async Task LoadAsync()
    {
        reading?.Cancel();
        using var cancellation = reading = new CancellationTokenSource();
        RefreshButton.IsEnabled = false; Feedback.Text = "Reading report…";
        try
        {
            var report = await service.RunAsync(Definition.Kind, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            Report = report; Show(report);
            Feedback.Text = $"Read at {report.ServerTime} (server time). Read-only.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException)
        { if (!cancellation.IsCancellationRequested) Feedback.Text = "The report could not be read: " + (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message); }
        finally
        {
            if (reading == cancellation) { reading = null; RefreshButton.IsEnabled = true; }
        }
    }

    /// <summary>Stops a read in progress when the tab closes.</summary>
    public void Shutdown() => reading?.Cancel();

    private void Show(ServerReport report)
    {
        Sections.Children.Clear();
        ReportTarget.Text = $"{(report.Definition.DatabaseScope ? "Database " : "Server ")}{report.Target} · {report.ServerTime}";
        foreach (var note in report.Notes) Sections.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var table in report.Tables)
        {
            Sections.Children.Add(new TextBlock { Text = table.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            if (table.Rows.Count == 0) { Sections.Children.Add(new TextBlock { Text = "No rows.", Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) }); continue; }
            Sections.Children.Add(Grid(table));
            if (table.IsTruncated) Sections.Children.Add(new TextBlock { Text = $"Only the first {ServerReportService.MaximumRows} rows are shown.", Margin = new Thickness(0, 2, 0, 0) });
        }
    }

    // Columns are bound by position (c0, c1, …) because report headers such as "Size (MB)" are not valid binding paths; typed values keep sorting numeric.
    private static DataGrid Grid(ServerReportTable table)
    {
        var data = new DataTable();
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var type = table.Rows.Select(r => r[i]?.GetType()).FirstOrDefault(t => t is not null) ?? typeof(string);
            data.Columns.Add("c" + i, type);
        }
        foreach (var row in table.Rows) data.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended,
            CanUserAddRows = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, ItemsSource = data.DefaultView
        };
        AutomationProperties.SetName(grid, table.Title);
        var right = new Style(typeof(TextBlock)); right.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        var trimmed = new Style(typeof(TextBlock)); trimmed.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); trimmed.Setters.Add(new Setter(ToolTipProperty, new Binding("Text") { RelativeSource = RelativeSource.Self }));
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var type = data.Columns[i].DataType;
            bool numeric = type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(decimal) || type == typeof(double) || type == typeof(float);
            bool wide = table.Columns[i] is "Query" || table.Columns[i].Contains("statement", StringComparison.OrdinalIgnoreCase);
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = table.Columns[i], Binding = new Binding("c" + i), ElementStyle = numeric ? right : trimmed,
                Width = wide ? new DataGridLength(420) : DataGridLength.Auto, MaxWidth = wide ? 900 : 360
            });
        }
        return grid;
    }
}
