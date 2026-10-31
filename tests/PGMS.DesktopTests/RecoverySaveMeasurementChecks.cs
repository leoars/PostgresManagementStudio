using System.IO;
using System.Text.Json;
using PGMS.Desktop;

internal static class RecoverySaveMeasurementChecks
{
    internal static Task RunAsync(Action<bool,string> check)
    {
        string root=Path.GetFullPath(Path.Combine("artifacts","recovery-save",Guid.NewGuid().ToString("N")));
        var store=new QueryRecoveryStore(Path.Combine(root,"drafts"));
        var samples=new List<object>();
        foreach(char character in new[]{'x','á'})
        {
            var draft=new RecoveryDraft(Guid.NewGuid(),"synthetic.sql",new string(character,QueryRecoveryStore.MaximumCharacters),SqlFileFormat.Utf8);
            store.Save(draft); // Untimed warm-up for serializer, DPAPI and directory creation.
            for(int i=0;i<16;i++)
            {
                var measurement=store.MeasureSave(draft);
                samples.Add(new{scenario=character=='x'?"ascii":"unicode",iteration=i,measurement});
                check(store.Read(draft.Id)==draft,"Measured durable recovery save roundtrips complete synthetic draft");
                check(measurement.ClearBytes>0 && measurement.ProtectionBytes>0 && measurement.EncryptedBytes>measurement.ProtectionBytes && measurement.WriteFlushMilliseconds>=0,"Recovery phase sample contains lengths and elapsed time only");
            }
        }
        string report=Path.Combine(root,"measurement.json");
        File.WriteAllText(report,JsonSerializer.Serialize(new{milestone=BuildInfo.Milestone,samples,limitation="Synthetic sequential warm overwrites, ASCII before Unicode. Timings include OS scheduling, antivirus and storage. Compress includes clearing JSON, Protect includes clearing compressed payload; validate includes path checks. Read verification between samples warms caches. No causal diagnosis, UI latency or production SLA."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Recovery phase report: "+report);
        return Task.CompletedTask;
    }
}
