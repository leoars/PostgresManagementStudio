using System.IO;
using AvalonDock.Layout;
using PGMS.Desktop;

internal static class RecoveryAsyncChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "recovery-async", Guid.NewGuid().ToString("N")));
        var draft = new RecoveryDraft(Guid.NewGuid(), "async.sql", "SELECT 'async';", SqlFileFormat.Utf8);
        string sourcePath;
        using (var source = new QueryRecoverySession(root)) { source.Store.Save(draft); sourcePath = source.Store.DirectoryPath; }
        using var destination = new QueryRecoverySession(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThread = Environment.CurrentManagedThreadId, acceptThread = 0;
        var pending = QueryRecoverySession.RecoverAsync(root, destination.Store.DirectoryPath, async item =>
        {
            acceptThread = Environment.CurrentManagedThreadId;
            entered.SetResult();
            await release.Task;
            destination.Store.Save(item);
            return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            check(acceptThread != callerThread && !pending.IsCompleted, "Async recovery scans outside Dispatcher and awaits acceptance");
            var competing = await QueryRecoverySession.RecoverAsync(root, destination.Store.DirectoryPath, _ => ValueTask.FromResult(false));
            check(competing.Busy == 1 && File.Exists(Path.Combine(sourcePath, draft.Id.ToString("N") + ".draft")), "Async acceptance retains source lease and record until durable transfer");
        }
        finally { release.TrySetResult(); }
        var result = await pending;
        check(result.Restored == 1 && destination.Store.Read(draft.Id) == draft && !Directory.Exists(sourcePath), "Async acceptance transfers complete draft before removing source");
        using (var source = new QueryRecoverySession(root)) source.Store.Save(draft with { Id = Guid.NewGuid() });
        result = await QueryRecoverySession.RecoverAsync(root, destination.Store.DirectoryPath, _ => ValueTask.FromResult(false));
        check(result.Deferred == 1 && result.Failed == 0, "Async capacity refusal defers source without reporting corruption");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        int calls = 0;
        result = await QueryRecoverySession.RecoverAsync(root, destination.Store.DirectoryPath, _ => { calls++; return ValueTask.FromResult(true); }, token: cancel.Token);
        check(result.Canceled && result.Restored == 0 && calls == 0, "Pre-canceled recovery does not read or accept source records");
        using var duringAccept = new CancellationTokenSource();
        result = await QueryRecoverySession.RecoverAsync(root, destination.Store.DirectoryPath, item =>
        {
            destination.Store.Save(item); duringAccept.Cancel(); return ValueTask.FromResult(true);
        }, token: duringAccept.Token);
        check(result.Canceled && result.Restored == 1 && result.Failed == 0, "Cancellation after durable acceptance still accounts for completed transfer");
        result = await QueryRecoverySession.RecoverAsync(root, destination.Store.DirectoryPath, _ => ValueTask.FromResult(false));
        check(result.Deferred == 0 && result.Restored == 0, "Accepted record is removed before cancellation ends scan");
        destination.Clear();
        await CheckWindowAsync(root, draft, check);
        await CheckCloseIoAsync(root, check);
        await CheckSelectionAsync(root, draft, check);
    }
    private static async Task CheckSelectionAsync(string root, RecoveryDraft draft, Action<bool, string> check)
    {
        string state = Path.Combine(root, "selection");
        using (var source = new QueryRecoverySession(Path.Combine(state, "recovery"))) source.Store.Save(draft);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        var io = (RecoveryIoQueue)typeof(MainWindow).GetField("recoveryIo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        using var release = new ManualResetEventSlim();
        var blocker = io.RunAsync(() => release.Wait(TimeSpan.FromSeconds(10)));
        var dock = (AvalonDock.DockingManager)window.FindName("DockManager");
        try
        {
            window.Show();
            string path = Path.Combine(state, "chosen.sql");
            SqlFileStore.Write(path, "SELECT 'chosen';", SqlFileFormat.Utf8);
            var chosen = window.OpenQueryFile(path);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            release.Set(); await blocker; await window.RecoveryCompletion;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(ReferenceEquals(dock.ActiveContent, chosen), "Async restore preserves the document chosen while disk IO was pending");
            var docs = dock.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().ToArray();
            check(docs.Length == 2 && docs.Any(d => d.Content is QueryView q && q.IsDirty && q.FilePath is null),
                "Background restore adds its complete dirty document alongside the selected SQL file");
            // Queue focus for an existing file, then change selection before that callback runs.
            window.OpenQueryFile(path);
            var recovered = docs.Single(d => !ReferenceEquals(d.Content, chosen));
            recovered.IsActive = true;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(ReferenceEquals(dock.ActiveContent, recovered.Content) && !((ICSharpCode.AvalonEdit.TextEditor)chosen.FindName("Editor")).IsKeyboardFocusWithin,
                "Deferred Open focus does not reactivate a document after another selection");
        }
        finally
        {
            release.Set(); await blocker; await window.RecoveryCompletion;
            foreach (var doc in dock.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>())
                if (doc.Content is QueryView query) query.IsDirty = false;
            window.Close(); await window.CloseCompletion;
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }
    private static async Task CheckWindowAsync(string root, RecoveryDraft draft, Action<bool, string> check)
    {
        string state = Path.Combine(root, "window");
        string recovery = Path.Combine(state, "recovery");
        string sourcePath;
        using (var source = new QueryRecoverySession(recovery)) { source.Store.Save(draft); sourcePath = source.Store.DirectoryPath; }
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        var io = (RecoveryIoQueue)typeof(MainWindow).GetField("recoveryIo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        using var release = new ManualResetEventSlim();
        var blocker = io.RunAsync(() => release.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            window.Show();
            check(window.IsRecovering && ReferenceEquals(window.RecoveryCompletion, window.RecoverPendingQueriesAsync()), "Startup restore is asynchronous and concurrent requests share its task");
            bool dispatched = false;
            await window.Dispatcher.InvokeAsync(() => dispatched = true);
            check(dispatched && !window.RecoveryCompletion.IsCompleted, "Startup recovery permits Dispatcher work while disk queue is blocked");
            window.Close(); window.Close();
            check(window.IsVisible && window.IsRecovering, "Repeated close requests wait for recovery cancellation without reentrancy");
            release.Set(); await blocker;
            var report = await window.RecoveryCompletion;
            await window.CloseCompletion;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(report.Canceled && !window.IsVisible && new QueryRecoveryStore(sourcePath).Read(draft.Id) == draft,
                "Close resumes after cancellation and leaves unaccepted source intact");
            check(System.Xml.Linq.XDocument.Load(Path.Combine(state, "layout.xml")).Root is not null, "Async layout write preserves XML declared encoding");
        }
        finally
        {
            release.Set(); await blocker; await window.RecoveryCompletion;
            window.Close(); await window.CloseCompletion;
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }
    private static async Task CheckCloseIoAsync(string root, Action<bool, string> check)
    {
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", Path.Combine(root, "closing"));
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        using var release = new ManualResetEventSlim();
        Task? blocker = null;
        try
        {
            window.Show(); await window.RecoveryCompletion;
            var io = (RecoveryIoQueue)typeof(MainWindow).GetField("recoveryIo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            blocker = io.RunAsync(() => release.Wait(TimeSpan.FromSeconds(10)));
            window.Close(); window.Close();
            bool responsive = false; await window.Dispatcher.InvokeAsync(() => responsive = true);
            check(responsive && window.IsVisible && !window.IsEnabled && !window.CloseCompletion.IsCompleted,
                "Window close awaits blocked cleanup with responsive Dispatcher and frozen edits");
            release.Set(); await blocker; await window.CloseCompletion;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(!window.IsVisible, "Window closes once cleanup and lease disposal finish");
        }
        finally
        {
            release.Set(); if (blocker is not null) await blocker;
            window.Close(); await window.CloseCompletion;
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }
}
