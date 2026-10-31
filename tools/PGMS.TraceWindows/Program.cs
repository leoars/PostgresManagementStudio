using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

if (args.Length != 2) throw new ArgumentException("Input nettrace and new output JSON required.");
string root = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
string Regular(string value)
{
    string path = Path.GetFullPath(value);
    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Workspace artifacts only.");
    for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
        if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Linked paths refused.");
    return path;
}
string input = Regular(args[0]), output = Regular(args[1]);
if (File.Exists(output)) throw new IOException("Existing evidence preserved.");
var events = new List<object>();
using (var source = new EventPipeEventSource(input))
{
    source.Dynamic.All += data =>
    {
        if (data.ProviderName != "PGMS-Test-Load") return;
        if ((int)data.ID == 0) throw new InvalidDataException("Test EventSource reported an error.");
        if ((int)data.ID != 1) return;
        events.Add(new { scenario = (string)data.PayloadValue(0), boundary = (string)data.PayloadValue(1),
            at = data.TimeStampRelativeMSec, processId = data.ProcessID, threadId = data.ThreadID });
    };
    source.Process();
    if (source.EventsLost != 0) throw new InvalidDataException("Trace lost events; incomplete boundaries refused.");
}
if (events.Count == 0) throw new InvalidDataException("No test load boundaries in trace.");
using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
JsonSerializer.Serialize(stream, new { schemaVersion = 1, inputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))),
    events, releaseReady = false, limitation = "EventPipe relative milliseconds; test events only, not compositor or CPU timestamps." }, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"Extracted {events.Count} boundaries.");
