using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PGMS.Desktop;

public sealed record RecoveryReport(int Restored, int Busy, int Failed, int Deferred, bool Canceled = false);

public sealed class QueryRecoverySession : IDisposable
{
    private readonly FileStream lease;
    private bool disposed;
    public QueryRecoveryStore Store { get; }

    public QueryRecoverySession(string root, Guid? sessionId = null)
    {
        Directory.CreateDirectory(root); QueryRecoveryStore.RejectLink(root);
        string directory = Path.Combine(Path.GetFullPath(root), (sessionId ?? Guid.NewGuid()).ToString("N"));
        Directory.CreateDirectory(directory); QueryRecoveryStore.RejectLink(directory);
        string lockPath = Path.Combine(directory, ".lease");
        if (File.Exists(lockPath)) QueryRecoveryStore.RejectLink(lockPath);
        lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Store = new(directory);
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var id in Store.ListIds()) Store.Remove(id);
    }

    public static RecoveryReport Recover(string root, string activeDirectory, Action<RecoveryDraft> accept,
        int maximumDocuments = QueryRecoveryStore.MaximumDocuments)
        => RecoverCoreAsync(root, activeDirectory, draft => { accept(draft); return ValueTask.FromResult(true); }, maximumDocuments, CancellationToken.None).GetAwaiter().GetResult();

    public static Task<RecoveryReport> RecoverAsync(string root, string activeDirectory,
        Func<RecoveryDraft, ValueTask<bool>> accept, int maximumDocuments = QueryRecoveryStore.MaximumDocuments, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(accept);
        return Task.Run(() => RecoverCoreAsync(root, activeDirectory, accept, maximumDocuments, token));
    }

    private static async Task<RecoveryReport> RecoverCoreAsync(string root, string activeDirectory,
        Func<RecoveryDraft, ValueTask<bool>> accept, int maximumDocuments, CancellationToken token)
    {
        if (maximumDocuments < 0 || maximumDocuments > QueryRecoveryStore.MaximumDocuments)
            throw new ArgumentOutOfRangeException(nameof(maximumDocuments));
        if (token.IsCancellationRequested) return new(0, 0, 0, 0, true);
        if (!Directory.Exists(root)) return new(0, 0, 0, 0);
        QueryRecoveryStore.RejectLink(root);
        int restored = 0, busy = 0, failed = 0, deferred = 0;
        var directories = Directory.EnumerateDirectories(root)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
                !string.Equals(Path.GetFullPath(path), Path.GetFullPath(activeDirectory), StringComparison.OrdinalIgnoreCase)).Take(129).ToArray();
        if (directories.Length > 128) deferred++;
        foreach (string directory in directories.Take(128))
        {
            if (token.IsCancellationRequested) return new(restored, busy, failed, deferred, true);
            if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(activeDirectory), StringComparison.OrdinalIgnoreCase)) continue;
            QueryRecoverySession session;
            try { session = new(root, Guid.ParseExact(Path.GetFileName(directory), "N")); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { busy++; continue; }
            catch (IOException) { failed++; continue; }
            catch (UnauthorizedAccessException) { failed++; continue; }
            using (session)
            {
                IReadOnlyList<Guid> ids;
                try { ids = session.Store.ListIds(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { failed++; continue; }
                foreach (var id in ids)
                {
                    if (token.IsCancellationRequested) return new(restored, busy, failed, deferred, true);
                    if (restored >= maximumDocuments) { deferred++; continue; }
                    try
                    {
                        var draft = session.Store.Read(id);
                        // Acceptance must retain the destination durably before returning true.
                        if (!await accept(draft).ConfigureAwait(false)) { deferred++; continue; }
                        // An accepted document consumes capacity even if source cleanup fails.
                        restored++;
                        session.Store.Remove(id);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { return new(restored, busy, failed, deferred, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException)
                    { failed++; }
                }
            }
        }
        return new(restored, busy, failed, deferred, token.IsCancellationRequested);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lease.Dispose();
        try
        {
            File.Delete(Path.Combine(Store.DirectoryPath, ".lease"));
            if (!Directory.EnumerateFileSystemEntries(Store.DirectoryPath).Any()) Directory.Delete(Store.DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preserve data if cleanup cannot finish. */ }
    }
}
