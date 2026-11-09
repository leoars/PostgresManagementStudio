using System.Globalization;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class QueryView
{
    /// <summary>Query ▸ Query Options of this tab. The time-out lives in CommandTimeoutSeconds, shared with the toolbar box.</summary>
    public QueryOptions QueryOptions { get; private set; } = new();
    internal QueryOptions CurrentQueryOptions => QueryOptions with { ExecutionTimeoutSeconds = CommandTimeoutSeconds };
    /// <summary>Opens the formatted JSON of a result link in a new tab.</summary>
    internal event Action<string>? JsonLinkRequested;

    internal void ApplyQueryOptions(QueryOptions options)
    {
        QueryOptions = options.Normalized();
        CommandTimeoutSeconds = QueryOptions.ExecutionTimeoutSeconds;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenJsonLink(string text) { if (ResultJson.TryFormat(text, out string formatted)) JsonLinkRequested?.Invoke(formatted); }

    // What this session has from Query Options: a new session has the server's values.
    private (QueryIsolationLevel Isolation, int LockTimeout) appliedSessionOptions = (QueryIsolationLevel.ServerDefault, -1);
    // Only BEGIN and START TRANSACTION open a block, so SQL without those words never needs the savepoint probe, which writes an ERROR to the server log.
    private bool mayHaveOpenTransaction;

    private void NoteSubmittedSql(string sql)
    {
        if (sql.Contains("begin", StringComparison.OrdinalIgnoreCase) || sql.Contains("start", StringComparison.OrdinalIgnoreCase)) mayHaveOpenTransaction = true;
    }

    private void ForgetSessionState() { appliedSessionOptions = (QueryIsolationLevel.ServerDefault, -1); mayHaveOpenTransaction = false; }

    /// <summary>Sends a changed isolation level or lock timeout before the query. Inside a transaction block the change waits: a ROLLBACK would undo it and a
    /// failed block refuses it. Returns a line for the output when the change did not happen.</summary>
    private async Task<string?> ApplySessionOptionsAsync(CancellationToken token)
    {
        var wanted = (QueryOptions.Isolation, QueryOptions.LockTimeoutMilliseconds);
        if (wanted == appliedSessionOptions || session is null) return null;
        try
        {
            if (mayHaveOpenTransaction)
            {
                if ((await session.InspectLossRiskAsync(token)).OpenTransaction) return "Query Options: the isolation level and lock timeout will apply after the open transaction ends.";
                mayHaveOpenTransaction = false;
            }
            await session.ApplySessionOptionsAsync(wanted.Isolation, wanted.LockTimeoutMilliseconds, token);
            appliedSessionOptions = wanted;
            return null;
        }
        catch (PostgresException ex) { return "Query Options were not applied: " + ex.MessageText; }
    }

    /// <summary>The open transaction Close has to ask about, or null. A session that broke has nothing left to commit.</summary>
    internal async Task<QuerySessionLossRisk?> FindOpenTransactionAsync()
    {
        if (!mayHaveOpenTransaction || session?.IsConnected != true) return null;
        try
        {
            var risk = await session.InspectLossRiskAsync();
            if (!risk.OpenTransaction) mayHaveOpenTransaction = false;
            return risk.OpenTransaction ? risk : null;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or InvalidOperationException or TimeoutException) { return null; }
    }

    internal async Task CommitOpenTransactionAsync()
    {
        await session!.CommitAsync();
        mayHaveOpenTransaction = false;
    }

    private int commandTimeoutSeconds;
    public int CommandTimeoutSeconds
    {
        get=>commandTimeoutSeconds;
        set
        {
            if(value is <0 or >86400)throw new ArgumentOutOfRangeException(nameof(value));
            if(commandTimeoutSeconds==value)return;
            commandTimeoutSeconds=value;RecoveryRevision++;
            QueryTimeout.Text=value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
