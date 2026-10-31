using System.Diagnostics;

namespace PGMS.Desktop;

// Opt-in in-process diagnostics. No text, paths, persistent log, or external telemetry.
internal sealed class UiPhaseScope : IDisposable
{
    private readonly Action<string, double> observer;
    private readonly string phase;
    private readonly long start = Stopwatch.GetTimestamp();
    private UiPhaseScope(Action<string, double> observer, string phase)
    { this.observer = observer; this.phase = phase; }
    public static UiPhaseScope? Start(Action<string, double>? observer, string phase)
        => observer is null ? null : new(observer, phase);
    public void Dispose() => observer(phase, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
}
