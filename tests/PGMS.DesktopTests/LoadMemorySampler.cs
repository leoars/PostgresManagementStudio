using System.Diagnostics;

// Test-only sampling: maxima can miss peaks between observations and include the whole process.
internal sealed class LoadMemorySampler : IAsyncDisposable
{
    internal sealed record Reading(long ManagedBytes, long WorkingSetBytes, long PrivateBytes);
    private readonly CancellationTokenSource stop = new();
    private readonly Task sampling;
    public Reading Baseline { get; }
    public Reading Maximum { get; private set; }
    public int Samples { get; private set; }

    public LoadMemorySampler()
    {
        Baseline = Maximum = Read();
        Samples = 1;
        sampling = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(20, stop.Token).ConfigureAwait(false);
                    var current = Read();
                    Maximum = new(Math.Max(Maximum.ManagedBytes, current.ManagedBytes),
                        Math.Max(Maximum.WorkingSetBytes, current.WorkingSetBytes), Math.Max(Maximum.PrivateBytes, current.PrivateBytes));
                    Samples++;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }

    private static Reading Read()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new(GC.GetTotalMemory(false), process.WorkingSet64, process.PrivateMemorySize64);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await sampling.ConfigureAwait(false);
        stop.Dispose();
    }
}
