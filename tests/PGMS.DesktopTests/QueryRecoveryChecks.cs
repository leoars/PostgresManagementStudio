using System.IO;
using System.Security.Cryptography;
using System.Text;
using PGMS.Desktop;
using AvalonDock.Layout;

internal static class QueryRecoveryChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        RecoveryPayloadChecks.Run(check);
        string root = Path.GetFullPath(Path.Combine("artifacts", "recovery-tests", Guid.NewGuid().ToString("N")));
        var store = new QueryRecoveryStore(root);
        var draft = new RecoveryDraft(Guid.NewGuid(), "query.sql", "SELECT 'synthetic secret ação 😀';\r\n", SqlFileFormat.Utf16BigEndian);
        store.Save(draft);
        check(store.Read(draft.Id) == draft, "Recovery roundtrips text title and format with DPAPI");
        string path = Path.Combine(root, draft.Id.ToString("N") + ".draft");
        byte[] bytes = File.ReadAllBytes(path);
        check(!Encoding.UTF8.GetString(bytes).Contains("synthetic secret"), "Recovery writes no plaintext SQL to disk");
        var updated = draft with { Text = "SELECT 2;" }; store.Save(updated);
        check(store.Read(draft.Id) == updated && Directory.GetFiles(root, "*.partial").Length == 0, "Recovery replaces checkpoint and leaves no staging file");
        bool rejected = false;
        try { store.Save(updated with { Text = new string('x', QueryRecoveryStore.MaximumCharacters + 1) }); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && store.Read(draft.Id) == updated, "Oversize recovery preserves last valid checkpoint");
        foreach(string invalidTitle in new[]{"bad\ud800","bad\udfff"})
        {
            bool invalid=false;byte[] prior=File.ReadAllBytes(path);
            try{store.Save(updated with{Title=invalidTitle});}catch(InvalidDataException){invalid=true;}
            check(invalid && File.ReadAllBytes(path).SequenceEqual(prior),"Invalid Unicode title is refused without replacing encrypted checkpoint");
        }
        var unicodeTitle=updated with{Title=new string('x',126)+"😀"};store.Save(unicodeTitle);
        check(store.Read(draft.Id)==unicodeTitle,"Recovery title preserves non-BMP pair at exact 128-unit boundary");store.Save(updated);
        byte[] lockedPrior=File.ReadAllBytes(path);bool writeRefused=false;
        using(var heldDestination=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            try{store.Save(updated with{Text="must not replace locked destination"});}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){writeRefused=true;}
            check(writeRefused && File.ReadAllBytes(path).SequenceEqual(lockedPrior),"Failed recovery rename preserves prior encrypted checkpoint byte for byte");
            check(Directory.GetFiles(root,"*.partial").Length==0,"Failed recovery rename cleans its encrypted staging file");
        }
        store.Save(updated with{Text="after lock release"});
        check(store.Read(draft.Id).Text=="after lock release","Recovery saving resumes after destination lock release");store.Save(updated);
        var copied = Guid.NewGuid(); string copiedPath = Path.Combine(root, copied.ToString("N") + ".draft");
        File.Copy(path, copiedPath);
        rejected = false;
        try { store.Read(copied); } catch (CryptographicException) { rejected = true; }
        check(rejected && File.Exists(copiedPath), "Recovery identity is cryptographically bound and invalid record retained");
        store.Remove(copied);
        bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(path, bytes);
        rejected = false;
        try { store.Read(draft.Id); } catch (CryptographicException) { rejected = true; }
        check(rejected && File.ReadAllBytes(path).SequenceEqual(bytes), "Corrupt recovery is reported without deleting evidence");
        store.Save(updated);
        for (int i = 1; i < QueryRecoveryStore.MaximumDocuments; i++) store.Save(new(Guid.NewGuid(), "query", "", SqlFileFormat.Utf8));
        rejected = false;
        try { store.Save(new(Guid.NewGuid(), "extra", "text", SqlFileFormat.Utf8)); } catch (IOException) { rejected = true; }
        check(rejected && store.ListIds().Count == 32, "Recovery bounds unsaved document count");
        store.Remove(draft.Id);
        check(!File.Exists(path), "Recovery can remove exactly one saved or discarded draft");
        var oversizedId = Guid.NewGuid(); string oversizedPath = Path.Combine(root, oversizedId.ToString("N") + ".draft");
        using (var oversized = File.Create(oversizedPath)) oversized.SetLength(3 * 1024 * 1024 + 1L);
        rejected = false;
        try { store.Read(oversizedId); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(oversizedPath), "Oversize encrypted recovery is rejected before decryption and retained");
        store.Remove(oversizedId);
        var unknownId = Guid.NewGuid(); string unknownPath = Path.Combine(root, unknownId.ToString("N") + ".draft");
        File.WriteAllBytes(unknownPath, ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"Version\":99}"), unknownId.ToByteArray(), DataProtectionScope.CurrentUser));
        rejected = false;
        try { store.Read(unknownId); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(unknownPath), "Unknown recovery version is rejected and retained");
        store.Remove(unknownId);
        var maximum = draft with { Text = new string('á', QueryRecoveryStore.MaximumCharacters) };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        store.Save(maximum); var maximumRead = store.Read(maximum.Id); stopwatch.Stop();
        check(maximumRead.Text == maximum.Text, "Maximum recovery text roundtrips without truncation");
        Console.WriteLine($"Recovery measurement: save + decrypt/read {maximum.Text.Length} UTF-16 units in {stopwatch.ElapsedMilliseconds} ms; encrypted bytes {new FileInfo(path).Length} (single synthetic record, not a latency guarantee).");
        check(new FileInfo(path).Length < 8192, "Repetitive maximum Unicode recovery compresses before DPAPI without plaintext storage");
        byte[] legacy = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Draft = draft });
        File.WriteAllBytes(path, ProtectedData.Protect(legacy, draft.Id.ToByteArray(), DataProtectionScope.CurrentUser));
        check(store.Read(draft.Id) == draft, "Recovery still reads legacy escaped JSON records");
        var special = draft with { Text = "SELECT '<script> & \"quoted\"';\n-- \\ slash\t😀" };
        store.Save(special);
        check(store.Read(draft.Id) == special, "Compact JSON preserves quotes controls slashes and non-BMP Unicode");
        store.Remove(maximum.Id);
        string boundedRoot = Path.Combine(root, "bounded-restore");
        for (int group = 0; group < 2; group++)
        {
            using var source = new QueryRecoverySession(boundedRoot);
            for (int index = 0; index < (group == 0 ? 32 : 1); index++) source.Store.Save(new(Guid.NewGuid(), "bounded", "SELECT 1;", SqlFileFormat.Utf8));
        }
        using (var destination = new QueryRecoverySession(boundedRoot))
        {
            var bounded = QueryRecoverySession.Recover(boundedRoot, destination.Store.DirectoryPath, d => destination.Store.Save(d));
            check(bounded.Restored == 32 && bounded.Deferred == 1 && destination.Store.ListIds().Count == 32, "Recovery defers excess records instead of opening unlimited tabs");
            destination.Clear();
        }
        string mixedRoot = Path.Combine(root, "mixed-restore"); string badRecord;
        using (var source = new QueryRecoverySession(mixedRoot))
        {
            source.Store.Save(draft);
            var badId = Guid.NewGuid(); badRecord = Path.Combine(source.Store.DirectoryPath, badId.ToString("N") + ".draft");
            File.WriteAllBytes(badRecord, [1, 2, 3]);
        }
        using (var destination = new QueryRecoverySession(mixedRoot))
        {
            var mixed = QueryRecoverySession.Recover(mixedRoot, destination.Store.DirectoryPath, d => destination.Store.Save(d));
            check(mixed.Restored == 1 && mixed.Failed == 1 && File.Exists(badRecord), "Corrupt record does not prevent recovery of valid neighbor and remains retained");
            destination.Clear();
        }
        string sessionRoot = Path.Combine(root, "sessions");
        var sessionId = Guid.NewGuid();
        using (var session = new QueryRecoverySession(sessionRoot, sessionId))
        {
            bool busy = false;
            try { using var other = new QueryRecoverySession(sessionRoot, sessionId); } catch (IOException) { busy = true; }
            check(busy, "Recovery session lease excludes another window or process");
            session.Store.Save(draft);
        }
        using (var released = new QueryRecoverySession(sessionRoot, sessionId))
        {
            check(released.Store.Read(draft.Id) == draft, "Released recovery session retains draft and can be acquired");
            released.Clear();
        }
        string failureRoot = Path.Combine(root, "failed-transfer");
        string retainedDirectory;
        using (var source = new QueryRecoverySession(failureRoot)) { source.Store.Save(draft); retainedDirectory = source.Store.DirectoryPath; }
        var transfer = QueryRecoverySession.Recover(failureRoot, Path.Combine(failureRoot, "other"), _ => throw new IOException("synthetic destination failure"));
        check(transfer.Failed == 1 && new QueryRecoveryStore(retainedDirectory).Read(draft.Id) == draft, "Failed recovery transfer retains original checkpoint");
        string cleanupRoot = Path.Combine(root, "failed-source-cleanup");
        string cleanupDirectory;
        using (var source = new QueryRecoverySession(cleanupRoot))
        {
            cleanupDirectory = source.Store.DirectoryPath;
            source.Store.Save(draft);
            source.Store.Save(draft with { Id = Guid.NewGuid() });
        }
        using (var destination = new QueryRecoverySession(cleanupRoot))
        {
            FileStream? held = null;
            RecoveryDraft? accepted = null;
            try
            {
                var report = QueryRecoverySession.Recover(cleanupRoot, destination.Store.DirectoryPath, d =>
                {
                    destination.Store.Save(d); accepted = d;
                    held = new FileStream(Path.Combine(cleanupDirectory, d.Id.ToString("N") + ".draft"), FileMode.Open, FileAccess.Read, FileShare.Read);
                }, 1);
                check(report.Restored == 1 && report.Failed == 1 && report.Deferred == 1 && destination.Store.ListIds().Count == 1,
                    "Accepted transfer consumes budget even when source cleanup fails");
            }
            finally { held?.Dispose(); }
            check(accepted is not null && new QueryRecoveryStore(cleanupDirectory).Read(accepted.Id) == accepted && destination.Store.Read(accepted.Id) == accepted,
                "Failed source deletion preserves both durable copies");
            destination.Clear();
        }
        string badLeaseRoot = Path.Combine(root, "bad-lease");
        string broken = Path.Combine(badLeaseRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(broken, ".lease"));
        using (var source = new QueryRecoverySession(badLeaseRoot)) source.Store.Save(draft);
        using (var destination = new QueryRecoverySession(badLeaseRoot))
        {
            var report = QueryRecoverySession.Recover(badLeaseRoot, destination.Store.DirectoryPath, d => destination.Store.Save(d));
            check(report.Failed == 1 && report.Busy == 0 && report.Restored == 1 && Directory.Exists(broken),
                "Invalid lease is reported separately and does not prevent neighboring recovery");
            destination.Clear();
        }
        string? previousState = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", Path.Combine(root, "window-state"));
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show();
            await window.RecoveryCompletion;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            System.Windows.Input.ApplicationCommands.New.Execute(null, window);
            var manager = (AvalonDock.DockingManager)window.FindName("DockManager");
            var query = (QueryView)manager.ActiveContent;
            var editor = (ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor");
            editor.Text = draft.Text;
            check(window.CheckpointQueries() == 1 && window.RecoveryDirectory is not null, "Dirty editor creates a checkpoint in its own session");
            var checkpointStore = new QueryRecoveryStore(window.RecoveryDirectory!);
            check(checkpointStore.Read(query.RecoveryId).Text == editor.Text && query.IsDirty && !query.HasSession, "Checkpoint preserves dirty editor without connection or SQL execution");
            check(window.CheckpointQueries() == 0, "Unchanged editor does not rewrite checkpoint");
            editor.AppendText("-- next");
            check(window.CheckpointQueries() == 1 && checkpointStore.Read(query.RecoveryId).Text.EndsWith("-- next"), "Later edits advance checkpoint revision");
            var timerField = typeof(MainWindow).GetField("recoveryTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var timer = (System.Windows.Threading.DispatcherTimer)timerField.GetValue(window)!;
            timer.Interval = TimeSpan.FromMilliseconds(50);
            var tickObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler observeTick = (_, _) => tickObserved.TrySetResult();
            timer.Tick += observeTick;
            editor.AppendText("-- timer");
            await tickObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await window.RecoveryCheckpointCompletion;
            timer.Tick -= observeTick;
            check(checkpointStore.Read(query.RecoveryId).Text.EndsWith("-- timer"), "Periodic dispatcher checkpoint captures edits without explicit Save");
            timer.Stop();
            await window.RecoveryCheckpointCompletion;
            var titleDocument = manager.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().Single(d => d.Content == query);
            titleDocument.Title = new string('a', 127) + "😀*";
            editor.AppendText("-- title boundary"); window.CheckpointQueries();
            check(checkpointStore.Read(query.RecoveryId).Title == new string('a', 127), "Recovery title limit does not split a surrogate pair");
            editor.AppendText("-- async snapshot");
            check(await window.CheckpointQueriesAsync() == 1 && checkpointStore.Read(query.RecoveryId).Text == editor.Text,
                "Async checkpoint persists immutable editor snapshot");
            check(await window.CheckpointQueriesAsync() == 0, "Async checkpoint skips unchanged revision");
            var io = (RecoveryIoQueue)typeof(MainWindow).GetField("recoveryIo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            using (var releaseIo = new ManualResetEventSlim())
            {
                var blocked = io.RunAsync(() => releaseIo.Wait(TimeSpan.FromSeconds(10)));
                editor.AppendText("-- captured");
                string captured = editor.Text;
                var writing = window.CheckpointQueriesAsync();
                check(ReferenceEquals(writing, window.CheckpointQueriesAsync()), "Concurrent ticks share one bounded checkpoint task");
                bool dispatched = false;
                await window.Dispatcher.InvokeAsync(() => dispatched = true);
                check(dispatched && !writing.IsCompleted, "Dispatcher processes input while recovery worker is blocked");
                editor.AppendText("-- edited during write");
                releaseIo.Set();
                await blocked; await writing;
                check(checkpointStore.Read(query.RecoveryId).Text == captured, "Worker writes captured revision without reading live controls");
                check(await window.CheckpointQueriesAsync() == 1 && checkpointStore.Read(query.RecoveryId).Text == editor.Text,
                    "Editing during checkpoint remains eligible for the next checkpoint");
            }
            editor.AppendText("-- save while checkpoint pending");
            var pendingWrite = window.CheckpointQueriesAsync();
            window.SaveQueryFile(query, Path.Combine(root, "async-save.sql"));
            await pendingWrite;
            check(!File.Exists(Path.Combine(checkpointStore.DirectoryPath, query.RecoveryId.ToString("N") + ".draft")),
                "Save ordered after pending checkpoint cannot resurrect the draft");
            editor.AppendText("-- after async Save");
            check(await window.CheckpointQueriesAsync() == 1, "Stale completion after Save does not suppress new edits");
            string originalCheckpoint = checkpointStore.Read(query.RecoveryId).Text;
            editor.Text = new string('x', QueryRecoveryStore.MaximumCharacters + 1);
            check(window.CheckpointQueries() == 0 && checkpointStore.Read(query.RecoveryId).Text == originalCheckpoint && ((System.Windows.Controls.TextBlock)window.FindName("Status")).Text.Contains("Recovery incomplete"), "Oversize edit keeps last checkpoint and reports incomplete recovery");
            check(await window.CheckpointQueriesAsync() == 0 && checkpointStore.Read(query.RecoveryId).Text == originalCheckpoint,
                "Async checkpoint rejects oversize text before snapshot and preserves prior record");
            editor.Text = "SELECT 'retry recovery';";
            check(window.CheckpointQueries() == 1 && checkpointStore.Read(query.RecoveryId).Text == editor.Text, "Checkpoint retries after text returns within budget");
            string queryDraftPath = Path.Combine(checkpointStore.DirectoryPath, query.RecoveryId.ToString("N") + ".draft");
            using (var lockedDraft = new FileStream(queryDraftPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                string savedSql = Path.Combine(root, "saved-query.sql");
                window.SaveQueryFile(query, savedSql);
                check(!query.IsDirty && File.ReadAllText(savedSql) == editor.Text && ((System.Windows.Controls.TextBlock)window.FindName("Status")).Text.Contains("cleanup failed"), "Successful SQL save remains saved when recovery cleanup fails");
                window.Close(); await window.CloseCompletion;
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                check(window.IsVisible, "Window close is retained while recovery cleanup is blocked");
            }
            await window.CheckpointQueriesAsync();
            check(!File.Exists(queryDraftPath), "Async checkpoint retries pending cleanup after lock release");
            editor.AppendText("-- new unsaved edit"); window.CheckpointQueries();
            check(File.Exists(queryDraftPath), "Editing after Save creates a fresh checkpoint");
            window.SaveQueryFile(query, query.FilePath!, true);
            check(!File.Exists(queryDraftPath), "Saving SQL removes its checkpoint immediately");
            editor.AppendText("-- discard fixture"); window.CheckpointQueries();
            query.IsDirty = false; // simulate already accepted discard, avoiding native confirmation in tests
            manager.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().Single(d => d.Content == query).Close();
            await window.TabCloseCompletion;
            check(!File.Exists(queryDraftPath), "Closing an accepted individual tab removes its draft");
            System.Windows.Input.ApplicationCommands.New.Execute(null, window);
            var closingDocument = manager.Layout.Descendents().OfType<LayoutDocument>().Single();
            var closingQuery = (QueryView)closingDocument.Content;
            ((ICSharpCode.AvalonEdit.TextEditor)closingQuery.FindName("Editor")).Text = "SELECT 'pending close'";
            var closingCheckpoint = window.CheckpointQueriesAsync();
            closingQuery.IsDirty = false; // Accepted discard fixture; no native confirmation automation.
            closingDocument.Close();
            await window.TabCloseCompletion;
            await closingCheckpoint;
            check(!File.Exists(Path.Combine(checkpointStore.DirectoryPath, closingQuery.RecoveryId.ToString("N") + ".draft")),
                "Accepted tab close ordered after async checkpoint leaves no orphan draft");
            System.Windows.Input.ApplicationCommands.New.Execute(null, window);
            var finalQuery = (QueryView)manager.Layout.Descendents().OfType<LayoutDocument>().Single().Content;
            ((ICSharpCode.AvalonEdit.TextEditor)finalQuery.FindName("Editor")).Text = "SELECT 'window closing'";
            var finalCheckpoint = window.CheckpointQueriesAsync();
            finalQuery.IsDirty = false;

            query.IsDirty = false;
            window.Close(); await window.CloseCompletion;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(!File.Exists(Path.Combine(checkpointStore.DirectoryPath, query.RecoveryId.ToString("N") + ".draft")), "Accepted normal window close removes its checkpoints");
            await finalCheckpoint;
            check(window.CheckpointQueries() == 0 && !Directory.Exists(checkpointStore.DirectoryPath), "Closed window cannot recreate orphan recovery data");
            check(await window.CheckpointQueriesAsync() == 0 && !Directory.Exists(checkpointStore.DirectoryPath),
                "Async checkpoint after accepted window close cannot recreate recovery directory");
            string recoveryRoot = Path.Combine(root, "window-state", "recovery");
            string orphanDirectory;
            using (var orphan = new QueryRecoverySession(recoveryRoot)) { orphan.Store.Save(draft); orphanDirectory = orphan.Store.DirectoryPath; }
            using var active = new QueryRecoverySession(recoveryRoot);
            active.Store.Save(draft with { Id = Guid.NewGuid(), Text = "SELECT 'active window';" });
            var recoveredWindow = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            try
            {
                recoveredWindow.Show();
                await recoveredWindow.RecoveryCompletion;
                await recoveredWindow.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var recoveredManager = (AvalonDock.DockingManager)recoveredWindow.FindName("DockManager");
                var recoveredDocuments = recoveredManager.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().ToArray();
                check(recoveredDocuments.Length == 1, "Startup restores inactive draft but does not steal active session");
                var recoveredQuery = (QueryView)recoveredDocuments[0].Content;
                var recoveredEditor = (ICSharpCode.AvalonEdit.TextEditor)recoveredQuery.FindName("Editor");
                check(recoveredEditor.Text == draft.Text && recoveredQuery.IsDirty && recoveredQuery.FilePath is null && !recoveredQuery.HasSession && recoveredQuery.FileFormat == draft.Format, "Recovered query is dirty untitled disconnected and preserves format");
                check(recoveredDocuments[0].Title.StartsWith("Recovered - "), "Recovered tab visibly identifies its origin");
                check(new QueryRecoveryStore(recoveredWindow.RecoveryDirectory!).Read(recoveredQuery.RecoveryId).Text == draft.Text && !Directory.Exists(orphanDirectory), "Recovery transfers durable checkpoint before removing old session");
                check(recoveredWindow.RecoverPendingQueries().Restored == 0, "Repeated recovery does not duplicate already transferred draft");
                for (int i = 0; i < 31; i++) System.Windows.Input.ApplicationCommands.New.Execute(null, recoveredWindow);
                string deferredDirectory;
                var deferredDraft = draft with { Id = Guid.NewGuid() };
                using (var extra = new QueryRecoverySession(recoveryRoot)) { extra.Store.Save(deferredDraft); deferredDirectory = extra.Store.DirectoryPath; }
                var capacity = recoveredWindow.RecoverPendingQueries();
                check(capacity.Restored == 0 && capacity.Deferred == 1 && capacity.Failed == 0 && recoveredManager.Layout.Descendents().OfType<LayoutDocument>().Count() == 32,
                    "Full window defers recovery without creating transient tabs or failures");
                check(new QueryRecoveryStore(deferredDirectory).Read(deferredDraft.Id) == deferredDraft, "Deferred draft remains intact in source session");
                recoveredManager.Layout.Descendents().OfType<LayoutDocument>().First(d => d.Content != recoveredQuery).Close();
                await recoveredWindow.TabCloseCompletion;
                check(recoveredWindow.RecoverPendingQueries().Restored == 1 && !Directory.Exists(deferredDirectory), "Freeing a tab permits retry of deferred recovery");
                recoveredQuery.IsDirty = false;
            }
            finally
            {
                foreach (var q in ((AvalonDock.DockingManager)recoveredWindow.FindName("DockManager")).Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) q.IsDirty = false;
                recoveredWindow.Close(); await recoveredWindow.CloseCompletion;
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                active.Clear();
            }
        }
        finally
        {
            foreach (var query in ((AvalonDock.DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) query.IsDirty = false;
            window.Close(); await window.CloseCompletion;
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previousState);
        }
        await RecoveryIoChecks.RunAsync(check);
        await RecoveryAsyncChecks.RunAsync(check);
        await RecoveryProcessChecks.RunAsync(root, check);
        await RecoveryPreferenceChecks.RunAsync(check);
    }
}
