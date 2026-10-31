using System.IO;
using System.Reflection;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using PGMS.Desktop;

internal static class SqlOpenAsyncChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "open-async", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", root);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        using var release = new ManualResetEventSlim();
        try
        {
            window.Show(); await window.RecoveryCompletion;
            string path = Path.Combine(root, "unicode.sql");
            var snapshot = SqlFileStore.Write(path, "SELECT 'ação 漢字';\r\n", SqlFileFormat.Utf16BigEndian);
            var io = (RecoveryIoQueue)typeof(MainWindow).GetField("openIo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var blocker = io.RunAsync(() => release.Wait(TimeSpan.FromSeconds(10)));
            var opening = window.OpenQueryFileAsync(path);
            check(ReferenceEquals(opening, window.OpenQueryFileAsync(path.ToUpperInvariant())), "Concurrent Open coalesces normalized case-insensitive path");
            bool responsive = false; await window.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Input);
            check(responsive && !opening.IsCompleted, "Open leaves input Dispatcher responsive while read queue is blocked");
            check(!window.TryOpenQueryFile(path, out _), "Synchronous Open cannot bypass pending read reservation");
            string otherPath = Path.Combine(root, "other.sql");
            SqlFileStore.Write(otherPath, "SELECT 2;", SqlFileFormat.Utf8);
            var other = window.OpenQueryFile(otherPath);
            bool reserved = false;
            try { await window.SaveQueryFileAsync(other, path, true); } catch (IOException) { reserved = true; }
            check(reserved && File.ReadAllBytes(path).SequenceEqual(SqlFileStore.GetEncoding(snapshot.Format).GetPreamble().Concat(SqlFileStore.GetEncoding(snapshot.Format).GetBytes(snapshot.Text))), "Save cannot overwrite a file reserved by Open");
            release.Set(); await blocker;
            var query = await opening;
            check(((DockingManager)window.FindName("DockManager")).ActiveContent == other, "Async Open preserves selection changed while reading");
            var editor = (ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor");
            check(editor.Text == snapshot.Text && query.FileFormat == snapshot.Format && query.FileVersion == snapshot.Version && !query.IsDirty, "Async Open preserves exact text BOM format and file fingerprint");
            check(!editor.CanUndo && editor.Document.LineCount == 2, "Prepared Open starts with correct lines and no synthetic Undo history");
            editor.AppendText("-- first edit"); editor.Undo();
            check(editor.Text == snapshot.Text && query.IsDirty, "Prepared Open preserves native edit Undo and dirty notification");
            editor.AppendText("-- unsaved");
            check(ReferenceEquals(query, await window.OpenQueryFileAsync(path)) && query.IsDirty, "Repeated async Open retains dirty existing document");
            using var cancelRelease = new ManualResetEventSlim();
            var cancelBlocker = io.RunAsync(() => cancelRelease.Wait(TimeSpan.FromSeconds(10)));
            string cancelPath = Path.Combine(root, "cancel.sql");
            SqlFileStore.Write(cancelPath, "SELECT 3;", SqlFileFormat.Utf8);
            var canceledOpen = window.OpenQueryFileAsync(cancelPath);
            window.CancelPendingOpens(); cancelRelease.Set(); await cancelBlocker;
            bool canceledWindowRead = false;
            try { await canceledOpen; } catch (OperationCanceledException) { canceledWindowRead = true; }
            check(canceledWindowRead && File.ReadAllText(cancelPath) == "SELECT 3;", "Canceled Open preserves source file and cancels completion");
            check((await window.OpenQueryFileAsync(cancelPath)).FilePath == cancelPath, "Canceled Open releases reservation for retry");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool canceledRead = false;
            try { await SqlFileStore.ReadAsync(path, canceled.Token); } catch (OperationCanceledException) { canceledRead = true; }
            check(canceledRead, "Snapshot async reader honors precancellation");
            using var slotsRelease = new ManualResetEventSlim();
            var slotsBlocker = io.RunAsync(() => slotsRelease.Wait(TimeSpan.FromSeconds(10)));
            string[] slotPaths = Enumerable.Range(0, 5).Select(i => Path.Combine(root, $"slot-{i}.sql")).ToArray();
            foreach (string slotPath in slotPaths) SqlFileStore.Write(slotPath, "SELECT 5;", SqlFileFormat.Utf8);
            var slotTasks = slotPaths.Select(window.OpenQueryFileAsync).ToArray();
            var slots = (SemaphoreSlim)typeof(MainWindow).GetField("openPreparationSlots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            check(slots.CurrentCount == 0 && slotTasks.All(t => !t.IsCompleted), "Open admits at most two preparations while later requests wait");
            window.CancelPendingOpens(); slotsRelease.Set(); await slotsBlocker;
            try { await Task.WhenAll(slotTasks); } catch (OperationCanceledException) { }
            check(slotTasks.All(t => t.IsCanceled) && slots.CurrentCount == 2, "Cancellation releases active and waiting preparation slots");
            check((await window.OpenQueryFileAsync(slotPaths[4])).FilePath == slotPaths[4] && slots.CurrentCount == 2, "Open retries after canceled preparation queue without leaking capacity");
            string invalidPath = Path.Combine(root, "invalid.sql");
            File.WriteAllBytes(invalidPath, [0xC3, 0x28]);
            var manager = (DockingManager)window.FindName("DockManager");
            int beforeFailure = manager.Layout.Descendents().OfType<LayoutDocument>().Count();
            bool invalidRejected = false;
            try { await window.OpenQueryFileAsync(invalidPath); } catch (InvalidDataException) { invalidRejected = true; }
            check(invalidRejected && manager.Layout.Descendents().OfType<LayoutDocument>().Count() == beforeFailure, "Invalid async Open adds no blank document");
            File.WriteAllText(invalidPath, "SELECT 'repaired';");
            var repaired = await window.OpenQueryFileAsync(invalidPath);
            check(repaired.FilePath == invalidPath, "Failed async Open releases path and accepts corrected file");
            string missing = Path.Combine(root, "missing.sql");
            bool missingRejected = false;
            try { await window.OpenQueryFileAsync(missing); } catch (FileNotFoundException) { missingRejected = true; }
            check(missingRejected && File.ReadAllText(invalidPath) == "SELECT 'repaired';", "Missing file failure does not poison subsequent reads or mutate files");
            File.WriteAllText(invalidPath, "SELECT 'external';");
            ((ICSharpCode.AvalonEdit.TextEditor)repaired.FindName("Editor")).AppendText("-- edit");
            bool stale = false;
            try { await window.SaveQueryFileAsync(repaired, invalidPath, true); } catch (IOException) { stale = true; }
            check(stale && repaired.IsDirty && File.ReadAllText(invalidPath) == "SELECT 'external';", "Async Open fingerprint protects against later external overwrite");
            repaired.IsDirty = false;
            query.IsDirty = false;
            using var closeRelease = new ManualResetEventSlim();
            var closeBlocker = io.RunAsync(() => closeRelease.Wait(TimeSpan.FromSeconds(10)));
            string[] latePaths = Enumerable.Range(0, 8).Select(i => Path.Combine(root, $"late-{i}.sql")).ToArray();
            foreach (string latePath in latePaths) SqlFileStore.Write(latePath, "SELECT 4;", SqlFileFormat.Utf8);
            var lateOpens = latePaths.Select(window.OpenQueryFileAsync).ToArray();
            window.Close(); var closing = window.CloseCompletion;
            window.Close();
            check(ReferenceEquals(closing, window.CloseCompletion), "Repeated window Close shares completion while eight Opens are pending");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            check(!closing.IsCompleted && !System.Windows.Input.ApplicationCommands.Open.CanExecute(null, window), "Close waits for pending Open without blocking input and disables new Open commands");
            check(!window.TryOpenQueryFile(path, out _), "Synchronous Open refuses window pending closure");
            closeRelease.Set(); await closeBlocker;
            bool lateCanceled = false;
            try { await Task.WhenAll(lateOpens); } catch (OperationCanceledException) { lateCanceled = true; }
            await closing;
            check(lateCanceled && lateOpens.All(t => t.IsCanceled) && slots.CurrentCount == 2 && !((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>().Any(q => latePaths.Contains(q.FilePath)), "Close cancels all late reads without creating documents");
        }
        finally
        {
            release.Set();
            await window.OpenCompletion;
            foreach (var q in ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) q.IsDirty = false;
            window.Close(); await window.CloseCompletion;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }
}
