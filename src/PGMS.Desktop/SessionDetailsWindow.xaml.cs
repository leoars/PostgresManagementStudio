using System.ComponentModel;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
/// <summary>PostgreSQL counterpart of the SSMS Session Details dialog: one backend, identified by PID and backend_start.</summary>
public partial class SessionDetailsWindow : Window
{
    private readonly ActivityService service;
    private bool busy;
    public ActivitySession Session { get; private set; }
    /// <summary>The session ended or was replaced by another backend with the same PID.</summary>
    public bool Ended { get; private set; }
    public bool Terminated { get; private set; }
    /// <summary>Confirmation before terminating; tests replace the message box.</summary>
    internal Func<ActivitySession, bool> ConfirmTermination { get; set; }
    public SessionDetailsWindow(ActivityService service, ActivitySession session)
    {
        InitializeComponent(); WindowWorkArea.Attach(this);
        this.service = service; Session = session; ConfirmTermination = s => ActivityMonitorView.ConfirmTerminate(this, s);
        Title = $"Session Details — {session.User} — {session.Pid}";
        Display(session);
        Status.Text = "Snapshot from the Activity Monitor list. Refresh rereads this session.";
    }
    private void Display(ActivitySession session)
    {
        DatabaseValue.Text = session.Database; ApplicationValue.Text = session.Application; HostValue.Text = session.Host.Length == 0 ? "—" : session.Host;
        StateValue.Text = session.State; WaitValue.Text = session.Wait.Length == 0 ? "—" : session.Wait;
        BackendStartValue.Text = session.BackendStart.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        BlockersValue.Text = session.Blockers.Length == 0 ? "—" : session.Blockers; HeadBlockerValue.Text = session.IsHeadBlocker ? "Yes" : "No";
        QueryText.Text = session.Query;
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Terminate(object sender, RoutedEventArgs e) => await TerminateAsync();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    public async Task RefreshAsync()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            if (await service.ReadSessionAsync(Session) is { } current) { Session = current; Display(current); Status.Text = $"Refreshed locally at {DateTime.Now:HH:mm:ss}."; }
            else { Ended = true; Status.Text = "This session has ended; the details above are the last ones read."; }
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { Status.Text = Describe(ex) + " Previous details retained."; }
        finally { SetBusy(false); }
    }
    public async Task TerminateAsync()
    {
        if (busy || Ended || !ConfirmTermination(Session)) return;
        SetBusy(true);
        try
        {
            // Same PID+backend_start revalidation as the monitor window, so a reused PID is never signaled.
            Terminated = await service.SignalAsync(Session, terminate: true);
            Status.Text = Terminated ? "Session termination confirmed." : "No signal confirmed. The session may have ended or changed; refresh before retrying.";
            if (Terminated) Ended = true;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or TimeoutException) { Status.Text = Describe(ex); }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool value) { busy = value; RefreshButton.IsEnabled = !value && !Ended; TerminateButton.IsEnabled = !value && !Ended; }
    private static string Describe(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; Status.Text = "Wait for the current operation before closing."; } }
}
