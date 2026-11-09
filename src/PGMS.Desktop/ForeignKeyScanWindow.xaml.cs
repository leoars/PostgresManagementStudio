using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>One line of the scan: a candidate and what its rows said.</summary>
public sealed class ForeignKeyCandidateRow(ForeignKeyCandidate candidate) : INotifyPropertyChanged
{
    public ForeignKeyCandidate Candidate { get; } = candidate;
    public string Source => $"{Candidate.SourceSchema}.{Candidate.SourceTable}.{Candidate.SourceColumn}";
    public string Target => $"{Candidate.TargetSchema}.{Candidate.TargetTable}.{Candidate.TargetColumn}";
    private bool selected;
    public bool Selected { get => selected; set { selected = value; Changed(nameof(Selected)); SelectionChanged?.Invoke(); } }
    public ForeignKeyDataCheck? Check { get; private set; }
    public string Rows => Check is { State: ForeignKeyDataState.Matches or ForeignKeyDataState.Orphans or ForeignKeyDataState.Empty } c ? c.NonNull.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string Orphans => Check is { State: ForeignKeyDataState.Matches or ForeignKeyDataState.Orphans or ForeignKeyDataState.Empty } c ? c.Orphans.ToString("N0", CultureInfo.CurrentCulture) : "";
    private string result = "Waiting";
    public string Result { get => result; private set { result = value; Changed(nameof(Result)); } }
    internal Action? SelectionChanged { get; set; }
    /// <summary>A key with rows that have no match can only be created NOT VALID.</summary>
    public bool NeedsNotValid => Check is { State: ForeignKeyDataState.Orphans };

    internal void Show(ForeignKeyDataCheck check)
    {
        Check = check;
        Changed(nameof(Rows)); Changed(nameof(Orphans));
        Result = check.State switch
        {
            ForeignKeyDataState.Matches => "Every value has a match",
            ForeignKeyDataState.Orphans => "Rows without a match: NOT VALID",
            ForeignKeyDataState.Empty => "No values to compare",
            _ => check.Message ?? "Not checked"
        };
    }
    internal void MarkCreated() { Result = "Created"; Selected = false; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>PGMS-037: candidates by name and type, confirmed in the rows; the selected ones become a script reviewed before anything runs.</summary>
public partial class ForeignKeyScanWindow : Window
{
    private const int CheckSeconds = 30;
    private readonly string connection;
    private readonly List<ForeignKeyCandidateRow> rows = [];
    private CancellationTokenSource? scan;
    /// <summary>Opens the script in a query tab.</summary>
    internal Func<string, Task>? OpenInQuery { get; set; }
    /// <summary>Replaces the review dialog in tests; returns true to run the script.</summary>
    internal Func<TableDesignChange, bool>? ReviewScript { get; set; }
    /// <summary>Set after keys were created, so the host can refresh.</summary>
    public bool Created { get; private set; }
    internal IReadOnlyList<ForeignKeyCandidateRow> Rows => rows;

    public ForeignKeyScanWindow(string connection)
    {
        InitializeComponent();
        this.connection = connection;
    }

    private async void Scan(object sender, RoutedEventArgs e) => await ScanAsync();
    private void CancelScan(object sender, RoutedEventArgs e) => scan?.Cancel();
    private void OnClosing(object? sender, CancelEventArgs e) => scan?.Cancel();

    internal async Task ScanAsync()
    {
        if (scan is not null) return;
        scan = new CancellationTokenSource();
        var token = scan.Token;
        ScanButton.IsEnabled = ApplyButton.IsEnabled = OpenScriptButton.IsEnabled = SelectMatchesButton.IsEnabled = false;
        CancelScanButton.IsEnabled = true;
        rows.Clear(); Candidates.ItemsSource = null; Script.Clear();
        ScanStatus.Text = "Reading the catalog…";
        try
        {
            var tables = await ForeignKeyScan.ReadTablesAsync(connection, token);
            rows.AddRange(ForeignKeyScan.FindCandidates(tables).Select(c => new ForeignKeyCandidateRow(c) { SelectionChanged = RefreshScript }));
            Candidates.ItemsSource = rows.ToList();
            await using var reader = new NpgsqlConnection(connection);
            await reader.OpenAsync(token);
            for (int i = 0; i < rows.Count; i++)
            {
                ScanStatus.Text = $"Checking rows {i + 1:N0} of {rows.Count:N0}…";
                rows[i].Show(await ForeignKeyScan.CheckDataAsync(reader, rows[i].Candidate, CheckSeconds, token));
            }
            int matches = rows.Count(r => r.Check?.State == ForeignKeyDataState.Matches);
            ScanStatus.Text = rows.Count == 0 ? "No candidates found." : $"{rows.Count:N0} candidate(s), {matches:N0} with every value matched. Review each one: a matching name is a hint, not proof.";
        }
        catch (OperationCanceledException) { ScanStatus.Text = "Scan canceled. Rows not reached stay unchecked."; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException or TimeoutException) { ScanStatus.Text = "Scan failed: " + (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message); }
        finally
        {
            scan.Dispose(); scan = null;
            ScanButton.IsEnabled = true; CancelScanButton.IsEnabled = false;
            SelectMatchesButton.IsEnabled = rows.Count > 0;
            RefreshScript();
        }
    }

    private void SelectMatches(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows.Where(r => r.Check?.State == ForeignKeyDataState.Matches)) row.Selected = true;
    }

    private List<ForeignKeyCandidateRow> Chosen() => rows.Where(r => r.Selected).ToList();

    private void RefreshScript()
    {
        var chosen = Chosen();
        Script.Text = string.Join(Environment.NewLine, chosen.Select(r => ForeignKeyScan.Statement(r.Candidate, r.NeedsNotValid)));
        ApplyButton.IsEnabled = OpenScriptButton.IsEnabled = scan is null && chosen.Count > 0;
    }

    private async void OpenScript(object sender, RoutedEventArgs e) { if (OpenInQuery is { } open && Script.Text.Length > 0) await open(Script.Text); }

    private async void Apply(object sender, RoutedEventArgs e) => await ApplyAsync();

    /// <summary>Always goes through the script review; the keys are created in one transaction.</summary>
    internal async Task ApplyAsync()
    {
        var chosen = Chosen();
        if (chosen.Count == 0 || scan is not null) return;
        var change = new TableDesignChange(chosen.Select(r => ForeignKeyScan.Statement(r.Candidate, r.NeedsNotValid)).ToList(),
            chosen.Where(r => r.NeedsNotValid).Select(r => $"{r.Source}: {r.Check!.Orphans:N0} row(s) have no match. The key is created NOT VALID: new rows are checked, existing ones are not.").ToList());
        if (!(ReviewScript?.Invoke(change) ?? new ChangeScriptDialog(change, canApply: true) { Owner = this }.ShowDialog() == true)) return;
        ApplyButton.IsEnabled = false;
        ScanStatus.Text = "Creating the keys…";
        try
        {
            await ForeignKeyScan.ApplyAsync(connection, change.Statements);
            foreach (var row in chosen) row.MarkCreated();
            Created = true;
            ScanStatus.Text = $"{chosen.Count:N0} foreign key(s) created.";
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or InvalidOperationException or TimeoutException)
        {
            ScanStatus.Text = "Nothing was created: " + (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message);
            RefreshScript();
        }
    }
}
