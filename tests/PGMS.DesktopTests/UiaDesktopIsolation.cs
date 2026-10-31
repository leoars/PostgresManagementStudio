using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

// UIA fixtures only. This isolates windows/input, NOT the clipboard or the OS.
// Never switches desktops; refuses to run on the visible input desktop.
internal sealed class UiaDesktopIsolation : IDisposable
{
    private nint desktop;
    internal string StationName { get; }
    internal string DesktopName { get; } = "PGMS_UIA_" + Guid.NewGuid().ToString("N");
    private readonly string inputDesktopBefore;
    internal UiaDesktopIsolation()
    {
        StationName = Name(GetProcessWindowStation());
        inputDesktopBefore = InputDesktopName();
        nint descriptor = 0;
        try
        {
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            Require(ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{sid})", 1, out descriptor, out _), "Private desktop security");
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            desktop = CreateDesktopW(DesktopName, null, 0, 0, 0x10000000, ref security);
            Require(desktop != 0, "Create inactive UIA test desktop");
            Require(Name(desktop) == DesktopName, "New desktop identity");
            Require(InputDesktopName() != DesktopName, "Test desktop must not receive user input");
        }
        catch { Dispose(); throw; }
        finally { if (descriptor != 0) LocalFree(descriptor); }
    }
    internal void Run(string report, string mode = "--uia-desktop-client-child") => ClipboardIsolation.RunOnDesktop(StationName, DesktopName, mode, report, true);
    internal void AssertInputUnchanged()
    {
        if (InputDesktopName() != inputDesktopBefore || inputDesktopBefore == DesktopName) throw new InvalidOperationException("Visible input desktop changed during the isolated test.");
    }
    internal static void Verify(string station, string desktop)
    {
        if (!desktop.StartsWith("PGMS_UIA_", StringComparison.Ordinal) || desktop.Length != 41 ||
            station != Name(GetProcessWindowStation()) || desktop != Name(GetThreadDesktop(GetCurrentThreadId())) || desktop == InputDesktopName())
            throw new InvalidOperationException("Refusing UIA fixture outside its identified inactive desktop.");
    }
    private static string InputDesktopName()
    {
        nint input = OpenInputDesktop(0, false, 1);
        Require(input != 0, "Identify visible desktop without switching");
        try { return Name(input); } finally { CloseDesktop(input); }
    }
    private static string Name(nint handle)
    {
        var name = new StringBuilder(512);
        Require(GetUserObjectInformationW(handle, 2, name, 1024, out _), "Read desktop/station identity"); return name.ToString();
    }
    private static void Require(bool success, string operation) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation); }
    public void Dispose() { if (desktop != 0) { CloseDesktop(desktop); desktop = 0; } }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size; public nint Descriptor; public int Inherit; }
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern nint CreateDesktopW(string name, string? device, nint mode, uint flags, uint access, ref SecurityAttributes security);
    [DllImport("user32.dll", SetLastError=true)] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint handle);
}
