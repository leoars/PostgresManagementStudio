using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Query ▸ Include Live Query Statistics (PGMS-067, M1101). SSMS draws the running plan with per-operator rows; PostgreSQL does not expose
/// such counters while a statement runs, so the tab shows what the server reports about the tab's backend once a second, from a separate
/// connection: state, elapsed time, waits, blocking backends and the pg_stat_progress_* counters of CREATE INDEX, VACUUM, ANALYZE, CLUSTER and COPY.</summary>
public partial class QueryView
{
    private bool includeLiveStatistics;
    private DispatcherTimer? liveTimer;
    private bool liveSampling;
    private DateTime liveStartedUtc;
    internal int LiveSamples { get; private set; }
    /// <summary>Seconds between samples; tests shorten it.</summary>
    internal TimeSpan LiveInterval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IncludeLiveStatistics
    {
        get => includeLiveStatistics;
        set
        {
            includeLiveStatistics = value;
            LiveStatisticsTab.Visibility = value || LiveStatisticsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!value && liveTimer is { } timer) { liveTimer = null; timer.Stop(); LiveStatisticsText.Text += "\n\nLive statistics turned off."; }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StartLiveStatistics()
    {
        if (!includeLiveStatistics || !executingQuery || ConnectionString is not { } connection || ConnectionDetails is not { } details) return;
        StopLiveStatistics();
        var service = new LiveQueryProgressService(connection);
        int pid = details.BackendPid;
        liveStartedUtc = DateTime.UtcNow; LiveSamples = 0;
        LiveStatisticsTab.Visibility = Visibility.Visible;
        LiveStatisticsText.Text = $"Watching backend PID {pid.ToString(CultureInfo.InvariantCulture)}…";
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LiveInterval };
        timer.Tick += async (_, _) =>
        {
            if (liveSampling || !ReferenceEquals(liveTimer, timer)) return;
            liveSampling = true;
            try
            {
                var sample = await service.SampleAsync(pid);
                if (!ReferenceEquals(liveTimer, timer)) return;
                LiveSamples++;
                LiveStatisticsText.Text = $"Backend PID {pid.ToString(CultureInfo.InvariantCulture)}, sample {LiveSamples.ToString(CultureInfo.InvariantCulture)} at {DateTime.Now:HH:mm:ss}\n"
                    + (sample is null ? "The backend is no longer listed in pg_stat_activity." : string.Join("\n", LiveQueryProgressService.Describe(sample)));
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
            {
                if (ReferenceEquals(liveTimer, timer)) LiveStatisticsText.Text = "Live statistics could not be read: " + ex.Message;
            }
            finally { liveSampling = false; }
        };
        liveTimer = timer;
        timer.Start();
    }

    private void StopLiveStatistics()
    {
        if (liveTimer is not { } timer) return;
        liveTimer = null;
        timer.Stop();
        var elapsed = DateTime.UtcNow - liveStartedUtc;
        LiveStatisticsText.Text += (LiveStatisticsText.Text.Length > 0 ? "\n\n" : "") + $"Execution finished after {elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s ({LiveSamples.ToString(CultureInfo.InvariantCulture)} sample(s)).";
    }
}
