using System.IO;
using System.Windows;

internal static class ClipboardProcessChecks
{
    public static int Run()
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "clipboard-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            using var isolation = new ClipboardIsolation();
            string report = Path.Combine(root, "clipboard.txt");
            isolation.Run("--clipboard-child", report);
            Console.Write(File.ReadAllText(report));
            Console.WriteLine("Clipboard evidence: " + root);
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("Invalid clipboard child arguments."); return 1; }
        try
        {
            // This must precede Application, OLE, Clipboard and any result command.
            ClipboardIsolation.Verify(args[1], args[2]);
            RunClipboardProbe();
            File.WriteAllText(args[3], "PASS: Private station and desktop verified before WPF clipboard access.\nPASS: WPF Unicode clipboard write/read matched.\n2 isolated clipboard checks passed.\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(args[3], ex.ToString()); return 1; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunClipboardProbe()
    {
        const string text = "PGMS isolated clipboard á 😀\t\"quoted\"\r\n";
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
        if (Clipboard.GetText(TextDataFormat.UnicodeText) != text) throw new Exception("Unicode clipboard roundtrip mismatch.");
    }
}
