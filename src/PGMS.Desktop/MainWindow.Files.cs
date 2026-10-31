using System.IO;
using System.Windows.Input;

namespace PGMS.Desktop;

public partial class MainWindow
{
    private readonly RecoveryIoQueue fileIo = new();
    private readonly Dictionary<QueryView, Task> pendingSaves = [];
    private readonly HashSet<string> pendingSavePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool closeRequested;
    private long documentSelectionRevision;
    private readonly RecoveryIoQueue openIo = new();
    private readonly SemaphoreSlim openPreparationSlots = new(2, 2);
    private readonly Dictionary<string, Task<QueryView>> pendingOpens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> openCancellations = new(StringComparer.OrdinalIgnoreCase);
    public void CancelPendingOpens()
    {
        Dispatcher.VerifyAccess();
        foreach (var cancellation in openCancellations.Values) cancellation.Cancel();
    }
    public Task OpenCompletion => Task.WhenAll(pendingOpens.Values.ToArray());

    public Task<QueryView> OpenQueryFileAsync(string path)
    {
        Dispatcher.VerifyAccess();
        if (closeRequested || closingWindow || recoveryClosed || shuttingDown)
            return Task.FromException<QueryView>(new IOException("The window is closing."));
        path = Path.GetFullPath(path);
        if (pendingSavePaths.Contains(path))
            return Task.FromException<QueryView>(new IOException("This file is being saved. Wait before opening it."));
        if (pendingOpens.TryGetValue(path, out var pending)) return pending;
        var existing = documents.Keys.FirstOrDefault(q => string.Equals(q.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return Task.FromResult(OpenQueryFile(path));
        var completion = new TaskCompletionSource<QueryView>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOpens.Add(path, completion.Task);
        var cancellation = new CancellationTokenSource();
        openCancellations.Add(path, cancellation);
        _ = OpenTrackedAsync(path, completion, cancellation, documentSelectionRevision);
        return completion.Task;
    }

    private async Task OpenTrackedAsync(string path, TaskCompletionSource<QueryView> completion, CancellationTokenSource cancellation, long selectionRevision)
    {
        bool reserved = false;
        try
        {
            await openPreparationSlots.WaitAsync(cancellation.Token);
            reserved = true;
            var opened = await openIo.RunAwaitedAsync(async () =>
            {
                var snapshot = await SqlFileStore.ReadAsync(path, cancellation.Token).ConfigureAwait(false);
                return (File: snapshot, Document: PreparedQueryDocument.Create(snapshot.Text, cancellation.Token));
            });
            var file = opened.File;
            // Let input and cancellation run before constructing another editor.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closeRequested || closingWindow || recoveryClosed || shuttingDown) throw new IOException("The window is closing.");
            var existing = documents.Keys.FirstOrDefault(q => string.Equals(q.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { completion.SetResult(existing); return; }
            var query = NewQuery(path: file.Path, activate: selectionRevision == documentSelectionRevision, prepared: opened.Document);
            query.FileFormat = file.Format;
            query.FileVersion = file.Version;
            completion.SetResult(query);
        }
        catch (OperationCanceledException) { completion.SetCanceled(cancellation.Token); }
        catch (Exception ex) { completion.SetException(ex); }
        finally
        {
            pendingOpens.Remove(path); openCancellations.Remove(path); cancellation.Dispose();
            if (reserved) openPreparationSlots.Release();
        }
    }

    public Task SaveCompletion => Task.WhenAll(pendingSaves.Values.ToArray());
    public bool IsSaving(QueryView query) => pendingSaves.ContainsKey(query);

    public Task SaveQueryFileAsync(QueryView query, string path, bool overwrite = false)
    {
        Dispatcher.VerifyAccess();
        if (closingWindow || closingQueries.Contains(query) || recoveryClosed || shuttingDown)
            return Task.FromException(new IOException("The query is closing."));
        if (IsSaving(query)) return Task.FromException(new InvalidOperationException("This query is already being saved."));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingSaves.Add(query, completion.Task);
        CommandManager.InvalidateRequerySuggested();
        _ = SaveTrackedAsync(query, path, overwrite, completion);
        return completion.Task;
    }

    private async Task<bool> SaveAsync(QueryView query, bool saveAs = false)
    {
        if (IsSaving(query)) return false;
        string? path = query.FilePath;
        if (saveAs || path is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", DefaultExt = ".sql",
                FileName = path is null ? documents[query].Title.TrimEnd('*') : Path.GetFileName(path) };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try { await SaveQueryFileAsync(query, path, true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        { ShowError(ex); return false; }
    }

    private async Task SaveTrackedAsync(QueryView query, string path, bool overwrite, TaskCompletionSource completion)
    {
        try { await SaveSnapshotAsync(query, path, overwrite); completion.SetResult(); }
        catch (Exception ex) { completion.SetException(ex); }
        finally { pendingSaves.Remove(query); CommandManager.InvalidateRequerySuggested(); }
    }

    private async Task SaveSnapshotAsync(QueryView query, string path, bool overwrite)
    {
        if (!documents.ContainsKey(query)) throw new ArgumentException("The query does not belong to this window.", nameof(query));
        path = Path.GetFullPath(path);
        if (pendingOpens.ContainsKey(path) || documents.Keys.Any(q => q != query && string.Equals(q.FilePath, path, StringComparison.OrdinalIgnoreCase)) || !pendingSavePaths.Add(path))
            throw new IOException("This file is already open or being saved in another query tab.");
        try
        {
            QueryTextSnapshot text;
            using (UiPhaseScope.Start(UiPhaseMeasured, "save-capture"))
                text = QueryTextSnapshot.Capture(query.Editor.Document);
            long revision = query.EditRevision;
            var format = query.FileFormat;
            string? previousPath = query.FilePath;
            var previousVersion = query.FileVersion;
            var saved = await fileIo.RunAsync(() =>
            {
                SqlFileVersion? expected = null;
                if (overwrite)
                {
                    if (string.Equals(path, previousPath, StringComparison.OrdinalIgnoreCase)) expected = previousVersion;
                    else if (File.Exists(path)) expected = SqlFileStore.CaptureVersion(path);
                }
                return SqlFileStore.Write(path, text.Materialize(), format, expected);
            });
            query.FilePath = saved.Path; query.FileVersion = saved.Version;
            query.IsDirty = query.EditRevision != revision || query.FileFormat != format;
            documents[query].Title = Path.GetFileName(saved.Path) + (query.IsDirty ? "*" : "");
            Status.Text = query.IsDirty ? "Snapshot saved; newer edits remain unsaved" : "Item saved";
            if (!query.IsDirty) await ForgetRecoveryAsync(query);
        }
        finally { pendingSavePaths.Remove(path); }
    }

    private async Task<bool> ForgetRecoveryAsync(QueryView query)
    {
        recoveryGeneration++; checkpointRevisions.Remove(query.RecoveryId);
        try
        {
            await recoveryIo.RunAsync(() => recoverySession?.Store.Remove(query.RecoveryId));
            pendingRecoveryRemovals.Remove(query.RecoveryId);
            return true;
        }
        catch (Exception ex) when (IsRecoveryError(ex))
        {
            if (!query.IsDirty) pendingRecoveryRemovals.Add(query.RecoveryId);
            Status.Text = "Recovery cleanup failed: " + ex.Message;
            return false;
        }
    }
}
