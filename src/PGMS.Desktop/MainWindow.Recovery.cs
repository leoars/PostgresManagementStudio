using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

namespace PGMS.Desktop;

public partial class MainWindow
{
    private QueryRecoverySession? recoverySession;
    private readonly RecoveryIoQueue recoveryIo = new();
    private Task<int> checkpointTask = Task.FromResult(0);
    public Task<int> RecoveryCheckpointCompletion => checkpointTask;
    private long recoveryGeneration;
    private Task<RecoveryReport> restoreTask = Task.FromResult(new RecoveryReport(0, 0, 0, 0));
    private CancellationTokenSource? restoreCancellation;
    public Task<RecoveryReport> RecoveryCompletion => restoreTask;
    public bool IsRecovering => !restoreTask.IsCompleted;
    public void CancelRecovery() { Dispatcher.VerifyAccess(); restoreCancellation?.Cancel(); }
    private bool recoveryStarted;
    private bool recoveryClosed;
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<Guid, long> checkpointRevisions = [];
    private readonly HashSet<Guid> pendingRecoveryRemovals = [];
    public string? RecoveryDirectory => recoverySession?.Store.DirectoryPath;

    private void StartRecovery()
    {
        if (recoveryStarted || recoveryClosed) return;
        recoveryStarted = true;
        try { recoverySession ??= new(Path.Combine(Path.GetDirectoryName(layoutFile)!, "recovery")); }
        catch (Exception ex) when (IsRecoveryError(ex)) { Status.Text = "Recovery unavailable: " + ex.Message; }
        _ = RecoverPendingQueriesAsync();
        _ = WarmEditorAsync();
        recoveryTimer.Tick += async (_, _) =>
        {
            // Skip ticks while IO is pending instead of accumulating awaiting event handlers.
            if (checkpointTask.IsCompleted) await CheckpointQueriesAsync();
        };
        recoveryTimer.Start();
    }

    public RecoveryReport RecoverPendingQueries()
    {
        if (IsRecovering) return new(0, 1, 0, 0);
        if (recoveryClosed) return new(0, 0, 0, 0);
        if (recoverySession is null) return new(0, 0, 1, 0);
        try
        {
            int occupied = documents.Keys.Select(q => q.RecoveryId).Concat(recoverySession.Store.ListIds()).Distinct().Count();
            int available = Math.Max(0, QueryRecoveryStore.MaximumDocuments - occupied);
            var result = QueryRecoverySession.Recover(Path.Combine(Path.GetDirectoryName(layoutFile)!, "recovery"), recoverySession.Store.DirectoryPath, draft =>
            {
                var query = NewQuery(draft.Text, origin: QueryHistoryOrigin.Typed);
                query.FileFormat = draft.Format;query.WordWrap=draft.WordWrap;query.ShowWhiteSpace=draft.ShowWhiteSpace;query.CommandTimeoutSeconds=draft.CommandTimeoutSeconds;
                query.IsDirty = true;
                string title = draft.Title.StartsWith("Recovered - ", StringComparison.Ordinal) ? draft.Title : "Recovered - " + draft.Title;
                title = BoundRecoveryTitle(title);
                documents[query].Title = title + "*";
                try
                {
                    recoveryIo.Run(() => recoverySession.Store.Save(draft with{Id=query.RecoveryId,Title=title}));
                    checkpointRevisions[query.RecoveryId] = query.RecoveryRevision;
                }
                catch
                {
                    query.IsDirty = false;
                    documents[query].Close();
                    throw;
                }
            }, available);
            if (result.Restored > 0 || result.Failed > 0 || result.Deferred > 0)
                Status.Text = $"Recovery: {result.Restored} queries restored; {result.Failed} errors; {result.Deferred} deferred. Review recovered tabs and use Save As.";
            return result;
        }
        catch (Exception ex) when (IsRecoveryError(ex))
        { Status.Text = "Recovery unavailable: " + ex.Message; return new(0, 0, 1, 0); }
    }

    public Task<RecoveryReport> RecoverPendingQueriesAsync()
    {
        Dispatcher.VerifyAccess();
        if (IsRecovering) return restoreTask;
        return restoreTask = RestoreQueriesCoreAsync();
    }

    private async Task<RecoveryReport> RestoreQueriesCoreAsync()
    {
        if (recoveryClosed || shuttingDown || closingWindow) return new(0, 0, 0, 0);
        if (recoverySession is null) return new(0, 0, 1, 0);
        using var cancel = new CancellationTokenSource();
        restoreCancellation = cancel;
        var store = recoverySession.Store;
        var documentIds = documents.Keys.Select(q => q.RecoveryId).ToArray();
        Status.Text = "Recovering unsaved queries...";
        try
        {
            var stored = await recoveryIo.RunAsync(store.ListIds);
            int available = Math.Max(0, QueryRecoveryStore.MaximumDocuments - documentIds.Concat(stored).Distinct().Count());
            var report = await QueryRecoverySession.RecoverAsync(Path.Combine(Path.GetDirectoryName(layoutFile)!, "recovery"), store.DirectoryPath, async draft =>
            {
                string title = BoundRecoveryTitle(draft.Title.StartsWith("Recovered - ", StringComparison.Ordinal) ? draft.Title : "Recovered - " + draft.Title);
                var destination = draft with { Id = Guid.NewGuid(), Title = title };
                var existingIds = await recoveryIo.RunAsync(store.ListIds).ConfigureAwait(false);
                bool hasCapacity = await Dispatcher.InvokeAsync(() => !cancel.IsCancellationRequested && !recoveryClosed && !shuttingDown &&
                    documents.Keys.Select(q => q.RecoveryId).Concat(existingIds).Distinct().Count() < QueryRecoveryStore.MaximumDocuments);
                if (!hasCapacity) return false;
                var prepared = await Task.Run(() => PreparedQueryDocument.Create(destination.Text, cancel.Token), cancel.Token).ConfigureAwait(false);
                // Persist before exposing a tab, so Save/Close can never precede this write.
                var retained = await recoveryIo.RunAsync(() => { store.Save(destination); return store.ListIds(); }).ConfigureAwait(false);
                bool accepted = await Dispatcher.InvokeAsync(() =>
                {
                    if (cancel.IsCancellationRequested || recoveryClosed || shuttingDown ||
                        documents.Keys.Select(q => q.RecoveryId).Concat(retained).Distinct().Count() > QueryRecoveryStore.MaximumDocuments) return false;
                    var query = NewQuery(recoveryId: destination.Id, activate: ActiveQuery is null, prepared: prepared);
                    query.FileFormat = destination.Format;query.WordWrap=destination.WordWrap;query.ShowWhiteSpace=destination.ShowWhiteSpace;query.CommandTimeoutSeconds=destination.CommandTimeoutSeconds; query.IsDirty = true;
                    documents[query].Title = destination.Title + "*";
                    checkpointRevisions[query.RecoveryId] = query.RecoveryRevision;
                    return true;
                }, DispatcherPriority.Background);
                if (!accepted) await recoveryIo.RunAsync(() => store.Remove(destination.Id)).ConfigureAwait(false);
                return accepted;
            }, available, cancel.Token);
            Status.Text = $"Recovery{(report.Canceled ? " canceled" : "")}: {report.Restored} queries restored; {report.Failed} errors; {report.Deferred} deferred. Review recovered tabs and use Save As.";
            return report;
        }
        catch (Exception ex) when (IsRecoveryError(ex))
        { Status.Text = "Recovery unavailable: " + ex.Message; return new(0, 0, 1, 0); }
        finally { restoreCancellation = null; }
    }

    public int CheckpointQueries()
    {
        if (recoveryClosed || shuttingDown || closingWindow) return 0;
        int saved = 0;
        try
        {
            recoverySession ??= new(Path.Combine(Path.GetDirectoryName(layoutFile)!, "recovery"));
            foreach (var id in pendingRecoveryRemovals.ToArray()) RemoveRecovery(id);
            foreach (var pair in documents)
            {
                var query = pair.Key;
                if (closingQueries.Contains(query) || !query.IsDirty || (checkpointRevisions.TryGetValue(query.RecoveryId, out long revision) && revision == query.RecoveryRevision)) continue;
                try
                {
                    string title = pair.Value.Title.TrimEnd('*');
                    title = BoundRecoveryTitle(title);
                    var draft = new RecoveryDraft(query.RecoveryId, title, query.Editor.Text, query.FileFormat){WordWrap=query.WordWrap,ShowWhiteSpace=query.ShowWhiteSpace,CommandTimeoutSeconds=query.CommandTimeoutSeconds};
                    recoveryIo.Run(() => recoverySession.Store.Save(draft));
                    checkpointRevisions[query.RecoveryId] = query.RecoveryRevision;
                    pendingRecoveryRemovals.Remove(query.RecoveryId);
                    saved++;
                }
                catch (Exception ex) when (IsRecoveryError(ex)) { Status.Text = "Recovery incomplete: " + ex.Message; }
            }
        }
        catch (Exception ex) when (IsRecoveryError(ex)) { Status.Text = "Recovery unavailable: " + ex.Message; }
        return saved;
    }

    public Task<int> CheckpointQueriesAsync()
    {
        Dispatcher.VerifyAccess();
        if (!checkpointTask.IsCompleted) return checkpointTask;
        return checkpointTask = CheckpointQueriesCoreAsync();
    }

    private async Task<int> CheckpointQueriesCoreAsync()
    {
        if (recoveryClosed || shuttingDown || closingWindow) return 0;
        try
        {
            recoverySession ??= new(Path.Combine(Path.GetDirectoryName(layoutFile)!, "recovery"));
            var removals = pendingRecoveryRemovals.ToArray();
            if (documents.Keys.Any(q => q.IsDirty && q.Editor.Document.TextLength > QueryRecoveryStore.MaximumCharacters))
                Status.Text = "Recovery incomplete: text exceeds 262144 characters. Save larger queries to files.";
            long generation = recoveryGeneration;
            // Capture immutable bounded rope snapshots; materialize text on the worker.
            var snapshotPhase = UiPhaseScope.Start(UiPhaseMeasured, "checkpoint-capture");
            var snapshots = documents.Where(pair => !closingQueries.Contains(pair.Key) && pair.Key.IsDirty && pair.Key.Editor.Document.TextLength <= QueryRecoveryStore.MaximumCharacters &&
                (!checkpointRevisions.TryGetValue(pair.Key.RecoveryId, out var revision) || revision != pair.Key.RecoveryRevision))
                .Take(QueryRecoveryStore.MaximumDocuments)
                .Select(pair => (Query: pair.Key, Revision: pair.Key.RecoveryRevision,
                    Id: pair.Key.RecoveryId, Title: BoundRecoveryTitle(pair.Value.Title.TrimEnd('*')),
                    Text: QueryTextSnapshot.Capture(pair.Key.Editor.Document), Format: pair.Key.FileFormat,Wrap:pair.Key.WordWrap,WhiteSpace:pair.Key.ShowWhiteSpace,Timeout:pair.Key.CommandTimeoutSeconds)).ToArray();
            snapshotPhase?.Dispose();
            var store = recoverySession.Store;
            var resultBatch = await recoveryIo.RunAsync(() =>
            {
                var cleanup = removals.Select(id =>
                {
                    try { store.Remove(id); return (Id: id, Error: (Exception?)null); }
                    catch (Exception ex) when (IsRecoveryError(ex)) { return (Id: id, Error: (Exception?)ex); }
                }).ToArray();
                var writes = snapshots.Select(snapshot =>
                {
                    try { store.Save(new(snapshot.Id, snapshot.Title, snapshot.Text.Materialize(), snapshot.Format){WordWrap=snapshot.Wrap,ShowWhiteSpace=snapshot.WhiteSpace,CommandTimeoutSeconds=snapshot.Timeout}); return (snapshot, Error: (Exception?)null); }
                    catch (Exception ex) when (IsRecoveryError(ex)) { return (snapshot, Error: (Exception?)ex); }
                }).ToArray();
                return (cleanup, writes);
            });
            if (recoveryClosed || generation != recoveryGeneration) return 0;
            foreach (var cleanup in resultBatch.cleanup)
                if (cleanup.Error is null) pendingRecoveryRemovals.Remove(cleanup.Id);
                else Status.Text = "Recovery cleanup failed: " + cleanup.Error.Message;
            int saved = 0;
            foreach (var result in resultBatch.writes)
            {
                if (result.Error is not null) { Status.Text = "Recovery incomplete: " + result.Error.Message; continue; }
                var snapshot = result.snapshot;
                if (!documents.ContainsKey(snapshot.Query) || !snapshot.Query.IsDirty) continue;
                checkpointRevisions[snapshot.Id] = snapshot.Revision;
                pendingRecoveryRemovals.Remove(snapshot.Id);
                saved++;
            }
            return saved;
        }
        catch (Exception ex) when (IsRecoveryError(ex))
        { if (!recoveryClosed) Status.Text = "Recovery unavailable: " + ex.Message; return 0; }
    }

    private static string BoundRecoveryTitle(string title)
    {
        if (title.Length <= 128) return title;
        int length = char.IsHighSurrogate(title[127]) ? 127 : 128;
        return title[..length];
    }

    private bool ForgetRecovery(QueryView query)
    {
        recoveryGeneration++;
        checkpointRevisions.Remove(query.RecoveryId);
        return RemoveRecovery(query.RecoveryId);
    }

    private bool RemoveRecovery(Guid id)
    {
        try
        {
            recoveryIo.Run(() => recoverySession?.Store.Remove(id));
            pendingRecoveryRemovals.Remove(id);
            return true;
        }
        catch (Exception ex) when (IsRecoveryError(ex))
        {
            pendingRecoveryRemovals.Add(id);
            Status.Text = "Recovery cleanup failed; retry Save or close after resolving access: " + ex.Message;
            return false;
        }
    }

    private async Task<bool> ClearRecoveryOnCloseAsync()
    {
        try
        {
            recoveryGeneration++;
            await recoveryIo.RunAsync(() => recoverySession?.Clear());
            pendingRecoveryRemovals.Clear(); checkpointRevisions.Clear();
            recoveryTimer.Stop();
            return true;
        }
        catch (Exception ex) when (IsRecoveryError(ex))
        { Status.Text = "Recovery cleanup failed; window remains open: " + ex.Message; return false; }
    }

    private static bool IsRecoveryError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException;
}
