using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>One line of the scan: a foreign key that no index covers.</summary>
public sealed class UnindexedForeignKeyRow(UnindexedForeignKey key) : INotifyPropertyChanged
{
    public UnindexedForeignKey Key { get; } = key;
    public string Source => $"{Key.Key.Schema}.{Key.Key.Table} ({string.Join(", ", Key.Key.Columns)})";
    public string Target => $"{Key.Key.TargetSchema}.{Key.Key.TargetTable}";
    public string Size => FormatSize(Key.Key.TableBytes);
    public string EstimatedRows => Key.Key.EstimatedRows.ToString("N0", CultureInfo.CurrentCulture);
    public string ParentDeletes => Key.Key.ParentDeletes?.ToString("N0", CultureInfo.CurrentCulture) ?? "";
    public string ParentUpdates => Key.Key.ParentUpdates?.ToString("N0", CultureInfo.CurrentCulture) ?? "";
    public string NearIndexes => string.Join("; ", Key.NearIndexes);
    private bool selected;
    public bool Selected { get => selected; set { selected = value; Changed(nameof(Selected)); SelectionChanged?.Invoke(); } }
    private string result = key.Key.Partitioned ? "No index. Partitioned: created without CONCURRENTLY" : "No index";
    public string Result { get => result; internal set { result = value; Changed(nameof(Result)); } }
    internal Action? SelectionChanged { get; set; }
    internal bool IsCreated { get; private set; }
    internal void MarkCreated() { IsCreated = true; Result = "Created"; Selected = false; }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "kB", "MB", "GB", "TB"];
        double value = bytes; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>PGMS-039: foreign keys without an index, read from the catalog; the selected ones become a script reviewed before anything runs.</summary>
public partial class ForeignKeyIndexScanWindow : Window
{
    private readonly string connection;
    private readonly List<UnindexedForeignKeyRow> rows = [];
    private CancellationTokenSource? scan;
    private bool applying;
    /// <summary>Opens the script in a query tab.</summary>
    internal Func<string, Task>? OpenInQuery { get; set; }
    /// <summary>Replaces the review dialog in tests; returns true to run the script.</summary>
    internal Func<TableDesignChange, bool>? ReviewScript { get; set; }
    /// <summary>Set after indexes were created, so the host can refresh.</summary>
    public bool Created { get; private set; }
    internal IReadOnlyList<UnindexedForeignKeyRow> Rows => rows;
    internal bool CreateConcurrently { get => Concurrently.IsChecked == true; set { Concurrently.IsChecked = value; RefreshScript(); } }

    public ForeignKeyIndexScanWindow(string connection)
    {
        InitializeComponent();
        this.connection = connection;
    }

    private async void Scan(object sender, RoutedEventArgs e) => await ScanAsync();
    private void OnClosing(object? sender, CancelEventArgs e) => scan?.Cancel();
    private void ConcurrentlyChanged(object sender, RoutedEventArgs e) => RefreshScript();

    internal async Task ScanAsync()
    {
        if (scan is not null || applying) return;
        scan = new CancellationTokenSource();
        var token = scan.Token;
        ScanButton.IsEnabled = ApplyButton.IsEnabled = OpenScriptButton.IsEnabled = SelectAllButton.IsEnabled = false;
        rows.Clear(); Keys.ItemsSource = null; Script.Clear();
        ScanStatus.Text = "Reading the catalog…";
        try
        {
            var (keys, names) = await ForeignKeyIndexScan.ReadAsync(connection, token);
            rows.AddRange(ForeignKeyIndexScan.FindUnindexed(keys, names).Select(k => new UnindexedForeignKeyRow(k) { SelectionChanged = RefreshScript }));
            Keys.ItemsSource = rows.ToList();
            ScanStatus.Text = keys.Count == 0 ? "This database has no foreign keys."
                : rows.Count == 0 ? $"Every one of the {keys.Count:N0} foreign key(s) has an index."
                : $"{rows.Count:N0} of {keys.Count:N0} foreign key(s) have no index. Choose by table size and by how often the parent changes.";
        }
        catch (OperationCanceledException) { ScanStatus.Text = "Scan canceled."; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException or TimeoutException) { ScanStatus.Text = "Scan failed: " + (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message); }
        finally
        {
            scan.Dispose(); scan = null;
            ScanButton.IsEnabled = true;
            SelectAllButton.IsEnabled = rows.Count > 0;
            RefreshScript();
        }
    }

    private void SelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows.Where(r => !r.IsCreated)) row.Selected = true;
    }

    private List<UnindexedForeignKeyRow> Chosen() => rows.Where(r => r.Selected).ToList();

    private void RefreshScript()
    {
        var chosen = Chosen();
        Script.Text = string.Join(Environment.NewLine, ForeignKeyIndexScan.Statements(chosen.Select(r => r.Key), CreateConcurrently));
        ApplyButton.IsEnabled = OpenScriptButton.IsEnabled = scan is null && !applying && chosen.Count > 0;
    }

    private async void OpenScript(object sender, RoutedEventArgs e) { if (OpenInQuery is { } open && Script.Text.Length > 0) await open(Script.Text); }

    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();

    /// <summary>Always goes through the script review. Concurrently: one index at a time, stopping at the first failure. Otherwise: one transaction.</summary>
    internal async Task ApplyAsync()
    {
        var chosen = Chosen();
        if (chosen.Count == 0 || scan is not null || applying) return;
        bool concurrently = CreateConcurrently;
        var keys = chosen.Select(r => r.Key).ToList();
        var statements = ForeignKeyIndexScan.Statements(keys, concurrently);
        var warnings = new List<string>
        {
            concurrently
                ? "Indexes are created one at a time, outside a transaction, without blocking writes. If one fails, the ones before it stay and the failed one may be left invalid."
                : "All indexes are created in one transaction. Writes on each table are blocked while its index is built."
        };
        if (concurrently)
            warnings.AddRange(chosen.Where(r => r.Key.Key.Partitioned).Select(r => $"{r.Key.Key.Schema}.{r.Key.Key.Table} is partitioned: its index is created without CONCURRENTLY, and writes on its partitions are blocked meanwhile.").Distinct());
        var change = new TableDesignChange(statements, warnings);
        string intro = concurrently ? "Review the script. Apply runs one statement at a time, outside a transaction." : "Review the script. Apply runs it in one transaction.";
        if (!(ReviewScript?.Invoke(change) ?? new ChangeScriptDialog(change, canApply: true, intro) { Owner = this }.ShowDialog() == true)) return;
        applying = true;
        ScanButton.IsEnabled = SelectAllButton.IsEnabled = Concurrently.IsEnabled = ApplyButton.IsEnabled = OpenScriptButton.IsEnabled = false;
        ScanStatus.Text = "Creating the indexes…";
        try
        {
            var result = await ForeignKeyIndexScan.ApplyAsync(connection, keys, concurrently);
            // The statements run in script order, so the first ones are the ones that were created.
            var done = statements.Take(result.Created).ToHashSet(StringComparer.Ordinal);
            var failed = concurrently && result.Error is not null && result.Created < statements.Count ? statements[result.Created] : null;
            foreach (var row in chosen)
            {
                string statement = ForeignKeyIndexScan.Statement(row.Key, concurrently);
                if (done.Contains(statement)) row.MarkCreated();
                else if (statement == failed) row.Result = "Failed: " + result.Error;
            }
            if (result.Created > 0) Created = true;
            ScanStatus.Text = result.Error is null ? $"{result.Created:N0} index(es) created."
                : !concurrently ? "Nothing was created: " + result.Error
                : $"{result.Created:N0} index(es) created, then stopped: {result.Error}" + (result.InvalidIndex is { } invalid ? $" An invalid index was left; remove it with {ForeignKeyIndexScan.DropInvalid(result.InvalidIndexSchema!, invalid)}" : "");
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException or TimeoutException)
        {
            ScanStatus.Text = "Apply stopped: " + ex.Message + " Scan again to see what was created.";
        }
        finally
        {
            applying = false;
            ScanButton.IsEnabled = Concurrently.IsEnabled = true;
            SelectAllButton.IsEnabled = rows.Count > 0;
            RefreshScript();
        }
    }
}
