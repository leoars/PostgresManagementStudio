using System.Globalization;
using System.IO;

internal static class ProcessCheckpoint
{
    internal static void SignalReady(string state)
    {
        string temporary = Path.Combine(state, ".ready-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            // Publish only after the writer is closed: readers must never observe a partial/open signal.
            File.Move(temporary, Path.Combine(state, "ready"));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
