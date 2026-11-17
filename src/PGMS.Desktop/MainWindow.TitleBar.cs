using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PGMS.Desktop;

/// <summary>
/// The title band of PGMS-048 (M1111): as in SSMS 22.11, the icon, the menu and Search sit in the title band, with Minimize, Maximize/Restore and
/// Close on the right, and no title text (the title still names the window for the taskbar, Alt+Tab and screen readers). WindowChrome keeps the
/// Windows behavior of the rest of the band: dragging, Aero Snap, double click to maximize, right click and Alt+Space for the system menu, and the
/// resize borders. The maximize button answers the hit test as a maximize button, so Windows 11 shows the snap layouts when the pointer rests on it.
/// </summary>
public partial class MainWindow
{
    internal const double TitleBandHeight = 30;
    private const int WmMouseMove = 0x0200, WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2, WmNcLButtonDblClk = 0x00A3;
    private const int WmNcMouseLeave = 0x02A2, WmSize = 0x0005, WmSysCommand = 0x0112, ScKeyMenu = 0xF100;
    internal const int HtMaxButton = 9;
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M0.5,0.5 L9.5,0.5 9.5,9.5 0.5,9.5 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M2.5,2.5 L2.5,0.5 9.5,0.5 9.5,7.5 7.5,7.5 M0.5,2.5 L7.5,2.5 7.5,9.5 0.5,9.5 Z");
    private static readonly Brush MaximizeHot = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), MaximizePressed = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private HwndSource? titleSource;
    private bool maximizePressed, maximizeTracked;

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

    private void ConfigureTitleBar()
    {
        SourceInitialized += (_, _) =>
        {
            titleSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            // Added after the WindowChrome hook, so it is asked first about the maximize button.
            titleSource?.AddHook(TitleBarMessage);
            UpdateTitleBand();
        };
        StateChanged += (_, _) => UpdateTitleBand();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateTitleBand);
        Closed += (_, _) => { titleSource?.RemoveHook(TitleBarMessage); titleSource = null; };
    }

    /// <summary>Caption buttons, their names and the space a maximized window needs; called when the state, the monitor or Full Screen changes.</summary>
    internal void UpdateTitleBand()
    {
        bool maximized = WindowState == WindowState.Maximized;
        string name = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, name);
        MaximizeButton.ToolTip = name;
        MaximizeGlyph.Data = maximized ? RestoreGeometry : MaximizeGeometry;
        CaptionButtons.Visibility = IsFullScreen ? Visibility.Collapsed : Visibility.Visible;
        var overhang = MaximizedOverhang();
        ShellRoot.Margin = overhang;
        // Full Screen has no title bar: the band does not drag the window out of it.
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = IsFullScreen ? 0 : TitleBandHeight + overhang.Top;
        SetMaximizeVisual(null);
    }

    /// <summary>A maximized window reaches past the work area by its resize border; this is that border in device-independent units, so nothing
    /// is cut and the status bar stays above the taskbar. Full Screen measures against the whole monitor.</summary>
    internal Thickness MaximizedOverhang()
    {
        if (WindowState != WindowState.Maximized || titleSource is null) return new Thickness(0);
        nint handle = titleSource.Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) || !GetWindowRect(handle, out var bounds)) return new Thickness(0);
        double scale = GetDpiForWindow(handle) / 96d;
        if (scale <= 0) return new Thickness(0);
        var area = IsFullScreen ? monitor.Monitor : monitor.Work;
        return new Thickness(Math.Max(0, area.Left - bounds.Left) / scale, Math.Max(0, area.Top - bounds.Top) / scale,
            Math.Max(0, bounds.Right - area.Right) / scale, Math.Max(0, bounds.Bottom - area.Bottom) / scale);
    }

    private void MinimizeClicked(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClicked(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseClicked(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    internal void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>A click on the icon opens the system menu where Alt+Space opens it.</summary>
    private void TitleIconClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (titleSource is not null) PostMessage(titleSource.Handle, WmSysCommand, ScKeyMenu, ' ');
    }

    /// <summary>Whether a screen point (device pixels) is on the maximize button.</summary>
    internal bool IsOverMaximizeButton(Point screen)
    {
        if (!MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) is null) return false;
        var point = MaximizeButton.PointFromScreen(screen);
        return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
    }

    /// <summary>null: normal; false: pointer over; true: pressed. The maximize button gets non-client messages, so WPF does not see the pointer on it.</summary>
    private void SetMaximizeVisual(bool? pressed)
    {
        if (pressed is null) MaximizeButton.ClearValue(BackgroundProperty);
        else MaximizeButton.Background = pressed.Value ? MaximizePressed : MaximizeHot;
    }

    private nint TitleBarMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest when !IsFullScreen && IsOverMaximizeButton(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF))):
                handled = true;
                return HtMaxButton;
            case WmNcMouseMove:
                if (wParam == HtMaxButton)
                {
                    SetMaximizeVisual(maximizePressed);
                    if (!maximizeTracked) { var track = new TrackMouse { Size = Marshal.SizeOf<TrackMouse>(), Flags = 0x00000012, Window = hwnd }; maximizeTracked = TrackMouseEvent(ref track); }
                }
                else { maximizePressed = false; SetMaximizeVisual(null); }
                break;
            case WmNcMouseLeave:
                maximizeTracked = false; maximizePressed = false; SetMaximizeVisual(null);
                break;
            case WmMouseMove:
                if (maximizePressed || MaximizeButton.ReadLocalValue(BackgroundProperty) != DependencyProperty.UnsetValue) { maximizePressed = false; SetMaximizeVisual(null); }
                break;
            case WmNcLButtonDown when wParam == HtMaxButton:
                maximizePressed = true; SetMaximizeVisual(true); handled = true;
                break;
            case WmNcLButtonUp when wParam == HtMaxButton:
                if (maximizePressed) { maximizePressed = false; Dispatcher.BeginInvoke(DispatcherPriority.Input, ToggleMaximize); }
                handled = true;
                break;
            case WmNcLButtonDblClk when wParam == HtMaxButton:
                handled = true;
                break;
            case WmSize when WindowState == WindowState.Maximized:
                Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateTitleBand);
                break;
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse { public int Size; public uint Flags; public nint Window; public uint HoverTime; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse track);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}
