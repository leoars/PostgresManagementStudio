using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>A row of the Query History grid: a distinct query (newest execution) or, under an expanded query, one of its executions.</summary>
public sealed class QueryHistoryRow(QueryHistoryGroup group, QueryHistoryEntry entry, bool isGroup, bool expanded)
{
    public QueryHistoryGroup Group { get; } = group;
    public QueryHistoryEntry Entry { get; } = entry;
    public bool IsGroup { get; } = isGroup;
    public bool Expanded { get; } = expanded;
    public bool CanExpand => IsGroup && Group.Executions.Count > 1;
    public string Toggle => CanExpand ? (Expanded ? "−" : "+") : "";
    public Visibility ToggleVisibility => CanExpand ? Visibility.Visible : Visibility.Collapsed;
    public string ToggleName => Expanded ? "Hide executions" : "Show executions";
    public string PinMark => IsGroup && Group.Pinned ? "★" : "";
    public string When => Entry.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string Server => $"{Entry.Server}:{Entry.Port}";
    public string Database => Entry.Database;
    public string User => Entry.User;
    public string Executions => IsGroup ? Group.Executions.Count.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string Duration => QueryHistoryWindow.FormatDuration(Entry.DurationMilliseconds);
    public string Rows => Entry.RowsAffected is { } affected ? $"{affected:N0} affected" : Entry.RowsReturned is { } returned ? returned.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string Result => Entry.Outcome switch { QueryHistoryOutcome.Succeeded => "Succeeded", QueryHistoryOutcome.Canceled => "Canceled", _ => Entry.SqlState is { } state ? "Error " + state : "Error" };
    public string Origin => QueryHistoryWindow.OriginLabel(Entry.Origin);
    public string Sql => (IsGroup ? "" : "    ") + QueryHistoryText.Preview(Entry.Sql);
    public string AccessibleName => (IsGroup ? (Group.Pinned ? "Pinned query, " : "Query, ") + $"{Executions} run(s), last " : "Execution ") + $"{When}, {Server}/{Database}, {Result}: {QueryHistoryText.Preview(Entry.Sql, 80)}";
}

/// <summary>View ▸ Query History… (PGMS-043). Lists the executions recorded on this computer, one row per distinct query, and reopens them in a query tab.
/// Everything is read from and written to the local encrypted history through the recorder's queue; nothing here talks to a server.</summary>
public partial class QueryHistoryWindow : Window
{
    internal const string ClearQuestion = "Delete every execution in Query History, including pinned queries? This cannot be undone.";
    private readonly QueryHistoryRecorder recorder;
    private readonly Func<QueryHistoryEntry, Task> openInTab;
    private IReadOnlyList<QueryHistoryGroup> groups = [];
    private readonly HashSet<QueryHistoryKey> expanded = [];
    private bool busy;
    /// <summary>Writes the clipboard; tests replace it.</summary>
    internal Action<string> WriteClipboard { get; set; } = text => Clipboard.SetText(text, TextDataFormat.UnicodeText);
    /// <summary>Asks a yes/no question; tests replace it.</summary>
    internal Func<string, bool> Confirm { get; set; }
    /// <summary>Rows shown now, in order.</summary>
    internal IReadOnlyList<QueryHistoryRow> Rows => (IReadOnlyList<QueryHistoryRow>?)Entries.ItemsSource ?? [];
    internal Task Loading { get; private set; } = Task.CompletedTask;

    public QueryHistoryWindow(QueryHistoryRecorder recorder, Func<QueryHistoryEntry, Task> openInTab)
    {
        this.recorder = recorder; this.openInTab = openInTab;
        Confirm = question => MessageBox.Show(this, question, "Query History", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        InitializeComponent(); WindowWorkArea.Attach(this);
        Target.Text = $"Query History — executions recorded on this computer for the Windows user {Environment.UserName}.\nStored encrypted for this user (DPAPI) in {recorder.Store.DirectoryPath}; never sent to a server.";
        ConnectionFilter.Items.Add(new ComboBoxItem { Content = "All connections", Tag = null }); ConnectionFilter.SelectedIndex = 0;
        recorder.Recorded += OnRecorded;
        Closed += (_, _) => recorder.Recorded -= OnRecorded;
    }

    private void OnRecorded(QueryHistoryEntry _) => Dispatcher.BeginInvoke(() => { if (IsLoaded && !busy) Loading = RefreshAsync(); });
    private void OnLoaded(object sender, RoutedEventArgs e) => Loading = RefreshAsync();
    private void Refresh(object sender, RoutedEventArgs e) => Loading = RefreshAsync();

    public async Task RefreshAsync()
    {
        busy = true; RefreshButton.IsEnabled = false; Status.Text = "Reading Query History…";
        try
        {
            DisabledBanner.Visibility = recorder.CurrentSettings.Enabled ? Visibility.Collapsed : Visibility.Visible;
            var snapshot = await recorder.ReadAsync();
            groups = QueryHistoryGroup.Build(snapshot);
            FillConnections();
            Apply();
            int executions = groups.Sum(g => g.Executions.Count);
            Status.Text = $"{groups.Count:N0} distinct quer{(groups.Count == 1 ? "y" : "ies")}, {executions:N0} execution(s)." +
                (snapshot.UnreadableFiles > 0 ? $" {snapshot.UnreadableFiles} history file(s) could not be read (damaged, from another Windows user or renamed) and were left untouched; Clear History removes them." : "");
        }
        catch (Exception ex) when (QueryHistoryRecorder.IsDiskError(ex)) { Status.Text = "Query History could not be read. " + ex.Message; }
        finally { busy = false; RefreshButton.IsEnabled = true; UpdateButtons(); }
    }

    private static string ConnectionLabel(QueryHistoryKey key) => $"{key.Server}:{key.Port} / {key.Database}";
    private void FillConnections()
    {
        string? selected = (ConnectionFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        while (ConnectionFilter.Items.Count > 1) ConnectionFilter.Items.RemoveAt(1);
        foreach (string label in groups.Select(g => ConnectionLabel(g.Key)).Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
            ConnectionFilter.Items.Add(new ComboBoxItem { Content = label, Tag = label });
        ConnectionFilter.SelectedItem = ConnectionFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == selected) ?? ConnectionFilter.Items[0];
    }

    private void FilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded) Apply(); }

    /// <summary>Executions of the group that pass the period filter.</summary>
    private IReadOnlyList<QueryHistoryEntry> Visible(QueryHistoryGroup group)
    {
        int days = PeriodFilter.SelectedItem is ComboBoxItem { Tag: string tag } ? int.Parse(tag, CultureInfo.InvariantCulture) : 0;
        if (days == 0) return group.Executions;
        DateTime since = days == 1 ? DateTime.Today.ToUniversalTime() : DateTime.UtcNow.AddDays(-days);
        return group.Executions.Where(e => e.StartedUtc >= since).ToArray();
    }

    internal void Apply()
    {
        var keep = (Entries.SelectedItem as QueryHistoryRow) is { } current ? (current.Group.Key, current.Entry.Id) : default((QueryHistoryKey, Guid)?);
        string text = QueryHistoryText.Normalize(TextFilter.Text);
        string? connection = (ConnectionFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var rows = new List<QueryHistoryRow>();
        foreach (var group in groups)
        {
            if (PinnedOnly.IsChecked == true && !group.Pinned) continue;
            if (connection is not null && ConnectionLabel(group.Key) != connection) continue;
            if (text.Length > 0 && !QueryHistoryText.Normalize(group.Latest.Sql).Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !group.Key.Database.Contains(text, StringComparison.OrdinalIgnoreCase) && !group.Latest.User.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            var visible = Visible(group);
            if (visible.Count == 0) continue;
            bool open = expanded.Contains(group.Key) && group.Executions.Count > 1;
            rows.Add(new(group, visible[0], true, open));
            if (open) rows.AddRange(visible.Select(e => new QueryHistoryRow(group, e, false, false)));
        }
        Entries.ItemsSource = rows;
        if (keep is { } k) Entries.SelectedItem = rows.FirstOrDefault(r => r.Group.Key == k.Item1 && r.Entry.Id == k.Item2) ?? rows.FirstOrDefault(r => r.IsGroup && r.Group.Key == k.Item1);
        if (Entries.SelectedItem is null && rows.Count > 0) Entries.SelectedIndex = 0;
        ShowSelection();
    }

    internal QueryHistoryRow? Selected => Entries.SelectedItem as QueryHistoryRow;

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelection();
    private void ShowSelection()
    {
        UpdateButtons();
        if (Selected is not { } row) { SqlText.Text = ""; Details.Text = ""; return; }
        var entry = row.Entry;
        SqlText.Text = entry.Sql;
        string result = entry.Outcome switch
        {
            QueryHistoryOutcome.Succeeded => "Succeeded",
            QueryHistoryOutcome.Canceled => "Canceled",
            _ => "Error" + (entry.SqlState is { } state ? " " + state : "") + (entry.Error is { } message ? ": " + message : "")
        };
        Details.Text = $"{(row.IsGroup ? $"Last of {row.Group.Executions.Count:N0} run(s)" : "Execution")}: started {row.When}, {row.Duration}, {result}. " +
            $"{row.Server}/{entry.Database} as {entry.User}. Origin: {row.Origin}." + (row.Rows.Length > 0 ? $" Rows: {row.Rows}." : "") +
            (entry.Truncated ? $" The SQL was cut at {entry.Sql.Length:N0} of {entry.OriginalLength:N0} characters when it was recorded." : "");
    }

    private void UpdateButtons()
    {
        bool any = Selected is not null && !busy;
        OpenButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = PinButton.IsEnabled = any;
        PinButton.Content = Selected?.Group.Pinned == true ? "Un_pin" : "_Pin";
        ClearButton.IsEnabled = !busy;
    }

    private void ToggleRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueryHistoryRow { CanExpand: true } row) { Entries.SelectedItem = row; SetExpanded(row, !row.Expanded); }
    }
    internal void SetExpanded(QueryHistoryRow row, bool open)
    {
        if (!row.CanExpand && open) return;
        if (open) expanded.Add(row.Group.Key); else expanded.Remove(row.Group.Key);
        Entries.SelectedItem = row;
        var key = row.Group.Key;
        Apply();
        Entries.SelectedItem = Rows.FirstOrDefault(r => r.IsGroup && r.Group.Key == key);
        if (Entries.SelectedItem is { } item) Entries.ScrollIntoView(item);
    }

    private void EntriesKeyDown(object sender, KeyEventArgs e)
    {
        if (Selected is not { } row) return;
        switch (e.Key)
        {
            case Key.Right when row.IsGroup && row.CanExpand && !row.Expanded: SetExpanded(row, true); e.Handled = true; break;
            case Key.Left when row.IsGroup && row.Expanded: SetExpanded(row, false); e.Handled = true; break;
            case Key.Left when !row.IsGroup: SetExpanded(Rows.First(r => r.IsGroup && r.Group.Key == row.Group.Key), false); e.Handled = true; break;
            case Key.Enter: _ = OpenAsync(); e.Handled = true; break;
            case Key.Delete: _ = DeleteAsync(); e.Handled = true; break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control: CopySql(); e.Handled = true; break;
        }
    }
    private void EntriesDoubleClick(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is DependencyObject source && FindRow(source) is not null) _ = OpenAsync(); }
    private static DataGridRow? FindRow(DependencyObject? element)
    {
        while (element is not null and not DataGridRow) element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        return element as DataGridRow;
    }

    private void Open(object sender, RoutedEventArgs e) => _ = OpenAsync();
    internal async Task OpenAsync()
    {
        if (Selected is not { } row) return;
        try { await openInTab(row.Entry); Status.Text = "Opened in a new query tab. Nothing was executed."; }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException or System.IO.IOException) { Status.Text = "The query tab could not be opened. " + ex.Message; }
    }

    private void Copy(object sender, RoutedEventArgs e) => CopySql();
    internal void CopySql()
    {
        if (Selected is not { } row) return;
        try { WriteClipboard(row.Entry.ReopenText); Status.Text = $"Copied {row.Entry.Sql.Length:N0} character(s) of SQL" + (row.Entry.Truncated ? " (cut when recorded; the copy says so in a last comment line)." : "."); }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "Copy failed: Windows could not update the clipboard. Try again."; }
    }

    private void TogglePin(object sender, RoutedEventArgs e) => _ = TogglePinAsync();
    internal async Task TogglePinAsync()
    {
        if (Selected is not { } row || busy) return;
        bool pin = !row.Group.Pinned;
        try { await recorder.SetPinnedAsync(row.Group.Key, pin); await RefreshAsync(); Status.Text = pin ? "Pinned: listed first, and its newest execution is always kept." : "Unpinned."; }
        catch (Exception ex) when (QueryHistoryRecorder.IsDiskError(ex)) { Status.Text = "The pin was not saved. " + ex.Message; }
    }

    private void Delete(object sender, RoutedEventArgs e) => _ = DeleteAsync();
    internal async Task DeleteAsync()
    {
        if (Selected is not { } row || busy) return;
        var ids = row.IsGroup ? row.Group.Executions.Select(x => x.Id).ToArray() : [row.Entry.Id];
        string question = row.IsGroup ? $"Delete this query and its {ids.Length:N0} execution(s) from Query History?" : "Delete this execution from Query History?";
        if (!Confirm(question)) return;
        try
        {
            int removed = await recorder.DeleteAsync(ids);
            if (row.IsGroup && row.Group.Pinned) await recorder.SetPinnedAsync(row.Group.Key, false);
            await RefreshAsync();
            Status.Text = $"Deleted {removed:N0} execution(s).";
        }
        catch (Exception ex) when (QueryHistoryRecorder.IsDiskError(ex)) { Status.Text = "Nothing was deleted. " + ex.Message; }
    }

    private void Clear(object sender, RoutedEventArgs e) => _ = ClearAsync();
    internal async Task ClearAsync()
    {
        if (busy || !Confirm(ClearQuestion)) return;
        try { int removed = await recorder.ClearAsync(); expanded.Clear(); await RefreshAsync(); Status.Text = $"Query History cleared: {removed:N0} execution(s) removed."; }
        catch (Exception ex) when (QueryHistoryRecorder.IsDiskError(ex)) { Status.Text = "Query History was not cleared. " + ex.Message; }
    }

    internal static string FormatDuration(long milliseconds) => milliseconds < 1000 ? $"{milliseconds:N0} ms" :
        milliseconds < 60000 ? (milliseconds / 1000.0).ToString("0.000", CultureInfo.CurrentCulture) + " s" : TimeSpan.FromMilliseconds(milliseconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
    internal static string OriginLabel(QueryHistoryOrigin origin) => origin switch
    {
        QueryHistoryOrigin.QueryDesigner => "Query Designer", QueryHistoryOrigin.Ai => "AI", QueryHistoryOrigin.Other => "Other", _ => "Typed"
    };
}
