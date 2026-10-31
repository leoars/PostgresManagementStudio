using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using ICSharpCode.AvalonEdit;

// Calls the installed editor's provider directly. No Window, Application.Run,
// focus changes, input injection, clipboard, Narrator or database operations.
// Exit 0 means evidence was collected, not that the contracts passed.
internal static class AutomationContractProbe
{
    internal static int Run(string output)
    {
        string root = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        output = Path.GetFullPath(output);
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Workspace artifacts only.");
        for (string? path = Path.GetDirectoryName(output); path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked paths refused.");

        var results = new List<object>();
        var editor = new TextEditor { Text = "SELECT 1" };
        var peer = UIElementAutomationPeer.CreatePeerForElement(editor.TextArea) ?? throw new InvalidOperationException("No editor peer.");
        var provider = peer.GetPattern(PatternInterface.Text) as ITextProvider ?? throw new InvalidOperationException("No text provider.");
        foreach (var item in new[]
        {
            (Name: "character-forward-at-end", AtEnd: true, Unit: TextUnit.Character, Count: 1, Expected: 0),
            (Name: "character-backward-at-start", AtEnd: false, Unit: TextUnit.Character, Count: -1, Expected: 0),
            (Name: "line-forward-at-last-line", AtEnd: true, Unit: TextUnit.Line, Count: 1, Expected: 0),
            (Name: "character-forward-past-end", AtEnd: false, Unit: TextUnit.Character, Count: 100, Expected: editor.Text.Length),
            (Name: "document-zero-count", AtEnd: false, Unit: TextUnit.Document, Count: 0, Expected: 0)
        })
        {
            var range = provider.DocumentRange;
            if (item.AtEnd) range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
            else range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            var before = range.Clone();
            int moved = range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, item.Unit, item.Count);
            bool endpointUnchanged = range.CompareEndpoints(TextPatternRangeEndpoint.Start, before, TextPatternRangeEndpoint.Start) == 0;
            bool passed = moved == item.Expected && (item.Expected != 0 || endpointUnchanged);
            results.Add(new { item.Name, requestedCount = item.Count, expectedMoved = item.Expected, actualMoved = moved, endpointUnchanged, contractPassed = passed });
        }
        string? visibleRangesError = null;
        try { _ = provider.GetVisibleRanges(); }
        catch (Exception ex) { visibleRangesError = ex.GetType().FullName; }
        var assembly = typeof(TextEditor).Assembly;
        string assemblyFile = assembly.Location;
        var report = new
        {
            schemaVersion = 1,
            mode = "Direct in-process UIA provider calls; no shown window or injected input",
            assembly = assembly.GetName().Name,
            version = assembly.GetName().Version?.ToString(),
            informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyFile))),
            syntheticText = editor.Text,
            results,
            visibleRangesError,
            limitation = "Contract observations only. Does not reproduce Narrator or the desktop-wide keyboard failure; not a passed accessibility gate."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
