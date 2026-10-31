using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceConfirmationChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string name = "pgms_confirm_" + Guid.NewGuid().ToString("N");
        string target = ObjectSql.Relation("public", name);
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE SEQUENCE {target};");
        try
        {
            var dialog = new SequenceDialog(connection, "public", name) { ShowInTaskbar = false };
            Exception? error = null;
            dialog.ContentRendered += async (_, _) =>
            {
                try
                {
                    ((ComboBox)dialog.FindName("Operation")).SelectedItem = SequenceOperation.Drop;
                    await dialog.ApplyAsync();
                    check(!dialog.WasApplied && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("Inspect"), "uninspected Drop is refused before confirmation");
                    await dialog.InspectAsync();
                    uint oid = dialog.Snapshot!.Oid;
                    await RespondAsync(dialog, false, check);
                    check(!dialog.WasApplied && dialog.IsVisible && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("canceled"), "native No cancels deletion and retains dialog");
                    check((await new SequenceService(connection).InspectAsync("public", name)).Oid == oid, "native No preserves the inspected sequence");
                    await session.ExecuteAsync($"DROP SEQUENCE {target}; CREATE SEQUENCE {target};");
                    await RespondAsync(dialog, true, check);
                    check(!dialog.WasApplied && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("identity changed"), "native Yes still refuses replaced sequence");
                    await dialog.InspectAsync();
                    await RespondAsync(dialog, true, check);
                    check(dialog.WasApplied && !dialog.IsVisible, "native Yes applies deletion after reinspection");
                }
                catch (Exception ex) { error = ex; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            };
            dialog.ShowDialog();
            if (error is not null) throw error;
            bool absent = false;
            try { await new SequenceService(connection).InspectAsync("public", name); }
            catch (InvalidOperationException) { absent = true; }
            check(absent, "native confirmed Drop removes sequence from PostgreSQL");
        }
        finally { await session.ExecuteAsync($"DROP SEQUENCE IF EXISTS {target};"); }
    }

    // Drives only a native dialog owned by this test's WPF window on this thread.
    // No global input injection or production confirmation override.
    internal static async Task RespondAsync(SequenceDialog owner, bool yes, Action<bool, string> check)
        => await RespondAsync(owner,owner.ApplyAsync,yes,check);

    internal static async Task RespondAsync(Window owner, Func<Task> apply, bool yes, Action<bool, string> check)
    {
        nint ownerHandle = new WindowInteropHelper(owner).Handle;
        uint thread = GetCurrentThreadId();
        Exception? failure = null;
        bool handled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow > deadline)
            {
                Console.Error.WriteLine("Native sequence confirmation did not complete within 10 seconds.");
                Environment.Exit(1);
            }
            if (handled) return;
            nint found = 0;
            EnumThreadWindows(thread, (handle, _) =>
            {
                var name = new StringBuilder(64); GetClassName(handle, name, name.Capacity);
                if (GetWindow(handle, 4) == ownerHandle && name.ToString() == "#32770") { found = handle; return false; }
                return true;
            }, 0);
            if (found == 0)
            {
                return;
            }
            handled = true;
            try
            {
                long defaultId = SendMessage(found, 0x0400, 0, 0).ToInt64();
                check((defaultId & 0xffff) == 7 && (defaultId >> 16) == 0x534b, "native sequence confirmation defaults to No");
                check(!IsWindowEnabled(ownerHandle), "native sequence confirmation disables its owner");
                check(GetDlgItem(found, 6) != 0 && GetDlgItem(found, 7) != 0, "native sequence confirmation exposes Yes and No");
            }
            catch (Exception ex) { failure = ex; }
            SetActiveWindow(found);
            SendMessage(GetDlgItem(found, failure is null && yes ? 6 : 7), 0x00f5, 0, 0); // BM_CLICK
        };
        timer.Start();
        try { await apply(); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        check(handled, "sequence Apply presents a real native confirmation");
    }

    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint window);
}
