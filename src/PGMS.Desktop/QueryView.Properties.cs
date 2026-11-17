using System.Globalization;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>The last execution of a query tab, for the Properties window.</summary>
internal sealed record QueryExecutionSummary(DateTime StartedUtc, TimeSpan Elapsed, long? Rows, string Outcome)
{
    public DateTime FinishedUtc => StartedUtc + Elapsed;
}

public partial class QueryView
{
    /// <summary>Last execution in this tab's session (null before the first one and after Change Connection).</summary>
    internal QueryExecutionSummary? LastExecution { get; private set; }
    internal QueryConnectionDetails? ConnectionDetails => session?.Details;
    /// <summary>Raised after an execution is noted, which happens after StateChanged, so the Properties window can read it.</summary>
    internal event EventHandler? ExecutionNoted;
    /// <summary>SQLSTATE and message of the error of the last execution, for View ▸ Error List (PGMS-079); null when it did not fail.</summary>
    internal (string Code, string Message)? LastError { get; private set; }
    /// <summary>Tests note a failed execution without a server.</summary>
    internal void NoteExecutionForTests(Exception? error) => NoteExecution(DateTime.UtcNow, TimeSpan.FromMilliseconds(5), error is null ? 1 : null, error, false);

    private void NoteExecution(DateTime startedUtc, TimeSpan elapsed, long? rows, Exception? error, bool canceled)
    {
        LastExecution = new(startedUtc, elapsed, rows, canceled ? "Canceled" : error is null ? "Succeeded" : "Failed");
        LastError = error is null || canceled ? null : error is Npgsql.PostgresException pg ? (pg.SqlState, pg.MessageText) : ("", error.Message);
        UpdateConnectionBar();
        ExecutionNoted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// "server.database (login (pid))", the connection text SSMS 22.11 puts in the tab tooltip and the window title (PGMS-051); null when not connected.
    /// </summary>
    internal string? ConnectionCaption => IsConnected && ConnectionDetails is { } d ? $"{d.ServerName}.{Database} ({d.UserName} ({d.BackendPid.ToString(CultureInfo.InvariantCulture)}))" : null;

    /// <summary>Right side of the yellow bar, as in SSMS 22.11: server | login (pid) | database | elapsed | rows. Hidden while not connected.</summary>
    internal void UpdateConnectionBar()
    {
        if (!IsConnected || ConnectionDetails is not { } d) { ConnectionSegments.Visibility = System.Windows.Visibility.Collapsed; return; }
        SegmentServer.Text = d.ServerName;
        SegmentLogin.Text = $"{d.UserName} ({d.BackendPid.ToString(CultureInfo.InvariantCulture)})";
        SegmentDatabase.Text = Database;
        SegmentElapsed.Text = (LastExecution?.Elapsed ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        long rows = LastExecution?.Rows ?? 0;
        SegmentRows.Text = rows == 1 ? "1 row" : $"{rows.ToString("N0", CultureInfo.CurrentCulture)} rows";
        ConnectionSegments.Visibility = System.Windows.Visibility.Visible;
    }

    /// <summary>Connection state as the Properties window names it: Executing, Open or Closed.</summary>
    internal string ConnectionStateText => executingQuery ? "Executing" : IsConnected ? "Open" : "Closed";

    /// <summary>
    /// Rows of View ▸ Properties Window for this tab, in the categories SSMS 22.11 shows for a query window (Aggregate Status, Connection, Connection Details).
    /// Values are read live, so RefreshValues updates them. SQL Server-only rows (TDS protocol version, Session Tracing ID) are left out; PostgreSQL adds
    /// Database and SSL mode, and SPID becomes Backend PID.
    /// </summary>
    internal IReadOnlyList<DesignerProperty> PropertyItems()
    {
        const string aggregate = "Aggregate Status", connection = "Connection", details = "Connection Details";
        string Server() => ConnectionDetails?.ServerName ?? "";
        string Time(Func<QueryExecutionSummary, DateTime> pick) => LastExecution is { } last ? pick(last).ToLocalTime().ToString("G", CultureInfo.CurrentCulture) : "";
        string Elapsed() => LastExecution is { } last ? last.Elapsed.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) : "";
        string Rows() => LastExecution?.Rows is { } rows ? rows.ToString("N0", CultureInfo.CurrentCulture) : "0";
        return
        [
            new(aggregate, "Elapsed time", "The time the last execution took.", Elapsed),
            new(aggregate, "Finish time", "When the last execution ended.", () => Time(l => l.FinishedUtc)),
            new(aggregate, "Name", "The name of the connection.", Server),
            new(aggregate, "Rows returned", "Rows the last execution returned.", Rows),
            new(aggregate, "Start time", "When the last execution started.", () => Time(l => l.StartedUtc)),
            new(aggregate, "State", "Open, Executing or Closed.", () => ConnectionStateText),
            new(connection, "Connection name", "The name of the connection.", Server),
            new(details, "Connection elapsed time", "The time the last execution on this connection took.", Elapsed),
            new(details, "Connection finish time", "When the last execution on this connection ended.", () => Time(l => l.FinishedUtc)),
            new(details, "Connection rows returned", "Rows the last execution on this connection returned.", Rows),
            new(details, "Connection start time", "When the last execution on this connection started.", () => Time(l => l.StartedUtc)),
            new(details, "Connection state", "Open, Executing or Closed.", () => ConnectionStateText),
            new(details, "Database", "The database this query tab is connected to.", () => session is null ? "" : Database),
            new(details, "Display name", "The server name shown for this connection.", Server),
            new(details, "Login name", "The PostgreSQL role this connection logged in as.", () => ConnectionDetails?.UserName ?? ""),
            new(details, "Server name", "Host, and port when it is not 5432.", Server),
            new(details, "Server version", "The PostgreSQL version the server reported.", () => ConnectionDetails?.ServerVersion ?? ""),
            new(details, "Backend PID", "Process ID of the server backend serving this tab (pg_backend_pid), as SPID in SSMS.", () => ConnectionDetails?.BackendPid.ToString(CultureInfo.InvariantCulture) ?? ""),
            new(details, "SSL mode", "The SSL mode requested in the connection settings; the server decides whether the session is encrypted.", () => ConnectionDetails?.SslMode ?? ""),
            new(details, "Connection opened", "When this tab's session opened.", () => ConnectionDetails?.OpenedAt.LocalDateTime.ToString("G", CultureInfo.CurrentCulture) ?? "")
        ];
    }
}
