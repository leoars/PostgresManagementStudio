using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using PGMS.Desktop;

/// <summary>B4/B5 (M1134): keys sent through SendInput, the system input queue a physical keyboard uses, on the defects the keyboard pilot of
/// the beta found. Needs the window in the foreground, so it is a focused suite of its own (--keyboard-real) and not part of --highlighting.</summary>
internal static class RealKeyboardChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string state = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "keyboard-real", Guid.NewGuid().ToString("N")));
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        var shell = new MainWindow { Width = 1400, Height = 900 };
        try
        {
            shell.Show();
            await shell.RecoveryCompletion;
            await Idle(shell);
            check(Foreground(shell), "The PGMS window comes to the foreground for real keyboard input");
            var manager = (DockingManager)shell.FindName("DockManager");

            await Press(shell, Key.LeftAlt);
            check(Keyboard.FocusedElement is MenuItem { Parent: Menu }, "Alt alone activates the menu bar, as in SSMS");
            await Press(shell, Key.Escape);
            await Press(shell, Key.F10);
            check(Keyboard.FocusedElement is MenuItem { Parent: Menu }, "F10 activates the menu bar");
            await Press(shell, Key.Escape);

            await Press(shell, Key.F8);
            check(Within(shell.FindName("ExplorerTree") as DependencyObject), "F8 puts the keyboard focus in the Object Explorer tree, even before connecting");

            await Press(shell, Key.LeftCtrl, Key.LeftAlt, Key.T);
            var templates = Anchorable(manager, "templates");
            check(Within(templates?.Content as DependencyObject), "Ctrl+Alt+T puts the keyboard focus in Template Explorer");
            check(Size(templates).Width >= 250, $"Template Explorer opens with a usable width ({Size(templates).Width:0} px), not a few pixels");
            check(Keyboard.FocusedElement is TreeViewItem, "Ctrl+Alt+T focuses a Template Explorer node, not the tree without a node");

            await Press(shell, Key.F4);
            var properties = Anchorable(manager, "properties");
            check(Within(properties?.Content as DependencyObject), "F4 puts the keyboard focus in the Properties window");
            check(Size(properties).Width >= 250, $"Properties opens with a usable width ({Size(properties).Width:0} px)");

            await Press(shell, Key.LeftCtrl, Key.LeftAlt, Key.O);
            var output = Anchorable(manager, "output");
            check(Keyboard.FocusedElement is TextBox && Within(output?.Content as DependencyObject), "Ctrl+Alt+O puts the keyboard focus in the Output text");
            check(Size(output).Height >= 150, $"Output opens with a usable height ({Size(output).Height:0} px), not only its tab");

            await Press(shell, Key.LeftCtrl, Key.Oem5); await Press(shell, Key.E);
            var errors = Anchorable(manager, "errorlist");
            check(Keyboard.FocusedElement is DependencyObject f && Within(errors?.Content as DependencyObject) && (f is DataGrid || Ancestor<DataGrid>(f) is not null), "Ctrl+\\, E puts the keyboard focus in the Error List grid");
            check(Size(errors).Height >= 150, $"Error List opens with a usable height ({Size(errors).Height:0} px)");

            // Tab through the whole window: never on the empty auto-hide strips, and out of the toolbar instead of around it.
            var visited = new List<DependencyObject>();
            for (int i = 0; i < 80; i++) { await Press(shell, Key.Tab); if (Keyboard.FocusedElement is DependencyObject d) visited.Add(d); }
            check(!visited.Any(d => d is LayoutAnchorSideControl || Ancestor<LayoutAnchorSideControl>(d) is not null), "Tab never stops on the empty strips of auto-hidden windows (an unnamed list for the Narrator)");
            check(!visited.Any(d => d is MenuItem), "Tab never lands on the menu bar (Alt and F10 open it), as in SSMS");
            check(!visited.Any(d => Ancestor<ToolBar>(d) is not null), "Tab never lands on a toolbar button, as in SSMS (B1 review, M1137)");
            var toolbar = (ToolBar)shell.FindName("StandardToolBar");
            var enabled = Descendants(toolbar).OfType<ButtonBase>().Where(b => b.IsEnabled && b.IsVisible && b.Focusable).ToList();
            enabled[0].Focus();
            var inToolbar = new List<bool>();
            for (int i = 0; i < 3; i++) { await Press(shell, Key.Tab); inToolbar.Add(Keyboard.FocusedElement is DependencyObject d && Ancestor<ToolBar>(d) is not null); }
            check(!inToolbar.Any(b => b), $"Tab from a toolbar button with the focus (after a click) goes on to the windows at once, without visiting the other buttons nor cycling (in a toolbar after each Tab: {string.Join(",", inToolbar.Select(b => b ? 1 : 0))})");

            // Schema Compare from the keyboard: Alt+T opens Tools, Down to the item, Enter.
            await Press(shell, Key.LeftAlt, Key.T);
            for (int i = 0; i < 40 && Keyboard.FocusedElement is not MenuItem { Name: "SchemaCompareMenu" }; i++) await Press(shell, Key.Down);
            await Press(shell, Key.Enter);
            var compare = manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.Content is SchemaCompareView);
            check(compare?.Content is SchemaCompareView && Within((DependencyObject)compare.Content), "Tools ▸ Schema Compare from the keyboard puts the focus in the new document");
            var grid = compare?.Content is SchemaCompareView view ? Descendants(view).OfType<DataGrid>().FirstOrDefault() : null;
            var narrow = grid?.Columns.Where(c => c.ActualWidth < 48).Select(c => $"{c.Header} {c.ActualWidth:0}").ToList() ?? ["no grid"];
            check(narrow.Count == 0, "The Schema Compare columns open wide enough for their headers (" + string.Join(", ", narrow) + ")");
            var stops = new List<DependencyObject>();
            for (int i = 0; i < 12; i++) { await Press(shell, Key.Tab); if (Keyboard.FocusedElement is DependencyObject d) stops.Add(d); }
            check(!stops.OfType<GridSplitter>().Any(), "Tab does not stop on the unnamed splitters of Schema Compare");

            var connect = new ConnectionDialog();
            var remember = (CheckBox)connect.FindName("RememberPassword");
            check(UIElementAutomationPeer.CreatePeerForElement(remember).GetName() == "Save passwords for this Windows user", "The Narrator reads a name for the Connect check box that saves passwords");
            connect.Close();

            // B5: a window taller than the work area (AI Assistant Settings at 175% on a 1080p screen) is fitted into it, for every window.
            WindowWorkArea.AttachToEveryWindow();            // Taller than every monitor together, so the check holds whichever monitor the window lands on.
            double screen = SystemParameters.VirtualScreenHeight, top = SystemParameters.VirtualScreenTop;
            var tall = new Window { Title = "Tall", Width = 400, Height = screen * 1.6, MinHeight = screen * 1.3, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            tall.Show();
            for (int i = 0; i < 20 && tall.ActualHeight > screen; i++) { await Task.Delay(50); await tall.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle); }
            check(tall.ActualHeight < screen && tall.Top >= top - 1 && tall.Top + tall.ActualHeight <= top + screen + 1,
                $"A window taller than the screen is fitted into the work area ({tall.ActualHeight:0} DIP at {tall.Top:0}, minimum {tall.MinHeight:0}; screen {screen:0} DIP)");
            tall.Close();
        }
        finally
        {
            shell.Close();
            Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous);
        }
    }

    private static LayoutAnchorable? Anchorable(DockingManager manager, string id) => manager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id);

    private static Size Size(LayoutAnchorable? anchorable) =>
        anchorable?.Content is FrameworkElement content && content.IsVisible ? new Size(content.ActualWidth, content.ActualHeight) : new Size(0, 0);

    private static bool Within(DependencyObject? root) => root is not null && Keyboard.FocusedElement is DependencyObject focused && (ReferenceEquals(focused, root) || IsDescendant(root, focused));

    private static bool IsDescendant(DependencyObject root, DependencyObject node)
    {
        for (var cur = node; cur is not null; cur = (cur is Visual ? VisualTreeHelper.GetParent(cur) : null) ?? LogicalTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, root)) return true;
        return false;
    }

    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var cur = node is Visual ? VisualTreeHelper.GetParent(node) : null; cur is not null; cur = VisualTreeHelper.GetParent(cur)) if (cur is T t) return t;
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    internal static async Task Idle(Window w) => await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>One chord: the keys go down in order and up in reverse, through SendInput.</summary>
    internal static async Task Press(Window w, params Key[] chord)
    {
        var keys = chord.Select(k => (ushort)KeyInterop.VirtualKeyFromKey(k)).ToArray();
        var inputs = keys.Select(k => Stroke(k, false)).Concat(keys.Reverse().Select(k => Stroke(k, true))).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) throw new InvalidOperationException("SendInput was blocked");
        await Task.Delay(200);
        await Idle(w);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    internal static bool Foreground(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        for (int i = 0; i < 5 && GetForegroundWindow() != hwnd; i++)
        {
            uint other = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
            AttachThreadInput(me, other, true);
            BringWindowToTop(hwnd); SetForegroundWindow(hwnd);
            AttachThreadInput(me, other, false);
            Thread.Sleep(200);
        }
        return GetForegroundWindow() == hwnd;
    }

    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2E];

    private static INPUT Stroke(ushort vk, bool up) => new()
    {
        Type = 1,
        U = new INPUTUNION { Ki = new KEYBDINPUT { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0), Flags = (up ? 2u : 0u) | (Extended.Contains(vk) ? 1u : 0u) } },
    };

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int Dx, Dy; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT Ki; [FieldOffset(0)] public MOUSEINPUT Mi; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION U; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
