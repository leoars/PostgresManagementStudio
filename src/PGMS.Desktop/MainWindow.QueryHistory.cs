using System.IO;
using System.Windows;
using Npgsql;

namespace PGMS.Desktop;

// Query History (PGMS-043, docs/MARCO-M1060.md): every execution of a query tab is recorded locally; View ▸ Query History… lists and reopens them.
public partial class MainWindow
{
    private QueryHistoryRecorder? queryHistory;
    private QueryHistoryWindow? historyWindow;
    /// <summary>Shared by every query tab of this window. Created at startup, which also applies the age and count limits once.</summary>
    internal QueryHistoryRecorder QueryHistory => queryHistory ??= CreateQueryHistory();
    internal QueryHistoryWindow? OpenHistoryWindow => historyWindow;

    private QueryHistoryRecorder CreateQueryHistory()
    {
        var recorder = new QueryHistoryRecorder(new QueryHistoryStore(QueryHistoryStore.DefaultDirectory), () => EnvironmentSettings.Load().History,
            (server, port) => QueryHistoryRecorder.ProfileExcludes(ConnectionProfileStore.DefaultPath, server, port));
        recorder.Warning += message => Dispatcher.BeginInvoke(() => Status.Text = message);
        _ = recorder.PruneAsync();
        return recorder;
    }

    private void AttachQueryHistory(QueryView view) => view.HistorySink = capture => _ = QueryHistory.RecordAsync(capture);

    private void ShowQueryHistory(object sender, RoutedEventArgs e) => ShowQueryHistory();
    /// <summary>One window per main window; asking again brings it to the front.</summary>
    internal QueryHistoryWindow ShowQueryHistory()
    {
        if (historyWindow is { } open) { open.Activate(); return open; }
        historyWindow = new QueryHistoryWindow(QueryHistory, async entry => await OpenHistoryEntryAsync(entry)) { Owner = this };
        historyWindow.Closed += (_, _) => historyWindow = null;
        historyWindow.Show();
        return historyWindow;
    }

    /// <summary>The SQL in a new tab, connected when PGMS already has a connection to that server, port and user (its database is switched to the entry's). Never executed.</summary>
    internal async Task<QueryView> OpenHistoryEntryAsync(QueryHistoryEntry entry)
    {
        var query = NewQuery(entry.ReopenText, origin: entry.Origin);
        if (HistoryConnection(entry) is not { } connection)
        {
            Status.Text = $"Opened from Query History without a connection: PGMS has no open connection to {entry.Server}:{entry.Port} as {entry.User}. Use Query ▸ Connection ▸ Connect….";
            return query;
        }
        try { await query.ConnectAsync(connection); Status.Text = $"Opened from Query History, connected to {entry.Server}:{entry.Port}/{entry.Database}. Nothing was executed."; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or OperationCanceledException or InvalidOperationException) { ShowError(ex); }
        return query;
    }

    /// <summary>An open connection (query tabs, then Object Explorer) to the entry's server, port and user, pointed at its database. The password stays in memory only.</summary>
    internal string? HistoryConnection(QueryHistoryEntry entry)
    {
        var candidates = documents.Keys.Select(q => q.ConnectionString).Append(explorerConnection).Append(catalogService?.ConnectionFor(null));
        foreach (string? candidate in candidates)
        {
            if (candidate is null) continue;
            NpgsqlConnectionStringBuilder c;
            try { c = new NpgsqlConnectionStringBuilder(candidate); } catch (ArgumentException) { continue; }
            if (!string.Equals(c.Host, entry.Server, StringComparison.OrdinalIgnoreCase) || c.Port != entry.Port || c.Username != entry.User) continue;
            c.Database = entry.Database;
            return c.ConnectionString;
        }
        return null;
    }
}
