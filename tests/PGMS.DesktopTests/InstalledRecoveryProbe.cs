using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using PGMS.Desktop;

internal static class InstalledRecoveryProbe
{
    internal static int Run(string executable, string state, string report)
    {
        Process? child = null;
        using var watchdog = new Timer(_ => { try { if (child is not null && !child.HasExited) child.Kill(true); } catch (InvalidOperationException) { } }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        try
        {
            state = InstalledRecoveryFixture.Safe(state); report = InstalledRecoveryFixture.Safe(report);
            InstalledRecoveryFixture.ValidateState(state);
            if (File.Exists(report)) throw new IOException("Existing evidence preserved.");
            string path = Path.GetFullPath(executable), allowed = Path.GetFullPath("artifacts/acceptance") + Path.DirectorySeparatorChar;
            if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed synthetic package required.");
            path = InstalledRecoveryFixture.Safe(path);
            string package = Path.GetDirectoryName(path)!;
            var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = package, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["PGMS_STATE_DIRECTORY"] = state;
            string empty = Path.Combine(state, "empty-runtime"); Directory.CreateDirectory(empty);
            start.Environment["DOTNET_ROOT"] = empty; start.Environment["DOTNET_ROOT_X64"] = empty; start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            child = Process.Start(start) ?? throw new IOException("Own launch failed.");
            AutomationElement Wait(Func<AutomationElement?> find)
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                do { if (child.HasExited) throw new IOException("Own child exited before observation."); var value = find(); if (value is not null) return value; Thread.Sleep(100); } while (DateTime.UtcNow < deadline);
                throw new TimeoutException("Own recovery UI not observed.");
            }
            var main = Wait(() => { child.Refresh(); return child.MainWindowHandle == IntPtr.Zero ? null : AutomationElement.FromHandle(child.MainWindowHandle); });
            if (main.Current.ProcessId != child.Id || !main.Current.Name.Contains("PGMS", StringComparison.Ordinal)) throw new IOException("Unexpected child UI.");
            var runtime = child.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)).FileName;
            if (!runtime.Equals(Path.Combine(package, "coreclr.dll"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed runtime not loaded.");
            Wait(() => main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Recovery: 3 queries restored; 0 errors; 0 deferred. Review recovered tabs and use Save As.")));
            var editor = Wait(() => main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "SQL editor")));
            string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\r', '\n');
            string observed = ((TextPattern)editor.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
            if (!InstalledRecoveryFixture.Items.Any(i => Normalize(i.Text) == Normalize(observed))) throw new IOException("Visible editor did not contain one complete synthetic draft.");
            child.Kill(true); child.WaitForExit(); // Deliberate crash of this fixture only, retains dirty recovery.
            InstalledRecoveryFixture.ValidateState(state);
            string root = InstalledRecoveryFixture.Safe(Path.Combine(state, "recovery"));
            var drafts = Directory.EnumerateDirectories(root).SelectMany(d =>
            {
                InstalledRecoveryFixture.Safe(d);
                var store = new QueryRecoveryStore(d);
                return store.ListIds().Select(store.Read).ToArray();
            }).ToArray();
            if (drafts.Length != 3 || InstalledRecoveryFixture.Items.Any(i => drafts.Count(d => d.Text == i.Text && d.Format == i.Format) != 1)) throw new IOException("Complete encrypted draft text/format preservation failed.");
            var files = Directory.EnumerateFiles(root, "*.draft", SearchOption.AllDirectories).Select(f => new { path = Path.GetRelativePath(state, f), sha256 = InstalledRecoveryFixture.Hash(InstalledRecoveryFixture.Safe(f)) }).ToArray();
            using var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(output, new { schemaVersion = 1, valid = true, executable = path, state, runtime, restored = 3, completeEncryptedTextAndFormats = true, oneVisibleEditorObserved = true,
                allEditorsIndividuallyObserved = false, forcedOwnChildTermination = true, connectionAttempted = false, clipboardAccessed = false, cleanWindowsCertified = false, releaseReady = false, files }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine("Installed synthetic recovery verified: " + report); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (child is not null) { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } child.Dispose(); } }
    }
}
