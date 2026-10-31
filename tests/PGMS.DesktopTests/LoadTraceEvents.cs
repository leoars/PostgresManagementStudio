using System.Diagnostics.Tracing;

// Test assembly only. Names contain no SQL, paths, credentials or profile data.
[EventSource(Name = "PGMS-Test-Load")]
internal sealed class LoadTraceEvents : EventSource
{
    public static readonly LoadTraceEvents Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(string scenario, string boundary) => WriteEvent(1, scenario, boundary);
}
