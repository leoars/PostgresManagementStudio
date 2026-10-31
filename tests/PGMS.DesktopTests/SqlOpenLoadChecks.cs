using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class SqlOpenLoadChecks
{
    public static async Task RunAsync(Action<bool, string> check, string scenario = "eight-documents")
    {
        bool large = scenario == "near-16MiB";
        bool longLine = scenario == "long-line";
        string root = Path.GetFullPath(Path.Combine("artifacts", "open-load", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string text = longLine ? "--" + new string('x', 262142) : large ? string.Concat(Enumerable.Repeat("--" + new string('x', 253) + "\n", 65535)) : string.Concat(Enumerable.Repeat("-- ação 漢字 SQL\r\n", 32768));
        string[] paths = Enumerable.Range(0, large || longLine ? 1 : 8).Select(i => Path.Combine(root, $"load-{i}.sql")).ToArray();
        foreach (string path in paths) SqlFileStore.Write(path, text, SqlFileFormat.Utf8Bom);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", root);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show(); await window.RecoveryCompletion;
            var phases = new List<object>();
            window.UiPhaseMeasured = (phase, milliseconds) => phases.Add(new { phase, milliseconds });
            var memory = new LoadMemorySampler();
            int beats = 0; long last = 0, maxGap = 0;
            var heartbeatTimes = new List<long> { 0 };
            var counters = new LoadIntervalCounters();
            var watch = Stopwatch.StartNew();
            LoadTraceEvents.Log.Boundary(scenario, "start");
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { long now = watch.ElapsedMilliseconds; heartbeatTimes.Add(now); maxGap = Math.Max(maxGap, now - last); last = now; beats++; };
            timer.Start();
            QueryView[] opened;
            long openCompletedMs;
            try
            {
                opened = await Task.WhenAll(paths.Select(window.OpenQueryFileAsync));
                openCompletedMs = watch.ElapsedMilliseconds;
                LoadTraceEvents.Log.Boundary(scenario, "opened");
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                LoadTraceEvents.Log.Boundary(scenario, "end");
            }
            finally { timer.Stop(); await memory.DisposeAsync(); }
            long elapsed = watch.ElapsedMilliseconds;
            var intervalCounters = counters.Complete();
            heartbeatTimes.Add(elapsed);
            maxGap = Math.Max(maxGap, elapsed - last);
            check(opened.Length == paths.Length && opened.Distinct().Count() == paths.Length, "Open load creates exactly the requested distinct documents");
            check(opened.All(q => ((TextEditor)q.FindName("Editor")).Text == text && q.FileFormat == SqlFileFormat.Utf8Bom && !q.IsDirty && !q.HasSession), "Open load preserves complete Unicode and disconnected clean state");
            check(opened.All(q => q.FileVersion == SqlFileStore.CaptureVersion(q.FilePath!)), "Open load retains exact on-disk fingerprints");
            var edited = opened[0]; var editor = (TextEditor)edited.FindName("Editor");
            editor.AppendText("-- edit"); editor.Undo();
            check(editor.Text == text && edited.IsDirty, "Loaded large text supports edit and Undo without losing contents");
            string savedPath = Path.Combine(root, "saved.sql");
            await window.SaveQueryFileAsync(edited, savedPath);
            check(!edited.IsDirty && SqlFileStore.Read(savedPath).Text == text && File.ReadAllBytes(savedPath).SequenceEqual(File.ReadAllBytes(paths[0])), "Loaded large text saves exact original bytes after Undo");
            string json = JsonSerializer.Serialize(new { milestone = BuildInfo.Milestone, scenario, documents = paths.Length, charactersPerDocument = text.Length,
                bytesPerFile = new FileInfo(paths[0]).Length, openCompletedMs, layoutAndIdleMs = elapsed - openCompletedMs, elapsedMs = elapsed, inputHeartbeats = beats, maxInputGapMs = maxGap,
                measurementSchemaVersion = 2, heartbeatTimesMs = heartbeatTimes, intervalCounters, uiPhases = phases,
                memory = new { memory.Baseline, memory.Maximum, memory.Samples, intervalMs = 20 },
                limitation = "Single local synthetic run, excludes startup and fixture generation. uiPhases are synchronous service times, view-total includes its subphases; layout and asynchronous waits are outside these scopes. Includes editor construction plus explicit layout and Dispatcher idle; idle is not a compositor presentation timestamp; no network latency or absolute memory peak guarantee. Samples cover whole process, including retained fixture strings." }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(root, "measurement.json"), json);
            Console.WriteLine(json); Console.WriteLine("Open load report: " + root);
        }
        finally
        {
            foreach (var q in ((DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) q.IsDirty = false;
            window.Close(); await window.CloseCompletion;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }
}
