using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PGMS.Desktop;

// Fits only the attached window; WPF handles DPI rendering. Never consumes native messages.
internal sealed class WindowWorkArea
{
    private readonly Window window;
    private readonly double minimumWidth, minimumHeight;
    private HwndSource? source;
    private DispatcherOperation? pending;
    private bool moving, closed, applying;
    internal static void Attach(Window window) => _ = new WindowWorkArea(window);

    private WindowWorkArea(Window window)
    {
        this.window = window;
        minimumWidth = window.MinWidth; minimumHeight = window.MinHeight;
        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(OnMessage); Queue();
        };
        window.Loaded += (_, _) => Queue();
        window.StateChanged += (_, _) => Queue();
        window.Closed += (_, _) => { closed = true; pending?.Abort(); source?.RemoveHook(OnMessage); source = null; };
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0231) moving = true;
        if (message == 0x0232) { moving = false; Queue(); }
        if (message is 0x0047 or 0x02E0 or 0x007E or 0x001A) Queue();
        return 0;
    }

    private void Queue()
    {
        if (closed || moving || applying || pending?.Status == DispatcherOperationStatus.Pending) return;
        pending = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Fit));
    }

    private void Fit()
    {
        if (closed || moving || source is null || window.WindowState == WindowState.Minimized) return;
        nint handle = source.Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) || !GetWindowRect(handle, out var bounds)) return;
        double scale = GetDpiForWindow(handle) / 96d;
        var work = monitor.Work;
        if (scale <= 0 || work.Right <= work.Left || work.Bottom <= work.Top) return;
        applying = true;
        try
        {
            window.MinWidth = Math.Min(minimumWidth, Math.Max(1, (work.Right - work.Left) / scale - 16));
            window.MinHeight = Math.Min(minimumHeight, Math.Max(1, (work.Bottom - work.Top) / scale - 16));
            // A window snapped to half or a quarter of the screen stays Normal; moving it would undo the snap.
            if (window.WindowState != WindowState.Normal || IsArranged(handle)) return;
            var fit = FitBounds(new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), scale);
            if (Math.Abs(fit.X - bounds.Left) >= 1 || Math.Abs(fit.Y - bounds.Top) >= 1 ||
                Math.Abs(fit.Width - (bounds.Right - bounds.Left)) >= 1 || Math.Abs(fit.Height - (bounds.Bottom - bounds.Top)) >= 1)
                SetWindowPos(handle, 0, (int)fit.X, (int)fit.Y, (int)fit.Width, (int)fit.Height, 0x0014);
        }
        finally { applying = false; }
    }

    internal static Rect FitBounds(Rect bounds, Rect work, double scale)
    {
        double margin = Math.Min(8 * scale, Math.Min(work.Width, work.Height) / 4);
        double width = Math.Min(bounds.Width, work.Width - 2 * margin);
        double height = Math.Min(bounds.Height, work.Height - 2 * margin);
        return new Rect(Math.Clamp(bounds.X, work.Left + margin, work.Right - margin - width),
            Math.Clamp(bounds.Y, work.Top + margin, work.Bottom - margin - height), width, height);
    }

    private static bool IsArranged(nint handle)
    {
        try { return IsWindowArranged(handle); }
        catch (EntryPointNotFoundException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowArranged(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
