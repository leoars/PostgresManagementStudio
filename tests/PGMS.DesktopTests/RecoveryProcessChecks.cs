using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class RecoveryProcessChecks
{
    private const string Original = "SELECT 'original';";
    private const string Edited = "SELECT 'unsaved crash recovery ação 😀';";

    // Test executable only; no production command-line hook is distributed.
    public static void RunChild(string state)
    {
        state = Path.GetFullPath(state);
        string allowed = Path.GetFullPath("artifacts/recovery-tests") + Path.DirectorySeparatorChar;
        if (!state.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Fixture path is outside recovery-tests.");
        Directory.CreateDirectory(state);
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        string original = Path.Combine(state, "original.sql");
        SqlFileStore.Write(original, Original, SqlFileFormat.Utf16LittleEndian);
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PGMS;component/Resources/Shell.xaml") });
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        window.Loaded += (_, _) =>
        {
            var query = window.OpenQueryFile(original);
            ((TextEditor)query.FindName("Editor")).Text = Edited;
            if (window.CheckpointQueries() != 1) throw new InvalidOperationException("Child checkpoint failed.");
            File.WriteAllText(Path.Combine(state, "ready"), "checkpoint persisted");
        };
        app.Run(window);
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string state = Path.Combine(root, "forced-process");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.CurrentDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(RecoveryProcessChecks).Assembly.Location);
        start.ArgumentList.Add("--recovery-child"); start.ArgumentList.Add(state);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(Path.Combine(state, "ready")) && !process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (process.HasExited) throw new Exception("Recovery child exited early: " + await errors);
            check(File.Exists(Path.Combine(state, "ready")), "Real child process persists editor checkpoint before interruption");
            int called = 0;
            var activeReport = QueryRecoverySession.Recover(Path.Combine(state, "recovery"), Path.Combine(state, "other"), _ => called++);
            check(activeReport.Busy == 1 && called == 0, "Recovery does not consume another live process's draft");
            process.Kill(true); await process.WaitForExitAsync();
            _ = await output; _ = await errors;
            string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
            var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            try
            {
                window.Show();
                await window.RecoveryCompletion;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var documents = ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().ToArray();
                check(documents.Length == 1, "Startup reclaims checkpoint after forced process termination");
                var query = (QueryView)documents[0].Content;
                check(((TextEditor)query.FindName("Editor")).Text == Edited && query.IsDirty && query.FilePath is null && !query.HasSession, "Crash recovery restores exact unsaved text without reconnecting or assigning original file");
                check(SqlFileStore.Read(Path.Combine(state, "original.sql")).Text == Original, "Crash recovery never overwrites original SQL file");
                string recoveredPath = Path.Combine(state, "recovered.sql");
                window.SaveQueryFile(query, recoveredPath);
                check(SqlFileStore.Read(recoveredPath).Text == Edited && SqlFileStore.Read(recoveredPath).Format == SqlFileFormat.Utf16LittleEndian, "Recovered process draft can be saved separately with original format");
            }
            finally
            {
                foreach (var query in ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) query.IsDirty = false;
                window.Close(); await window.CloseCompletion; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
            }
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
