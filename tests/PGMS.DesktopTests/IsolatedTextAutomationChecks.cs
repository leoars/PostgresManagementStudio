using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PGMS.Desktop;

internal static class IsolatedTextAutomationChecks
{
    private const string InitialText = "SELECT 'alpha';\r\nSELECT 'beta';\r\n";
    private static string Safe(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Workspace artifacts only.");
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((Directory.Exists(parent) || File.Exists(parent)) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked evidence paths refused.");
        return path;
    }
    private static void Verify(string[] args)
    {
        if (args[0].StartsWith("--uia-desktop-", StringComparison.Ordinal)) UiaDesktopIsolation.Verify(args[1], args[2]);
        else ClipboardIsolation.Verify(args[1], args[2]);
    }
    internal static int Run(string root, bool inactiveDesktopOnly = false, bool shell = false)
    {
        root = Safe(root);
        if (Directory.Exists(root)) throw new IOException("New evidence directory required.");
        Directory.CreateDirectory(root);
        try
        {
            if (inactiveDesktopOnly)
            {
                using var isolation = new UiaDesktopIsolation();
                isolation.Run(Path.Combine(root, "client.json"), shell ? "--uia-desktop-shell-client-child" : "--uia-desktop-client-child");
                RunContainmentCheck(root, isolation);
                isolation.AssertInputUnchanged();
            }
            else
            {
                using var isolation = new ClipboardIsolation();
                isolation.RunContained("--uia-client-child", Path.Combine(root, "client.json"));
            }
            Console.WriteLine(File.ReadAllText(Path.Combine(root, "client.json")));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    internal static int RunContainmentChild(string[] args)
    {
        Verify(args); string report = Safe(args[3]);
        if (args[0] == "--uia-desktop-sleeper-child")
        {
            File.WriteAllText(report + ".ready", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PGMS.DesktopTests.exe")) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "--uia-desktop-sleeper-child", args[1], args[2], report }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Owned cleanup child launch failed.");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(report + ".ready"))
        {
            if (child.HasExited || wait.Elapsed > TimeSpan.FromSeconds(5)) throw new IOException("Owned cleanup child did not become ready.");
            Thread.Sleep(20);
        }
        using (var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, new { processId = child.Id, startedUtcTicks = child.StartTime.ToUniversalTime().Ticks, executable = start.FileName });
        return 42; // Deliberate failure with a live descendant: parent job must clean it up.
    }
    private static void RunContainmentCheck(string root, UiaDesktopIsolation isolation)
    {
        string spawned = Path.Combine(root, "contained-descendant.json"); bool expectedFailure = false;
        try { isolation.Run(spawned, "--uia-desktop-fail-child"); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Isolated test child failed (42):", StringComparison.Ordinal)) { expectedFailure = true; }
        if (!expectedFailure) throw new IOException("Expected containment fixture failure not observed.");
        using var data = JsonDocument.Parse(File.ReadAllText(spawned));
        int id = data.RootElement.GetProperty("processId").GetInt32();
        long started = data.RootElement.GetProperty("startedUtcTicks").GetInt64();
        bool gone = false;
        try
        {
            using var process = Process.GetProcessById(id);
            gone = process.StartTime.ToUniversalTime().Ticks != started || process.WaitForExit(3000);
            if (!gone)
            {
                // Only this test-created descendant, checked by start time and image.
                if (!string.Equals(process.MainModule?.FileName, data.RootElement.GetProperty("executable").GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup process identity changed.");
                process.Kill(); process.WaitForExit(3000);
            }
        }
        catch (ArgumentException) { gone = true; }
        if (!gone) throw new IOException("Job did not terminate its owned descendant after failure.");
        isolation.AssertInputUnchanged();
        using var output = new FileStream(Path.Combine(root, "containment.json"), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, new { expectedChildExitCode = 42, descendantTerminatedByJob = true, inputDesktopUnchanged = true, inputInjected = false, clipboardAccessed = false });
    }
    internal static int RunFixture(string[] args)
    {
        try { return RunFixtureCore(args); }
        catch (Exception ex)
        {
            File.WriteAllText(Safe(Path.Combine(args[3], "fixture-failure.txt")), ex.ToString());
            return 1;
        }
    }
    private static int RunFixtureCore(string[] args)
    {
        Verify(args);
        string root = Safe(args[3]);
        void Phase(string phase) => File.AppendAllText(Path.Combine(root, "fixture-phases.txt"), phase + "\n");
        Phase("isolation-verified");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => File.WriteAllText(Path.Combine(root, "unhandled-fixture.txt"), e.ExceptionObject.ToString());
        bool shell = args[0].Contains("-shell-", StringComparison.Ordinal);
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", Path.Combine(root, "state"));
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) => File.WriteAllText(Path.Combine(root, "dispatcher-fixture.txt"), e.Exception.ToString());
        Phase("application-created");
        SqlTextEditor editor; Window window; DockPanel panel; QueryView? query = null;
        if (shell)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PGMS;component/Resources/Shell.xaml") });
            Phase("shell-resources-loaded");
            var main = new MainWindow { Width = 1000, Height = 600, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
            Phase("main-window-created");
            string sql = Path.Combine(root, "fixture.sql");
            using (var writer = new StreamWriter(new FileStream(sql, FileMode.CreateNew, FileAccess.Write))) writer.Write(InitialText);
            query = main.OpenQueryFile(sql);
            Phase("synthetic-query-opened");
            editor = (SqlTextEditor)query.FindName("Editor"); editor.WordWrap = true;
            window = main; panel = (DockPanel)main.Content;
        }
        else
        {
            editor = new SqlTextEditor { Text = InitialText, FontFamily = new FontFamily("Consolas"), FontSize = 16, WordWrap = true, Margin = new Thickness(6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel = new DockPanel(); panel.Children.Add(editor);
            window = new Window { Title = "PGMS isolated UIA fixture", Width = 520, Height = 300, Left = 20, Top = 20, Content = panel, ShowInTaskbar = false };
        }
        AutomationProperties.SetName(editor, "SQL editor M986"); AutomationProperties.SetAutomationId(editor, "FixtureEditor");
        AutomationProperties.SetName(editor.TextArea, "SQL text area M986"); AutomationProperties.SetAutomationId(editor.TextArea, "FixtureTextArea");
        var close = new Button { Content = "Close isolated fixture", Height = 28 };
        AutomationProperties.SetAutomationId(close, "FixtureClose");
        var focusOther = new Button { Content = "Focus other fixture control", Height = 28 };
        AutomationProperties.SetAutomationId(focusOther, "FixtureFocusOther");
        focusOther.Click += (_, _) => { focusOther.Focus(); Keyboard.Focus(focusOther); };
        DockPanel.SetDock(close, Dock.Bottom); DockPanel.SetDock(focusOther, Dock.Bottom); panel.Children.Insert(0, close); panel.Children.Insert(1, focusOther);
        Phase("fixture-controls-created");
        if (shell) AddTabLifecycleControls((MainWindow)window, panel, root);
        var focusJournal = new List<object>(); var focusWatch = Stopwatch.StartNew();
        void RecordFocus(string control) => focusJournal.Add(new { control, milliseconds = focusWatch.ElapsedMilliseconds, stack = Environment.StackTrace });
        editor.TextArea.GotKeyboardFocus += (_, _) => RecordFocus("text-area");
        close.GotKeyboardFocus += (_, _) => RecordFocus("close-button");
        focusOther.GotKeyboardFocus += (_, _) => RecordFocus("other-button");
        window.Closed += (_, _) => File.WriteAllText(Path.Combine(root, "focus-journal.json"), JsonSerializer.Serialize(focusJournal));
        close.Click += (_, _) => { if (query is not null) query.IsDirty = false; window.Close(); };
        window.ContentRendered += async (_, _) =>
        {
            Phase("content-rendered");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Verify(args);
            // Establish focus in another fixture control, on this inactive desktop only.
            close.Focus(); Keyboard.Focus(close);
            RecordFocus(close.IsKeyboardFocused ? "initial-close-confirmed" : "initial-close-unconfirmed");
            var dpi = VisualTreeHelper.GetDpi(editor);
            string pending = Path.Combine(root, "fixture.pending");
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, new { processId = Environment.ProcessId, hwnd = new WindowInteropHelper(window).Handle.ToInt64(), station = args[1], desktop = args[2], shell, dpiX = dpi.PixelsPerInchX, dpiY = dpi.PixelsPerInchY });
            File.Move(pending, Path.Combine(root, "fixture.json"));
            Phase("handshake-published");
        };
        window.Loaded += (_, _) => Phase("window-loaded");
        Phase("application-run");
        app.Run(window); return 0;
    }
    internal static int RunClient(string[] args)
    {
        // Enter the MTA before loading UIA and keep subscription/removal on this thread.
        Verify(args);
        return Task.Run(() => ClientMta(args)).GetAwaiter().GetResult();
    }
    private static int ClientMta(string[] args)
    {
        Verify(args);
        string report = Safe(args[3]), root = Path.GetDirectoryName(report)!;
        var results = new List<object>(); var observations = new List<object>(); int failed = 0;
        Process? fixture = null;
        var events = new int[3];
        var areaEvents = new int[3];
        AutomationElement? editor = null, area = null;
        AutomationEventHandler textHandler = (_, _) => Interlocked.Increment(ref events[0]);
        AutomationEventHandler selectionHandler = (_, _) => Interlocked.Increment(ref events[1]);
        AutomationPropertyChangedEventHandler valueHandler = (_, _) => Interlocked.Increment(ref events[2]);
        AutomationEventHandler areaTextHandler = (_, _) => Interlocked.Increment(ref areaEvents[0]);
        AutomationEventHandler areaSelectionHandler = (_, _) => Interlocked.Increment(ref areaEvents[1]);
        AutomationPropertyChangedEventHandler areaValueHandler = (_, _) => Interlocked.Increment(ref areaEvents[2]);
        bool subscribed = false;
        void Check(bool passed, string name) { results.Add(new { name, passed }); if (!passed) failed++; }
        void Wait(Func<bool> condition, string operation, int seconds = 5)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (fixture is not null && fixture.HasExited) throw new IOException($"Fixture exited ({fixture.ExitCode}) during {operation}: " + (File.Exists(Path.Combine(root, "fixture-failure.txt")) ? File.ReadAllText(Path.Combine(root, "fixture-failure.txt")) : "no fixture error report"));
                if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(operation);
                Thread.Sleep(25);
            }
        }
        try
        {
            Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "UIA client runs in MTA without windows");
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PGMS.DesktopTests.exe")) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            string fixtureMode = args[0].Contains("-shell-", StringComparison.Ordinal) ? "--uia-desktop-shell-fixture-child"
                : args[0].StartsWith("--uia-desktop-", StringComparison.Ordinal) ? "--uia-desktop-fixture-child" : "--uia-fixture-child";
            foreach (string argument in new[] { fixtureMode, args[1], args[2], root }) start.ArgumentList.Add(argument);
            fixture = Process.Start(start) ?? throw new IOException("Fixture launch failed.");
            string handshake = Path.Combine(root, "fixture.json");
            Wait(() => File.Exists(handshake) && new FileInfo(handshake).Length > 0, "Fixture handshake", args[0].Contains("-shell-", StringComparison.Ordinal) ? 15 : 5);
            using var identity = JsonDocument.Parse(File.ReadAllText(handshake));
            var data = identity.RootElement;
            if (data.GetProperty("processId").GetInt32() != fixture.Id || data.GetProperty("station").GetString() != args[1] || data.GetProperty("desktop").GetString() != args[2]) throw new IOException("Fixture identity mismatch.");
            var main = AutomationElement.FromHandle(new IntPtr(data.GetProperty("hwnd").GetInt64()));
            Check(main.Current.ProcessId == fixture.Id, "UIA root belongs to the test-owned fixture");
            editor = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "FixtureEditor")) ?? throw new IOException("Editor not found.");
            area = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "FixtureTextArea")) ?? throw new IOException("Text area not found.");
            var text = (TextPattern)editor.GetCurrentPattern(TextPattern.Pattern);
            var value = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
            bool initialEditorFocus = editor.Current.HasKeyboardFocus, initialAreaFocus = area.Current.HasKeyboardFocus;
            void Focus(string stage) => observations.Add(new { stage, editorFocus = editor.Current.HasKeyboardFocus, areaFocus = area.Current.HasKeyboardFocus });
            Focus("initial");
            Check(text.DocumentRange.GetText(-1) == InitialText, "cross-process TextPattern exposes exact synthetic document");
            Check(((TextPattern)area.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1) == InitialText, "text area exposes corrected provider too");
            Check(text.DocumentRange.GetEnclosingElement().Current.ProcessId == fixture.Id, "range enclosing element belongs to fixture");
            var end = text.DocumentRange;
            end.MoveEndpointByRange(TextPatternRangeEndpoint.Start, end, TextPatternRangeEndpoint.End);
            Check(end.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 1) == 0, "cross-process endpoint makes no false progress at end");
            var unchanged = end.Clone();
            Check(end.Move(TextUnit.Document, 0) == 0 && end.Compare(unchanged), "cross-process count zero preserves range");
            var visible = text.GetVisibleRanges();
            Check(visible.Length > 0, "private desktop provides visible text geometry");
            var alpha = text.DocumentRange.FindText("alpha", false, false)!;
            alpha.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -4);
            var rectangles = alpha.GetBoundingRectangles();
            Check(rectangles.Length > 0 && rectangles[0].Width > 0 && rectangles[0].Height > 0, "character has nonempty screen rectangle");
            if (rectangles.Length > 0)
            {
                var rect = rectangles[0];
                var fromPoint = text.RangeFromPoint(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
                fromPoint.ExpandToEnclosingUnit(TextUnit.Character);
                var leadingPoint = text.RangeFromPoint(new Point(rect.X + rect.Width / 4, rect.Y + rect.Height / 2));
                leadingPoint.ExpandToEnclosingUnit(TextUnit.Character);
                observations.Add(new { stage = "character-point", rect.X, rect.Y, rect.Width, rect.Height, centerText = fromPoint.GetText(-1), leadingQuarterText = leadingPoint.GetText(-1) });
                Check(leadingPoint.GetText(-1) == "a", "unambiguous leading-glyph screen point maps back to the character");
            }
            Check(editor.Current.HasKeyboardFocus == initialEditorFocus && area.Current.HasKeyboardFocus == initialAreaFocus, "read and geometry queries preserve focus");
            Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, editor, TreeScope.Element, textHandler);
            Automation.AddAutomationEventHandler(TextPattern.TextSelectionChangedEvent, editor, TreeScope.Element, selectionHandler);
            Automation.AddAutomationPropertyChangedEventHandler(editor, TreeScope.Element, valueHandler, ValuePattern.ValueProperty);
            Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, area, TreeScope.Element, areaTextHandler);
            Automation.AddAutomationEventHandler(TextPattern.TextSelectionChangedEvent, area, TreeScope.Element, areaSelectionHandler);
            Automation.AddAutomationPropertyChangedEventHandler(area, TreeScope.Element, areaValueHandler, ValuePattern.ValueProperty);
            subscribed = true;
            value.SetValue("changed alpha beta");
            Focus("after-value");
            Wait(() => Volatile.Read(ref events[0]) > 0 && Volatile.Read(ref events[2]) > 0 && Volatile.Read(ref areaEvents[0]) > 0 && Volatile.Read(ref areaEvents[2]) > 0, "TextChanged and Value event delivery from both peers");
            Check(text.DocumentRange.GetText(-1) == "changed alpha beta", "Value edit updates TextPattern across processes");
            Check(events[0] > 0 && events[2] > 0, "external client receives text and Value events");
            Check(areaEvents[0] > 0 && areaEvents[2] > 0, "focused text-area peer also delivers text and Value events");
            // UIA core may explicitly request SetFocus for ValuePattern.SetValue.
            // Test range selection independently, starting in another fixture control.
            var focusOther = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "FixtureFocusOther"));
            ((InvokePattern)focusOther.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Wait(() => focusOther.Current.HasKeyboardFocus, "Focus sentinel control");
            bool beforeSelectionEditorFocus = editor.Current.HasKeyboardFocus, beforeSelectionAreaFocus = area.Current.HasKeyboardFocus;
            Focus("before-selection");
            var beta = text.DocumentRange.FindText("beta", false, false)!;
            int previousSelectionEvents = Volatile.Read(ref events[1]), previousAreaSelectionEvents = Volatile.Read(ref areaEvents[1]);
            beta.Select();
            Focus("after-selection");
            Wait(() => Volatile.Read(ref events[1]) > previousSelectionEvents && Volatile.Read(ref areaEvents[1]) > previousAreaSelectionEvents, "New selection event delivery from both peers");
            Check(text.GetSelection().Single().GetText(-1) == "beta", "external selection roundtrip and event");
            Check(editor.Current.HasKeyboardFocus == beforeSelectionEditorFocus && area.Current.HasKeyboardFocus == beforeSelectionAreaFocus, "range selection does not steal focus from another control");
            string longText = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"row{i:D2} " + new string('W', 80))) + "\nEND_MARKER";
            value.SetValue(longText);
            Focus("after-long-value");
            bool beforeScrollEditorFocus = editor.Current.HasKeyboardFocus, beforeScrollAreaFocus = area.Current.HasKeyboardFocus;
            var marker = text.DocumentRange.FindText("END_MARKER", false, false)!;
            marker.ScrollIntoView(false);
            Focus("after-scroll-request");
            Wait(() => text.GetVisibleRanges().Any(r => r.GetText(-1).Contains("END_MARKER", StringComparison.Ordinal)), "ScrollIntoView layout");
            Check(marker.GetBoundingRectangles().Length > 0, "scroll reveals final marker rectangle");
            Check(text.GetVisibleRanges().Sum(r => r.GetText(-1).Length) < longText.Length, "visible ranges exclude offscreen wrapped text");
            Focus("after-layout");
            Check(editor.Current.HasKeyboardFocus == beforeScrollEditorFocus && area.Current.HasKeyboardFocus == beforeScrollAreaFocus, "scroll preserves focus state");
            ((InvokePattern)focusOther.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Wait(() => focusOther.Current.HasKeyboardFocus, "Focus sentinel before explicit focus request");
            editor.SetFocus();
            Wait(() => area.Current.HasKeyboardFocus, "Explicit UIA focus forwarding");
            Check(editor.Current.HasKeyboardFocus && area.Current.HasKeyboardFocus, "explicit editor focus succeeds and both peers report text-area focus");
            if (data.GetProperty("shell").GetBoolean())
            {
                CheckTabLifecycle(main, Check);
                int beforeText = Volatile.Read(ref events[0]), beforeAreaText = Volatile.Read(ref areaEvents[0]);
                value.SetValue("SELECT 'original survives tab lifecycle';");
                Wait(() => Volatile.Read(ref events[0]) > beforeText && Volatile.Read(ref areaEvents[0]) > beforeAreaText, "Original peer events after tab lifecycle");
                Check(text.DocumentRange.GetText(-1) == "SELECT 'original survives tab lifecycle';", "original editor and both subscriptions survive three tab lifecycles");
            }
            var close = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "FixtureClose"));
            ((InvokePattern)close.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Check(fixture.WaitForExit(5000), "fixture closes without a stranded process");
        }
        catch (Exception ex) { Check(false, "Failure: " + ex); }
        finally
        {
            if (subscribed && editor is not null)
            {
                try
                {
                    Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, editor, textHandler);
                    Automation.RemoveAutomationEventHandler(TextPattern.TextSelectionChangedEvent, editor, selectionHandler);
                    Automation.RemoveAutomationPropertyChangedEventHandler(editor, valueHandler);
                    if (area is not null)
                    {
                        Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, area, areaTextHandler);
                        Automation.RemoveAutomationEventHandler(TextPattern.TextSelectionChangedEvent, area, areaSelectionHandler);
                        Automation.RemoveAutomationPropertyChangedEventHandler(area, areaValueHandler);
                    }
                }
                catch (Exception ex) { Check(false, "Remove UIA subscriptions: " + ex); }
            }
            if (fixture is not null) { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(5000); } fixture.Dispose(); }
            using var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, new { total = results.Count, failed, results, observations, station = args[1], desktop = args[2], clientProcessId = Environment.ProcessId,
                textEvents = events[0], selectionEvents = events[1], valueEvents = events[2], areaTextEvents = areaEvents[0], areaSelectionEvents = areaEvents[1], areaValueEvents = areaEvents[2], limitation = "Identified inactive test desktop; station may be shared, clipboard is never accessed. No input injection, Narrator or database. Not physical keyboard/DPI certification or reproduction of the desktop-wide keyboard failure." }, new JsonSerializerOptions { WriteIndented = true });
        }
        return failed == 0 ? 0 : 1;
    }

    private static void AddTabLifecycleControls(MainWindow main, DockPanel panel, string root)
    {
        QueryView? current = null;
        int generation = 0;
        var open = new Button { Content = "Create synthetic tab", Height = 28 };
        var close = new Button { Content = "Close synthetic tab", Height = 28 };
        AutomationProperties.SetAutomationId(open, "FixtureTabOpen");
        AutomationProperties.SetAutomationId(close, "FixtureTabClose");
        foreach (var button in new[] { open, close })
        {
            DockPanel.SetDock(button, Dock.Bottom); panel.Children.Insert(0, button);
        }
        open.Click += (_, _) =>
        {
            if (current is not null) throw new InvalidOperationException("Previous synthetic tab still open.");
            string path = Path.Combine(root, $"tab-{++generation}.sql");
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write))) writer.Write($"SELECT 'tab {generation}';");
            current = main.OpenQueryFile(path);
            var editor = (SqlTextEditor)current.FindName("Editor");
            AutomationProperties.SetAutomationId(editor, $"LifecycleEditor{generation}");
            AutomationProperties.SetAutomationId(editor.TextArea, $"LifecycleArea{generation}");
        };
        close.Click += async (_, _) =>
        {
            var target = current ?? throw new InvalidOperationException("No synthetic tab to close.");
            // Only this owned synthetic document: modified-document confirmation is a separate gate.
            target.IsDirty = false;
            if (!await main.CloseQueryAsync(target)) throw new InvalidOperationException("Synthetic tab close refused.");
            current = null;
            AutomationProperties.SetName(close, $"Closed synthetic tab {generation}");
        };
    }

    private static void CheckTabLifecycle(AutomationElement main, Action<bool, string> check)
    {
        AutomationElement Find(string id) => main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        void Wait(Func<bool> condition, string operation)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(operation);
                Thread.Sleep(25);
            }
        }
        int structures = 0;
        StructureChangedEventHandler structure = (_, _) => Interlocked.Increment(ref structures);
        Automation.AddStructureChangedEventHandler(main, TreeScope.Subtree, structure);
        try
        {
            for (int generation = 1; generation <= 3; generation++)
            {
                ((InvokePattern)Find("FixtureTabOpen").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                AutomationElement? editor = null, area = null;
                Wait(() => (editor = Find($"LifecycleEditor{generation}")) is not null && (area = Find($"LifecycleArea{generation}")) is not null, "New tab peers");
                var text = (TextPattern)editor!.GetCurrentPattern(TextPattern.Pattern);
                var value = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
                check(text.DocumentRange.GetText(-1) == $"SELECT 'tab {generation}';", $"tab {generation}: new provider exposes its own document");
                int editorEvents = 0, areaEvents = 0;
                AutomationEventHandler changed = (_, _) => Interlocked.Increment(ref editorEvents);
                AutomationEventHandler areaChanged = (_, _) => Interlocked.Increment(ref areaEvents);
                Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, editor, TreeScope.Element, changed);
                try
                {
                    Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, area!, TreeScope.Element, areaChanged);
                    try
                    {
                        string replacement = $"SELECT 'updated tab {generation}';";
                        value.SetValue(replacement);
                        Wait(() => Volatile.Read(ref editorEvents) > 0 && Volatile.Read(ref areaEvents) > 0, "New tab event delivery");
                        check(text.DocumentRange.GetText(-1) == replacement, $"tab {generation}: both peers deliver events and updated text");
                        ((InvokePattern)Find("FixtureTabClose").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        Wait(() => Find("FixtureTabClose").Current.Name == $"Closed synthetic tab {generation}", "Tracked production close completion");
                        check(Find($"LifecycleEditor{generation}") is null && Find($"LifecycleArea{generation}") is null, $"tab {generation}: closed peers leave live UIA tree with listeners attached");
                    }
                    finally { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, area!, areaChanged); }
                }
                finally { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, editor, changed); }
                check(main.Current.ProcessId > 0, $"tab {generation}: root responds after closed-peer listener removal");
            }
            Wait(() => Volatile.Read(ref structures) > 0, "Shell structure event delivery");
            check(true, "shell subtree delivers structure changes while tabs are created and closed");
        }
        finally { Automation.RemoveStructureChangedEventHandler(main, structure); }
    }
}
