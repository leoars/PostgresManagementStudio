using System.IO;
using System.Reflection;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using PGMS.Desktop;

internal static class QueryTextSnapshotChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string original = "SELECT 'ação 漢字 😀';\r\n";
        var document = new TextDocument(original);
        var snapshot = QueryTextSnapshot.Capture(document);
        document.Text = "newer";
        check(await Task.Run(snapshot.Materialize) == original, "Immutable snapshot retains Unicode after live document replacement");
        check(snapshot.Length == original.Length && document.Text == "newer", "Snapshot length and materialization do not alter live document");
        check((await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(snapshot.Materialize)))).All(t => t == original), "Independent workers read the same immutable snapshot");
        check(QueryTextSnapshot.Capture(new TextDocument()).Materialize() == "", "Empty snapshot materializes without live editor");

        string root = Path.GetFullPath(Path.Combine("artifacts", "snapshot-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", root);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show(); await window.RecoveryCompletion;
            ApplicationCommands.New.Execute(null, window);
            var query = ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>().Single();
            var editor = (TextEditor)query.FindName("Editor");
            editor.Text = original;
            string file = Path.Combine(root, "snapshot.sql");
            await WithBlockedQueue(window, "fileIo", async release =>
            {
                Task save = window.SaveQueryFileAsync(query, file);
                editor.AppendText("-- later");
                release(); await save;
                check(SqlFileStore.Read(file).Text == original, "Delayed Save worker writes the captured version, not a later edit");
                check(query.IsDirty && editor.Text == original + "-- later", "Save completion retains later text and dirty state");
            });
            await window.SaveQueryFileAsync(query, file, true);
            check(!query.IsDirty && SqlFileStore.Read(file).Text == editor.Text, "Next Save persists the newer revision and clears dirty");
            editor.Text = original;
            await WithBlockedQueue(window, "recoveryIo", async release =>
            {
                Task<int> checkpoint = window.CheckpointQueriesAsync();
                editor.AppendText("-- after checkpoint");
                release(); check(await checkpoint == 1, "Delayed checkpoint accepts its captured revision");
                var store = new QueryRecoveryStore(window.RecoveryDirectory!);
                check(store.Read(query.RecoveryId).Text == original, "Checkpoint worker materializes the pre-edit snapshot");
                check(query.IsDirty && editor.Text.EndsWith("-- after checkpoint"), "Checkpoint does not replace newer live edits");
            });
            check(await window.CheckpointQueriesAsync() == 1, "Captured checkpoint revision does not suppress a later edit");
            check(new QueryRecoveryStore(window.RecoveryDirectory!).Read(query.RecoveryId).Text == editor.Text, "Next checkpoint retains the complete newer revision");
        }
        finally
        {
            foreach (var query in ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) query.IsDirty = false;
            window.Close(); await window.CloseCompletion;
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }

    private static async Task WithBlockedQueue(MainWindow window, string field, Func<Action, Task> test)
    {
        var queue = (RecoveryIoQueue)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = queue.RunAwaitedAsync(async () => { entered.SetResult(); await release.Task; return true; });
        try { await entered.Task; await test(() => release.TrySetResult()); }
        finally { release.TrySetResult(); await blocker; }
    }
}
