using System.IO;
using System.Windows;

namespace PGMS.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<QueryView, Task<bool>> pendingTabCloses = [];
    private readonly HashSet<QueryView> acceptedTabCloses = [];
    private readonly HashSet<QueryView> closingQueries = [];
    public Task TabCloseCompletion => Task.WhenAll(pendingTabCloses.Values.ToArray());

    public Task<bool> CloseQueryAsync(QueryView query)
    {
        Dispatcher.VerifyAccess();
        if (pendingTabCloses.TryGetValue(query, out var pending)) return pending;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingTabCloses.Add(query, completion.Task);
        _ = CloseQueryTrackedAsync(query, completion);
        return completion.Task;
    }

    private async Task CloseQueryTrackedAsync(QueryView query, TaskCompletionSource<bool> completion)
    {
        try { completion.SetResult(await CloseQueryCoreAsync(query)); }
        catch (Exception ex) { completion.SetException(ex); }
        finally { pendingTabCloses.Remove(query); }
    }

    private async Task<bool> CloseQueryCoreAsync(QueryView query)
    {
        if (!documents.TryGetValue(query, out var document)) return true;
        if (closingWindow || !await ConfirmCloseAsync(query)) return false;
        long revision = query.EditRevision;
        closingQueries.Add(query); query.IsEnabled = false;
        try
        {
            if (!await ForgetRecoveryAsync(query)) return false;
            if (query.EditRevision != revision) { Status.Text = "Query changed during close; tab retained."; return false; }
            acceptedTabCloses.Add(query);
            document.Close();
            return !documents.ContainsKey(query);
        }
        finally { closingQueries.Remove(query); query.IsEnabled = true; }
    }

    private async Task<bool> ConfirmCloseAsync(QueryView query)
    {
        if (pendingSaves.TryGetValue(query, out var save))
        {
            try { await save; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            { Status.Text = "Save failed; query remains open: " + ex.Message; return false; }
        }
        if (query.IsBusy) { MessageBox.Show(this, "Cancel the running operation before closing this query.", "PGMS"); return false; }
        if (!query.IsDirty) return true;
        var answer = MessageBox.Show(this, "Save changes to this query?", "PGMS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && await SaveAsync(query) && !query.IsDirty);
    }
}
