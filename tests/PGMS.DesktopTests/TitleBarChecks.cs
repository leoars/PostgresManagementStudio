using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-048 (M1111): the menu in the title band, as in SSMS 22.11, with Search and the caption buttons; Windows keeps dragging, snapping and the system menu.</summary>
internal static class TitleBarChecks
{
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        var errors = new List<string>(); shell.ShowErrorOverride = ex => errors.Add(ex.Message);
        try
        {
            await Idle();
            var chrome = WindowChrome.GetWindowChrome(shell);
            check(chrome is { UseAeroCaptionButtons: false } && chrome.CaptionHeight == MainWindow.TitleBandHeight && shell.WindowStyle == WindowStyle.SingleBorderWindow && shell.ResizeMode == ResizeMode.CanResize,
                "The window draws its own 30 px title band and keeps the Windows frame (resize, snap, system menu)");
            check(shell.MainMenu.Parent == shell.TitleBand && shell.TitleBand.ActualHeight == 30 && WindowChrome.GetIsHitTestVisibleInChrome(shell.MainMenu) && WindowChrome.GetIsHitTestVisibleInChrome(shell.TitleIcon)
                && new[] { shell.MinimizeButton, shell.MaximizeButton, shell.CloseButton }.All(WindowChrome.GetIsHitTestVisibleInChrome), "Icon, menu and caption buttons sit in the band and take the clicks");
            var top = shell.MainMenu.Items.OfType<MenuItem>().Where(i => i.Visibility == System.Windows.Visibility.Visible).ToList(); // Update available (M1118) is hidden until a newer release exists
            check(top[^1] == shell.FeatureSearchMenu && top[^2] == shell.HelpMenu && !shell.FeatureSearchMenu.HasItems, "Search is the last item of the band, after Help, as in SSMS 22.11");
            check(!shell.KeyboardCommands().Any(c => c.Name.StartsWith("Search", StringComparison.Ordinal)), "Search itself is not a command of Environment ▸ Keyboard");

            // Hit test: the maximize button answers as one (Windows 11 snap layouts), the empty band as caption, the menu as client.
            nint hwnd = new WindowInteropHelper(shell).Handle;
            nint Hit(FrameworkElement element, double x, double y) { var p = element.PointToScreen(new Point(x, y)); return SendMessage(hwnd, 0x0084, 0, (nint)(((int)p.Y << 16) | ((int)p.X & 0xFFFF))); }
            check(Hit(shell.MaximizeButton, 22, 15) == MainWindow.HtMaxButton, "The maximize button answers the hit test as a maximize button");
            var empty = shell.TitleBand.ColumnDefinitions[2];
            double emptyX = shell.TitleIcon.ActualWidth + 14 + shell.MainMenu.ActualWidth + empty.ActualWidth / 2;
            check(empty.ActualWidth > 50 && Hit(shell.TitleBand, emptyX, 15) == 2, "The empty part of the band is caption: dragging and double click are Windows'");
            check(Hit(shell.MainMenu, 10, shell.MainMenu.ActualHeight / 2) == 1 && Hit(shell.CloseButton, 22, 15) == 1, "Menu items and the other buttons stay in the client area");

            // Maximize, Restore, Minimize through the buttons.
            check(new ButtonAutomationPeer(shell.MaximizeButton).GetName() == "Maximize" && (string)shell.MaximizeButton.ToolTip == "Maximize", "Normal window: the button says Maximize");
            Invoke(shell.MaximizeButton); await Idle(); await Idle();
            check(shell.WindowState == WindowState.Maximized && new ButtonAutomationPeer(shell.MaximizeButton).GetName() == "Restore", "Maximize maximizes, and the button becomes Restore");
            var work = WorkArea(hwnd);
            var topLeft = shell.ShellRoot.PointToScreen(new Point(0, 0)); var bottomRight = shell.ShellRoot.PointToScreen(new Point(shell.ShellRoot.ActualWidth, shell.ShellRoot.ActualHeight));
            check(topLeft.X >= work.Left - 1 && topLeft.Y >= work.Top - 1 && bottomRight.X <= work.Right + 1 && bottomRight.Y <= work.Bottom + 1 && bottomRight.Y >= work.Bottom - 2,
                $"Maximized, the content fills the work area without being cut or covering the taskbar ({topLeft} – {bottomRight} in {work.Left},{work.Top},{work.Right},{work.Bottom})");
            check(chrome!.CaptionHeight == MainWindow.TitleBandHeight + shell.ShellRoot.Margin.Top, "Maximized, the caption still covers the whole visible band");
            Invoke(shell.MaximizeButton); await Idle();
            check(shell.WindowState == WindowState.Normal && shell.ShellRoot.Margin == new Thickness(0) && new ButtonAutomationPeer(shell.MaximizeButton).GetName() == "Maximize", "Restore gives the window back, with no margin");
            Invoke(shell.MinimizeButton); await Idle();
            check(shell.WindowState == WindowState.Minimized, "Minimize minimizes");
            shell.WindowState = WindowState.Normal; await Idle();

            // Full Screen (M1092) has no title bar: no caption buttons, the band does not drag.
            shell.SetFullScreen(true); await Idle();
            check(shell.CaptionButtons.Visibility == Visibility.Collapsed && chrome.CaptionHeight == 0 && shell.MainMenu.IsVisible && Hit(shell.TitleBand, emptyX, 15) != 2, "Full Screen keeps the menu, without caption buttons or dragging");
            var full = MonitorArea(hwnd); var fullTopLeft = shell.ShellRoot.PointToScreen(new Point(0, 0));
            check(Math.Abs(fullTopLeft.X - full.Left) <= 1 && Math.Abs(fullTopLeft.Y - full.Top) <= 1, "Full Screen content starts at the corner of the monitor");
            shell.SetFullScreen(false); await Idle();
            check(shell.CaptionButtons.Visibility == Visibility.Visible && chrome.CaptionHeight == MainWindow.TitleBandHeight && shell.WindowState == WindowState.Normal, "Leaving Full Screen brings the caption buttons back");

            // Themes (M1108): the band is the window background and the glyphs take the window text color.
            Theme.Apply(ThemeChoice.Dark); await Idle();
            check(ThemeChecks.ColorOf(shell.MinimizeButton.Foreground) == Theme.Dark["Pgms.PrimaryText"].ToString() && ThemeChecks.ColorOf(shell.Background) == Theme.Dark["ShellBrush"].ToString(), "Dark: dark band, light caption glyphs");
            Theme.Apply(ThemeChoice.CoolBreeze); await Idle();
            check(ThemeChecks.ColorOf(shell.Background) == "#FFA8C7D9" && ThemeChecks.ColorOf(shell.MinimizeButton.Foreground) == ThemeChecks.ColorOf(SystemColors.ControlTextBrush), "Cool Breeze: the SSMS blue band with dark glyphs");

            // Search.
            check(FeatureSearch.Text("Display E_stimated Plan") == "Display Estimated Plan" && FeatureSearch.Text("a__b") == "a_b", "Search shows headers without access-key markers");
            var commands = FeatureSearch.Commands(shell.MainMenu);
            var format = FeatureSearch.Find(commands, "format sql");
            // Edit ▸ Paste Special ▸ Format SQL (M1120) matches as well.
            check(format.Count > 0 && format.Take(2).Any(f => f is { Title: "Format SQL", Path: "Edit > Advanced > Format SQL", Shortcut: "Ctrl+K, Ctrl+Q" }),"Searching \"format sql\" finds Edit > Advanced > Format SQL with its shortcut");
            check(FeatureSearch.Find(commands, "  ").Count == 0 && FeatureSearch.Find(commands, "zzqqxx").Count == 0, "Empty or unknown text finds nothing");
            var advanced = FeatureSearch.Find(commands, "advanced");
            check(advanced.Count > 3 && advanced.All(c => c.Path.Contains("Advanced", StringComparison.Ordinal)), "A word of the menu path finds the commands under it");
            check(commands.All(c => c.Item?.Visibility == Visibility.Visible) && commands.Count > 100 && commands.Count <= shell.KeyboardCommands().Count, "Search lists the visible commands of Environment ▸ Keyboard");

            FeatureSearchWindow? window = null;
            shell.FeatureSearchShown = w => window = w;
            shell.ShowFeatureSearch();
            check(window is { Title: "Search", Owner: not null } && window.Results.Items.Count == 0 && window.NoResults.Visibility == Visibility.Collapsed, "Search opens empty, owned by the main window");
            window!.SearchBox.Text = "zzqqxx";
            check(window.Results.Items.Count == 0 && window.NoResults.Visibility == Visibility.Visible, "No results found when nothing matches");
            window.SearchBox.Text = "full screen";
            var row = window.Results.Items.OfType<ListBoxItem>().FirstOrDefault();
            check(window.Results.SelectedIndex == 0 && row is { Tag: FeatureSearchResult { Path: "View > Full Screen" } } && System.Windows.Automation.AutomationProperties.GetName(row) == "Full Screen View > Full Screen Shift+Alt+Enter",
                "The first match is selected and named for screen readers with its path and shortcut");
            check(window.RunSelected(), "Enter runs the selected command");
            await Idle(); await Idle();
            check(shell.IsFullScreen, "The command runs as its menu item would (View ▸ Full Screen)");
            shell.SetFullScreen(false); await Idle();
            window.SearchBox.Text = "zzqqxx";
            check(!window.RunSelected(), "With nothing found, Enter does nothing");
            check(errors.Count == 0, "No errors: " + string.Join(" | ", errors));
        }
        finally
        {
            Theme.Apply(ThemeChoice.CoolBreeze);
            foreach (var q in shell.QueryDocuments.ToArray()) q.IsDirty = false;
            shell.Close(); await shell.CloseCompletion;
        }

        // Close goes through the usual closing (prompts, layout), as Alt+F4 does.
        var other = new MainWindow { ShowInTaskbar = false }; other.Show(); await other.RecoveryCompletion; await Idle();
        bool closed = false; other.Closed += (_, _) => closed = true;
        Invoke(other.CloseButton);
        for (int i = 0; i < 100 && !closed; i++) await Task.Delay(50);
        await other.CloseCompletion;
        check(closed, "Close closes the main window");
    }

    /// <summary>Accessibility slice: the band's buttons have names and Invoke, and the menu stays reachable with Alt.</summary>
    internal static async Task RunAccessibilityAsync(Action<bool, string> check)
    {
        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        try
        {
            await Idle();
            foreach (var (button, name) in new[] { (shell.MinimizeButton, "Minimize"), (shell.MaximizeButton, "Maximize"), (shell.CloseButton, "Close") })
            {
                var peer = new ButtonAutomationPeer(button);
                check(peer.GetName() == name && peer.GetPattern(PatternInterface.Invoke) is not null && !peer.IsOffscreen() && peer.GetAutomationControlType() == AutomationControlType.Button, $"Title band button {name}: named, invokable, on screen");
            }
            var search = new MenuItemAutomationPeer(shell.FeatureSearchMenu);
            check(search.GetName() == "Search" && search.GetPattern(PatternInterface.Invoke) is not null && search.GetAutomationControlType() == AutomationControlType.MenuItem, "Search is a named menu item that can be invoked");
            check(shell.MainMenu.IsMainMenu && shell.MainMenu.IsVisible && new MenuAutomationPeer(shell.MainMenu).GetChildren()?.Count == shell.MainMenu.Items.Count, "The menu in the band is still the main menu (Alt and F10 reach it), with every item exposed");
            check(new ImageAutomationPeer(shell.TitleIcon).GetName() == "System menu" && shell.Title.Length > 0, "The icon is named, and the window keeps its title for the taskbar and screen readers");
        }
        finally { shell.Close(); await shell.CloseCompletion; }
    }

    private readonly record struct Area(double Left, double Top, double Right, double Bottom);
    private static Area WorkArea(nint hwnd) { var m = Info(hwnd); return new(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom); }
    private static Area MonitorArea(nint hwnd) { var m = Info(hwnd); return new(m.Monitor.Left, m.Monitor.Top, m.Monitor.Right, m.Monitor.Bottom); }
    private static MonitorInfo Info(nint hwnd) { var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info); return info; }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
}
