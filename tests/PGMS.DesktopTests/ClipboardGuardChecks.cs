internal static class ClipboardGuardChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var identity in new[]
        {
            ("WinSta0", "PGMS_" + Guid.NewGuid().ToString("N")),
            ("winsta0", "PGMS_" + Guid.NewGuid().ToString("N")),
            ("PGMS_" + Guid.NewGuid().ToString("N"), "Default"),
            ("PGMS_" + Guid.NewGuid().ToString("N"), "PGMS_" + Guid.NewGuid().ToString("N")),
            ("", "")
        })
        {
            bool refused = false;
            try { ClipboardIsolation.Verify(identity.Item1, identity.Item2); }
            catch (InvalidOperationException) { refused = true; }
            check(refused, "Clipboard isolation refuses unverified station/desktop: " + identity);
        }
    }

    public static int RunStandalone()
    {
        int count = 0;
        try
        {
            Run((ok, name) => { if (!ok) throw new Exception(name); Console.WriteLine("PASS: " + name); count++; });
            Console.WriteLine($"{count} clipboard isolation guard checks passed (no clipboard access).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
