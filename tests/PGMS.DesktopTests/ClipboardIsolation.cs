using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

// Test infrastructure only. A desktop alone does NOT isolate a clipboard.
internal sealed class ClipboardIsolation : IDisposable
{
    private nint station, desktop;
    public string StationName { get; }
    public string DesktopName { get; } = "PGMS_" + Guid.NewGuid().ToString("N");

    public ClipboardIsolation()
    {
        nint original = GetProcessWindowStation(), descriptor = 0;
        bool initialized = false;
        try
        {
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            Require(ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})", 1, out descriptor, out _), "Private station security");
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // Named stations require administrator membership; never request elevation here.
            // Anonymous CREATE_ONLY is a safe fallback ONLY if the logon station is new.
            station = CreateWindowStationW("PGMS_" + Guid.NewGuid().ToString("N"), 1, 0x10000000, ref security);
            if (station == 0 && Marshal.GetLastWin32Error() == 5)
                station = CreateWindowStationW(null, 1, 0x10000000, ref security);
            Require(station != 0, "Create private station (no existing station allowed)");
            Require(SetProcessWindowStation(station), "Select private station");
            StationName = Name(station);
            if (StationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) || StationName == Name(original))
                throw new InvalidOperationException("Clipboard isolation was not established.");
            desktop = CreateDesktopW(DesktopName, null, 0, 0, 0x10000000, ref security);
            Require(desktop != 0, "Create private desktop");
            initialized = true;
        }
        finally
        {
            bool restored = SetProcessWindowStation(original);
            if (descriptor != 0) LocalFree(descriptor);
            // A station cannot be closed while assigned to this process.
            if (!initialized || !restored) Dispose();
            Require(restored, "Restore original station");
        }
    }

    public void Run(string mode, string reportPath, params string[] arguments)
        => RunCore(mode, reportPath, false, arguments);

    public void RunContained(string mode, string reportPath, params string[] arguments)
        => RunCore(mode, reportPath, true, arguments);

    private void RunCore(string mode, string reportPath, bool containProcessTree, params string[] arguments)
        => RunOnDesktop(StationName, DesktopName, mode, reportPath, containProcessTree, arguments);

    internal static void RunOnDesktop(string stationName, string desktopName, string mode, string reportPath, bool containProcessTree, params string[] arguments)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "PGMS.DesktopTests.exe");
        var command = new StringBuilder(string.Join(" ", new[] { executable, mode, stationName, desktopName, reportPath }.Concat(arguments).Select(Quote)));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = stationName + "\\" + desktopName };
        using var job = containProcessTree ? new TestProcessJob() : null;
        Require(CreateProcessW(executable, command, 0, 0, false, containProcessTree ? 0x08000004u : 0x08000000u, 0, null, ref startup, out var child), "Start isolated test child");
        try
        {
            if (job is not null)
            {
                job.Assign(child.Process);
                Require(ResumeThread(child.Thread) != uint.MaxValue, "Resume contained test child");
            }
            uint wait = WaitForSingleObject(child.Process, 45000);
            if (wait != 0)
            {
                TerminateProcess(child.Process, 124);
                WaitForSingleObject(child.Process, 5000);
                throw new TimeoutException("Isolated clipboard child did not exit within 45 seconds.");
            }
            Require(GetExitCodeProcess(child.Process, out uint code), "Read clipboard child exit code");
            if (code != 0) throw new InvalidOperationException($"Isolated test child failed ({code}): " + (File.Exists(reportPath) ? File.ReadAllText(reportPath) : "no report"));
        }
        catch
        {
            TerminateProcess(child.Process, 125);
            WaitForSingleObject(child.Process, 5000);
            throw;
        }
        finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
    }

    public static void Verify(string expectedStation, string expectedDesktop)
    {
        if (expectedStation.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) ||
            !(expectedStation.StartsWith("Service-", StringComparison.OrdinalIgnoreCase) || expectedStation.StartsWith("PGMS_", StringComparison.Ordinal)) ||
            !expectedDesktop.StartsWith("PGMS_", StringComparison.Ordinal) ||
            Name(GetProcessWindowStation()) != expectedStation || Name(GetThreadDesktop(GetCurrentThreadId())) != expectedDesktop)
            throw new InvalidOperationException("Refusing clipboard access outside the private test station and desktop.");
    }

    private static string Quote(string value)
    {
        if (value.Contains('"') || value.Contains('\0') || value.EndsWith('\\')) throw new ArgumentException("Unsupported child argument.");
        return "\"" + value + "\"";
    }
    private static void Require(bool ok, string operation) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    private static string Name(nint handle)
    {
        var name = new StringBuilder(512);
        Require(GetUserObjectInformationW(handle, 2, name, 1024, out _), "Read station/desktop identity");
        return name.ToString();
    }
    public void Dispose()
    {
        if (desktop != 0) { CloseDesktop(desktop); desktop = 0; }
        if (station != 0) { CloseWindowStation(station); station = 0; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern nint CreateWindowStationW(string? name, uint flags, uint access, ref SecurityAttributes security);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern nint CreateDesktopW(string name, string? device, nint mode, uint flags, uint access, ref SecurityAttributes security);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseWindowStation(nint station);
}
