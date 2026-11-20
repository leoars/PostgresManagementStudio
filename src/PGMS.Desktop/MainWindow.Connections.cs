using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace PGMS.Desktop;

/// <summary>Connection of the query tabs (PGMS-068, M1084): File ▸ New ▸ Database Engine Query, Open ▸ File… with the current connection,
/// Open ▸ File Disconnected… and Query ▸ Connection ▸ Disconnect All Queries.</summary>
public partial class MainWindow
{
    private IEnumerable<QueryView> OpenQueries() => OpenDocuments().Select(d => d.Content).OfType<QueryView>();

    private async void NewDatabaseEngineQuery(object sender, RoutedEventArgs e) => await NewDatabaseEngineQueryAsync();

    /// <summary>File ▸ New ▸ Database Engine Query: asks for the connection first; Cancel opens nothing.</summary>
    internal async Task<QueryView?> NewDatabaseEngineQueryAsync()
    {
        if (closeRequested || shuttingDown) return null;
        if (PickConnection() is not { } connection) { Status.Text = "No query opened."; return null; }
        var query = NewQuery();
        try { await query.ConnectAsync(connection); Status.Text = "Connected"; }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        RefreshCommands();
        return query;
    }

    private IReadOnlyList<string> PickSqlFiles()
    {
        if (PickFilesToOpen is { } pick) return pick();
        var dialog = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", Multiselect = true };
        if (OpenDialogDirectory() is { } folder) dialog.InitialDirectory = folder; // Environment ▸ Documents (M1141)
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }

    private async void Open(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        var files = PickSqlFiles();
        if (files.Count > 0) await OpenFilesAsync(files, connect: true);
    }

    private async void OpenFileDisconnected(object sender, RoutedEventArgs e)
    {
        var files = PickSqlFiles();
        if (files.Count > 0) await OpenFilesAsync(files, connect: false);
    }

    /// <summary>Open ▸ File… (<paramref name="connect"/>): each newly opened tab connects to the server of the active query or of Object Explorer,
    /// as SSMS uses the current connection; without one it stays disconnected, without asking. File Disconnected… never connects.
    /// A file already open is only activated, its connection unchanged. Nothing is executed.</summary>
    internal async Task OpenFilesAsync(IReadOnlyList<string> files, bool connect)
    {
        string? connection = connect ? ActiveQuery?.ConnectionString ?? SelectedConnection : null;
        int connected = 0;
        foreach (var file in files)
        {
            if (closeRequested || shuttingDown) break;
            var before = OpenQueries().ToHashSet();
            QueryView query;
            try { query = await OpenQueryFileAsync(file); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(ex); continue; }
            if (connection is null || before.Contains(query) || query.HasSession || query.IsBusy) continue;
            try { await query.ConnectAsync(connection); connected++; }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        }
        if (connected > 0) Status.Text = connected == 1 ? "Opened with the current connection. Nothing was executed." : $"Opened {connected:N0} files with the current connection. Nothing was executed.";
        else if (!connect) Status.Text = "Opened disconnected.";
        RefreshCommands();
    }

    private async void DisconnectAllQueries(object sender, RoutedEventArgs e) => await DisconnectAllQueriesAsync();

    /// <summary>Query ▸ Connection ▸ Disconnect All Queries: each connected tab in order, with the open-transaction question of Disconnect;
    /// Cancel there stops the rest. Busy tabs keep their connection. Object Explorer is not touched.</summary>
    internal async Task<int> DisconnectAllQueriesAsync()
    {
        int done = 0, busy = 0; bool stopped = false;
        foreach (var query in OpenQueries().Where(q => q.HasSession).ToList())
        {
            if (query.IsBusy) { busy++; continue; }
            if (!await ConfirmOpenTransactionAsync(query)) { stopped = true; break; }
            await query.DisconnectAsync();
            done++;
        }
        // Whole sentences for singular and plural, so each translates as one pattern (B1 review, M1139).
        Status.Text = (stopped ? "Disconnect All Queries stopped; " : "") + (done == 1 ? $"Disconnected {done:N0} query." : $"Disconnected {done:N0} queries.")
            + (busy == 1 ? $" {busy:N0} running query keeps its connection." : busy > 1 ? $" {busy:N0} running queries keep their connection." : "");
        RefreshCommands();
        return done;
    }
}
