using System.Diagnostics;

// Whole-process counters, not per-thread or exact attribution to the UI operation.
internal sealed class LoadIntervalCounters
{
    private readonly TimeSpan cpu = Cpu();
    private readonly long allocations = GC.GetTotalAllocatedBytes(false);
    private readonly int[] collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    internal object Complete() => new
    {
        elapsedMs = elapsed.Elapsed.TotalMilliseconds,
        processCpuMs = (Cpu() - cpu).TotalMilliseconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocations,
        collectionsByGeneration = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
        limitation = "Whole-process deltas with concurrent work; CPU may exceed wall time. Not UI-only or absolute peak."
    };
    private static TimeSpan Cpu() { using var p = Process.GetCurrentProcess(); return p.TotalProcessorTime; }
}
