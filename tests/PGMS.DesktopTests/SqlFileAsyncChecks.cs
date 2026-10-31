using System.IO;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class SqlFileAsyncChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "file-async", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", root);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show(); await window.RecoveryCompletion;
            string original = Path.Combine(root, "original.sql"), target = Path.Combine(root, "snapshot.sql");
            SqlFileStore.Write(original, "SELECT 1;", SqlFileFormat.Utf8);
            var query = window.OpenQueryFile(original);
            var editor = (TextEditor)query.FindName("Editor");
            editor.AppendText("-- captured"); string captured = editor.Text;
            var io = (RecoveryIoQueue)typeof(MainWindow).GetField("fileIo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            using var release = new ManualResetEventSlim();
            var blocker = io.RunAsync(() => release.Wait(TimeSpan.FromSeconds(10)));
            var saving = window.SaveQueryFileAsync(query, target);
            bool responsive = false; await window.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Input);
            check(responsive && window.IsSaving(query) && !saving.IsCompleted, "Async SQL save leaves Dispatcher responsive during blocked IO");
            bool duplicate = false;
            try { await window.SaveQueryFileAsync(query, target); } catch (InvalidOperationException) { duplicate = true; }
            check(duplicate && !window.TryOpenQueryFile(target, out _), "Pending save rejects duplicate writes and opening its reserved path");
            editor.AppendText("-- newer edit");
            release.Set(); await blocker; await saving;
            check(File.ReadAllText(target) == captured && query.IsDirty && editor.Text.EndsWith("-- newer edit"), "Async Save persists captured revision and preserves newer unsaved edits");
            await window.SaveQueryFileAsync(query, target, true);
            check(!query.IsDirty && File.ReadAllText(target) == editor.Text && !window.IsSaving(query), "Next async Save persists latest revision and clears pending state");
            editor.AppendText("-- routed Save");
            System.Windows.Input.ApplicationCommands.Save.Execute(null, window);
            check(window.IsSaving(query) && !System.Windows.Input.ApplicationCommands.Save.CanExecute(null, window), "Save command routes to async write and disables duplicate invocation");
            await window.SaveCompletion;
            check(!query.IsDirty && File.ReadAllText(target) == editor.Text && System.Windows.Input.ApplicationCommands.Save.CanExecute(null, window), "Routed Save finishes with exact contents and re-enables command");
            editor.AppendText("-- conflict");
            File.WriteAllText(target, "SELECT 'external change';");
            bool conflict = false;
            try { await window.SaveQueryFileAsync(query, target, true); } catch (IOException) { conflict = true; }
            check(conflict && query.IsDirty && File.ReadAllText(target) == "SELECT 'external change';" && !window.IsSaving(query), "Async conflict preserves external file and unsaved editor then releases Save state");
            string rescued = Path.Combine(root, "rescued.sql");
            await window.SaveQueryFileAsync(query, rescued);
            check(!query.IsDirty && File.ReadAllText(rescued) == editor.Text, "Async Save As recovers conflicting text to another destination");
            var recoveryIo = (RecoveryIoQueue)typeof(MainWindow).GetField("recoveryIo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            using var closeRelease = new ManualResetEventSlim();
            var closeBlocker = recoveryIo.RunAsync(() => closeRelease.Wait(TimeSpan.FromSeconds(10)));
            var close = window.CloseQueryAsync(query);
            bool closeResponsive = false; await window.Dispatcher.InvokeAsync(() => closeResponsive = true);
            check(closeResponsive && !close.IsCompleted && !query.IsEnabled && !System.Windows.Input.ApplicationCommands.Save.CanExecute(null, window), "Tab cleanup keeps Dispatcher responsive and prevents Save during closure");
            bool reopenRefused = false;
            try { await window.OpenQueryFileAsync(rescued); } catch (IOException) { reopenRefused = true; }
            check(reopenRefused && !window.TryOpenQueryFile(rescued, out _), "Open cannot reactivate a tab awaiting close cleanup");
            closeRelease.Set(); await closeBlocker;
            check(await close && !((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Any(d => d.Content == query), "Tab closes only after asynchronous cleanup completes");
            check((await window.OpenQueryFileAsync(rescued)).FilePath == rescued, "Open succeeds with fresh document after prior tab closes");

        }
        finally
        {
            foreach (var q in ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) q.IsDirty = false;
            window.Close(); await window.CloseCompletion; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }
}
