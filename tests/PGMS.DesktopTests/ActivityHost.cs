using System.Windows;
using PGMS.Desktop;

/// <summary>Test window around the Activity Monitor document, sized like the former monitor window (1100 × 650). Closing follows the tab rules:
/// refused while an operation runs, otherwise the monitor shuts down.</summary>
internal sealed class ActivityHost : Window
{
    public ActivityMonitorView View { get; }
    public ActivityHost(string connection, int maximumRows = 1000, Func<string, string, Task>? openQueryText = null, Func<string, string, Task>? openQueryPlan = null)
    {
        View = new ActivityMonitorView(connection, maximumRows) { OpenQueryText = openQueryText, OpenQueryPlan = openQueryPlan };
        Content = View; Title = View.Title; Width = 1100; Height = 650; MinWidth = 850; MinHeight = 500; Background = View.Background;
        Closing += (_, e) => { if (!View.RequestClose()) e.Cancel = true; else View.Shutdown(); };
    }
    /// <summary>Names live in the view's namescope, not the window's.</summary>
    public new object FindName(string name) => View.FindName(name);
    public Task ReloadAsync() => View.ReloadAsync();
    public Task ApplyFiltersAsync() => View.ApplyFiltersAsync();
    public Task FirstPageAsync() => View.FirstPageAsync();
    public Task PreviousPageAsync() => View.PreviousPageAsync();
    public Task NextPageAsync() => View.NextPageAsync();
    public int SnapshotVersion => View.SnapshotVersion;
    public int MetricsVersion => View.MetricsVersion;
    public bool IsAutoRefreshRunning => View.IsAutoRefreshRunning;
    public TimeSpan AutoRefreshInterval => View.AutoRefreshInterval;
    public SessionDetailsWindow? CreateDetails() => View.CreateDetails();
}
