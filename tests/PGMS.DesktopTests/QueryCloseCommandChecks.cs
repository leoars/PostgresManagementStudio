using System.IO;
using System.Data;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using PGMS.Desktop;

internal static class QueryCloseCommandChecks
{
    internal static int RunIsolated(string report)
    {
        report = Path.GetFullPath(report);
        if (!report.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(report))
            throw new IOException("New workspace evidence file required.");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using var isolation = new UiaDesktopIsolation();
        isolation.Run(report, "--query-close-child");
        isolation.AssertInputUnchanged();
        Console.WriteLine(File.ReadAllText(report));
        return 0;
    }

    internal static int RunChild(string[] args)
    {
        UiaDesktopIsolation.Verify(args[1], args[2]);
        int count = 0, exit = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PGMS;component/Resources/Shell.xaml") });
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync((ok, name) => { if (!ok) throw new Exception("FAILED: " + name); count++; Console.WriteLine("PASS: " + name); });
                exit = 0;
                File.WriteAllText(args[3], JsonSerializer.Serialize(new { passed = count, failed = 0, inputInjected = false, inactiveDesktop = true, physicalKeyboard = false }));
            }
            catch (Exception ex) { File.WriteAllText(args[3], ex.ToString()); Console.Error.WriteLine(ex); }
            finally { app.Shutdown(exit); }
        };
        app.Run();
        return exit;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "query-close", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", root);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        try
        {
            window.Show(); await window.RecoveryCompletion;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var workArea = SystemParameters.WorkArea;
            var toleranceArea = workArea; toleranceArea.Inflate(1, 1);
            check(window.ActualWidth <= workArea.Width && window.ActualHeight <= workArea.Height,
                "Initial main window fits the real scaled work area");
            check(toleranceArea.Contains(new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)),
                "Initial caption and bottom edge remain inside the real work area");
            await CheckMonitorFitAsync(window, check);
            await CheckSmallResultsAsync(check);
            check(!ApplicationCommands.Close.CanExecute(null, window), "Close is disabled without an active query");
            var shortcut = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.F4 && b.Modifiers == ModifierKeys.Control);
            check(shortcut.Command == ApplicationCommands.Close, "Ctrl+F4 routes to the query Close command");
            string firstPath = Path.Combine(root, "first.sql"), secondPath = Path.Combine(root, "second.sql");
            File.WriteAllText(firstPath, "SELECT 'first 😀';"); File.WriteAllText(secondPath, "SELECT 'second';");
            var first = window.OpenQueryFile(firstPath);
            var second = window.OpenQueryFile(secondPath);
            var manager = (DockingManager)window.FindName("DockManager");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(ApplicationCommands.Close.CanExecute(null, second.Editor.TextArea), "Close routes from the focused editor area");
            second.Editor.AppendText("\n-- unsaved ação");
            string expected = second.Editor.Text;
            await RespondAsync(window, second.Editor.TextArea, 2, check);
            check(manager.Layout.Descendents().OfType<LayoutDocument>().Count() == 2 && second.IsDirty && second.Editor.Text == expected,
                "Native Cancel retains both tabs and exact unsaved Unicode SQL");
            check(File.ReadAllText(secondPath) == "SELECT 'second';" && window.IsVisible, "Cancel leaves disk and the application intact");
            check(second.Editor.IsKeyboardFocusWithin, "Cancel restores keyboard focus to the retained SQL editor");
            await window.SaveQueryFileAsync(second, secondPath, true);
            check(!second.IsDirty && File.ReadAllText(secondPath) == expected, "Save after canceled Close persists the exact retained text");
            second.Editor.AppendText("\n-- keep in editor only");
            await RespondAsync(window, second.Editor.TextArea, 6, check);
            check(File.ReadAllText(secondPath) == second.Editor.Text, "Native Yes saves current SQL before closing the query");
            check(!manager.Layout.Descendents().OfType<LayoutDocument>().Any(d => d.Content == second) && manager.Layout.Descendents().OfType<LayoutDocument>().Any(d => d.Content == first),
                "Close removes only the active query and preserves the background tab");
            window.OpenQueryFile(firstPath);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ApplicationCommands.Close.Execute(null, first.Editor.TextArea);
            await window.TabCloseCompletion;
            check(!manager.Layout.Descendents().OfType<LayoutDocument>().Any() && window.IsVisible, "Closing the last clean query retains the main window");
            check(!ApplicationCommands.Close.CanExecute(null, window), "Close disables again after the last query");
        }
        finally
        {
            var manager = (DockingManager)window.FindName("DockManager");
            foreach (var doc in manager.Layout.Descendents().OfType<LayoutDocument>()) if (doc.Content is QueryView query) query.IsDirty = false;
            window.Close(); await window.CloseCompletion;
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }

    private static async Task CheckSmallResultsAsync(Action<bool, string> check)
    {
        var query = new QueryView { Width = 540, Height = 390 };
        var fixture = new Window { Content = query, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            var data = new DataTable();
            data.Columns.Add("value", typeof(int)); data.Columns.Add("long_text", typeof(string));
            data.Rows.Add(1, new string('x', 300));
            typeof(QueryView).GetMethod("AddResultGrid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(query, [data]);
            var results = (TabControl)query.FindName("ResultSets"); results.SelectedIndex = 0;
            fixture.Show();
            foreach (double height in new[] { 390d, 360d })
            {
                query.Height = height;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); query.UpdateLayout();
                var grid = (DataGrid)((TabItem)results.SelectedItem).Content;
                grid.ScrollIntoView(grid.Items[0]); grid.UpdateLayout();
                var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
                var presenter = FindVisual<ScrollContentPresenter>(grid);
                check(row is not null && presenter is not null && row.ActualHeight >= 18 &&
                    new Rect(0, 0, presenter.ActualWidth, presenter.ActualHeight).Contains(
                        row.TransformToAncestor(presenter).TransformBounds(new Rect(0, 0, Math.Min(row.ActualWidth, presenter.ActualWidth), row.ActualHeight))),
                    $"First real result row remains inside its visible viewport at 540x{height}");
                check(query.Editor.ActualHeight >= 60 && ((TabControl)query.FindName("OutputTabs")).ActualHeight > 0,
                    $"Editor and output tabs remain reachable at 540x{height}");
            }
        }
        finally { fixture.Close(); await query.DisposeAsync(); }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static async Task CheckMonitorFitAsync(Window window, Action<bool, string> check)
    {
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var work = new Rect(-800, -200, 800, 560);
            var fitted = WindowWorkArea.FitBounds(new Rect(-900, -250, 1800, 1125), work, scale);
            check(work.Contains(fitted) && fitted.Width > 0 && fitted.Height > 0,
                $"Small negative-origin work area contains fitted bounds at scale {scale}");
            check(WindowWorkArea.FitBounds(fitted, work, scale) == fitted, $"Fit stabilizes at scale {scale}");
        }
        nint handle = new WindowInteropHelper(window).Handle;
        check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle), -4), "Real test window uses PerMonitorV2");
        var monitors = new List<TestMonitor>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, nint rect, nint data) =>
        {
            var info = new TestMonitor { Size = Marshal.SizeOf<TestMonitor>() };
            if (GetMonitorInfo(monitor, ref info)) monitors.Add(info);
            return true;
        }, 0);
        // Moves only this fixture on the verified inactive desktop, never the user's window.
        foreach (var monitor in monitors.OrderBy(m => m.Work.Right - m.Work.Left))
        {
            var work = monitor.Work;
            SetWindowPos(handle, 0, work.Left + 10, work.Top + 10, 900, 600, 0x0014);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            GetWindowRect(handle, out var bounds);
            check(bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom,
                $"Native window fits actual monitor work area {work.Right - work.Left}x{work.Bottom - work.Top}");
            double scale = GetDpiForWindow(handle) / 96d;
            check(Math.Abs(window.MinWidth - Math.Min(900, (work.Right - work.Left) / scale - 16)) < 1 &&
                Math.Abs(window.MinHeight - Math.Min(600, (work.Bottom - work.Top) / scale - 16)) < 1,
                "Minimum constraints adapt and restore on the destination monitor");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TestRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct TestMonitor { public int Size; public TestRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref TestMonitor info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out TestRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    // Only answers this fixture's owned native confirmation, on its WPF thread.
    private static async Task RespondAsync(MainWindow owner, IInputElement target, int response, Action<bool, string> check)
    {
        nint ownerHandle = new WindowInteropHelper(owner).Handle;
        uint thread = GetCurrentThreadId();
        bool handled = false;
        Exception? failure = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow > deadline) { Console.Error.WriteLine("Owned query confirmation timeout"); Environment.Exit(1); }
            if (handled) return;
            nint found = 0;
            EnumThreadWindows(thread, (handle, _) =>
            {
                var name = new StringBuilder(64); GetClassName(handle, name, name.Capacity);
                if (GetWindow(handle, 4) == ownerHandle && name.ToString() == "#32770") { found = handle; return false; }
                return true;
            }, 0);
            if (found == 0) return;
            handled = true;
            try
            {
                check(GetDlgItem(found, 2) != 0 && GetDlgItem(found, 6) != 0 && GetDlgItem(found, 7) != 0 && !IsWindowEnabled(ownerHandle), "Close opens the owned native Yes/No/Cancel confirmation");
                check(!ApplicationCommands.Close.CanExecute(null, target), "Pending Close disables duplicate command execution");
            }
            catch (Exception ex) { failure = ex; }
            SendMessage(found, 0x0111, failure is null ? response : 2, 0); // WM_COMMAND, never global input.
        };
        timer.Start();
        try { ApplicationCommands.Close.Execute(null, target); await owner.TabCloseCompletion; }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        check(handled, "Editor-routed Close reaches the protected confirmation path");
    }
    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
}
