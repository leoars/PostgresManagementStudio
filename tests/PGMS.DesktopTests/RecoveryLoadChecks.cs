using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AvalonDock.Layout;
using PGMS.Desktop;

internal static class RecoveryLoadChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string state = Path.GetFullPath(Path.Combine("artifacts", "recovery-load", Guid.NewGuid().ToString("N")));
        string root = Path.Combine(state, "recovery");
        string text = new('á', QueryRecoveryStore.MaximumCharacters);
        var timer = Stopwatch.StartNew();
        using (var source = new QueryRecoverySession(root))
            for (int i = 0; i < QueryRecoveryStore.MaximumDocuments; i++)
                source.Store.Save(new(Guid.NewGuid(), $"load-{i}.sql", text, SqlFileFormat.Utf8));
        long saveMs = timer.ElapsedMilliseconds;
        long encryptedBytes = Directory.EnumerateFiles(root, "*.draft", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        long heapBefore = GC.GetTotalMemory(true);
        long allocationsBefore = GC.GetTotalAllocatedBytes(true);
        MainWindow? window = null;
        try
        {
            var memory = new LoadMemorySampler();
            LoadTraceEvents.Log.Boundary("restore", "start");
            timer.Restart();
            var restoreCounters = new LoadIntervalCounters();
            var restoreTimes = new List<long> { 0 };
            var phases = new List<(string Phase, double Milliseconds)>();
            window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            window.UiPhaseMeasured = (phase, milliseconds) => phases.Add((phase, milliseconds));
            int restoreHeartbeats = 0;
            long restoreLastBeat = 0, restoreMaxGapMs = 0;
            var restoreHeartbeat = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            restoreHeartbeat.Tick += (_, _) => { long now = timer.ElapsedMilliseconds; restoreTimes.Add(now); restoreMaxGapMs = Math.Max(restoreMaxGapMs, now - restoreLastBeat); restoreLastBeat = now; restoreHeartbeats++; };
            restoreHeartbeat.Start();
            try { window.Show(); await window.RecoveryCompletion; await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); }
            finally { restoreHeartbeat.Stop(); await memory.DisposeAsync(); }
            long restoreMs = timer.ElapsedMilliseconds;
            restoreMaxGapMs = Math.Max(restoreMaxGapMs, restoreMs - restoreLastBeat);
            restoreTimes.Add(restoreMs);
            var restoreIntervalCounters = restoreCounters.Complete();
            LoadTraceEvents.Log.Boundary("restore", "end");
            check(memory.Samples > 1 && memory.Maximum.ManagedBytes >= memory.Baseline.ManagedBytes, "Recovery load records sampled process and managed memory during restore");
            var documents = ((AvalonDock.DockingManager)window.FindName("DockManager")).Layout.Descendents()
                .OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>().ToArray();
            check(documents.Length == 32, "Load restores 32 maximum-size documents");
            check(documents.All(q => ((ICSharpCode.AvalonEdit.TextEditor)q.FindName("Editor")).Text == text && q.IsDirty && !q.HasSession && q.FilePath is null), "Load preserves all text and disconnected recovery state");
            long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocationsBefore;
            long retainedHeapDelta = GC.GetTotalMemory(true) - heapBefore;
            foreach (var query in documents)
                ((ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor")).Document.Replace(0, 1, "b");
            int heartbeatCount = 0;
            long lastBeat = 0, maximumGapMs = 0;
            var heartbeat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            var checkpointWatch = Stopwatch.StartNew();
            var checkpointCounters = new LoadIntervalCounters();
            var checkpointTimes = new List<long> { 0 };
            heartbeat.Tick += (_, _) => { long now = checkpointWatch.ElapsedMilliseconds; checkpointTimes.Add(now); maximumGapMs = Math.Max(maximumGapMs, now - lastBeat); lastBeat = now; heartbeatCount++; };
            heartbeat.Start();
            LoadTraceEvents.Log.Boundary("checkpoint", "start");
            var checkpoint = window.CheckpointQueriesAsync();
            long snapshotMs = checkpointWatch.ElapsedMilliseconds;
            int saved;
            try { saved = await checkpoint; }
            finally { heartbeat.Stop(); }
            long checkpointMs = checkpointWatch.ElapsedMilliseconds;
            checkpointTimes.Add(checkpointMs);
            var checkpointIntervalCounters = checkpointCounters.Complete();
            LoadTraceEvents.Log.Boundary("checkpoint", "end");
            maximumGapMs = Math.Max(maximumGapMs, checkpointMs - lastBeat);
            check(saved == 32, "Load asynchronously checkpoints all 32 modified documents");
            var checkpointStore = new QueryRecoveryStore(window.RecoveryDirectory!);
            check(documents.All(q => checkpointStore.Read(q.RecoveryId).Text == "b" + text[1..]), "Load checkpoint preserves every complete modified document");
            var report = new { milestone = BuildInfo.Milestone, documents = documents.Length, charactersPerDocument = text.Length,
                memory = new { memory.Baseline, memory.Maximum, memory.Samples, intervalMs = 20 },
                uiPhases = phases.GroupBy(p => p.Phase).Select(g => new { phase = g.Key, count = g.Count(), totalMs = g.Sum(p => p.Milliseconds), maxMs = g.Max(p => p.Milliseconds) }).ToArray(),
                measurementSchemaVersion = 2, restoreTimesMs = restoreTimes, checkpointTimesMs = checkpointTimes,
                restoreIntervalCounters, checkpointIntervalCounters,
                saveMs, restoreMs, restoreHeartbeats, restoreMaxGapMs, encryptedBytes, allocatedBytes, retainedHeapDelta,
                snapshotMs, checkpointMs, heartbeatCount, maximumGapMs,
                limitation = "Single synthetic run; includes WPF startup. Heap delta is post-GC retained memory; sampled maxima can miss peaks and include the whole process, not just recovery. Input-priority restore timer and Background-priority checkpoint timer are different probes. UI scopes measure synchronous service, view-total includes its subphases. Checkpoint begins after edits that can invalidate layout; its timer does not isolate serialization cost. No latency SLA." };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(state, "measurement.json"), json);
            Console.WriteLine(json);
            Console.WriteLine("Recovery load report: " + state);
            foreach (var query in documents) query.IsDirty = false;
        }
        finally
        {
            if (window is not null)
            {
                foreach (var query in ((AvalonDock.DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d => d.Content).OfType<QueryView>()) query.IsDirty = false;
                window.Close(); await window.CloseCompletion;
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }
}
