using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace PGMS.Desktop;

/// <summary>How an execution ended. Stored by number: keep the values.</summary>
public enum QueryHistoryOutcome { Succeeded = 0, Failed = 1, Canceled = 2 }

/// <summary>Who wrote the SQL that ran. Text inserted by a tool keeps that origin while it is executed unchanged; once edited it counts as typed.
/// Stored by number: keep the values.</summary>
public enum QueryHistoryOrigin { Typed = 0, QueryDesigner = 1, Ai = 2, Other = 3 }

/// <summary>A distinct query: same normalized SQL on the same server, port and database (PGMS-043).</summary>
public sealed record QueryHistoryKey(string Server, int Port, string Database, string Hash);

/// <summary>One execution kept in Query History (PGMS-043). Sql may be cut at the size limit: OriginalLength is the length that ran, Hash is of the whole text.
/// RowsReturned counts the rows the tab received (shown, or read and discarded); RowsAffected is what the server reported for INSERT, UPDATE, DELETE and similar.</summary>
public sealed record QueryHistoryEntry(Guid Id, DateTime StartedUtc, long DurationMilliseconds, string Server, int Port, string Database, string User,
    string Sql, int OriginalLength, string Hash, QueryHistoryOrigin Origin, QueryHistoryOutcome Outcome,
    long? RowsReturned = null, long? RowsAffected = null, string? SqlState = null, string? Error = null)
{
    public const int MaximumErrorCharacters = 4096, MaximumNameCharacters = 1024;
    public bool Truncated => Sql.Length < OriginalLength;
    public QueryHistoryKey Key => new(Server, Port, Database, Hash);
    /// <summary>Text to put back in an editor: a cut entry ends with a comment that says so.</summary>
    public string ReopenText => Truncated
        ? Sql + Environment.NewLine + $"-- PGMS Query History: this text was cut at {Sql.Length:N0} of {OriginalLength:N0} characters when it was recorded."
        : Sql;

    internal void Validate()
    {
        static bool Name(string? value, bool allowEmpty = false) => value is not null && value.Length <= MaximumNameCharacters && (allowEmpty || value.Length > 0);
        if (Id == Guid.Empty || StartedUtc.Kind != DateTimeKind.Utc || DurationMilliseconds < 0 || !Name(Server) || Port is < 1 or > 65535 || !Name(Database) || !Name(User, true) ||
            Sql is null || Sql.Length > QueryHistorySettings.MaxSqlKiBLimit * 1024 || OriginalLength < Sql.Length || Hash is not { Length: 64 } || !Hash.All(Uri.IsHexDigit) ||
            !Enum.IsDefined(Origin) || !Enum.IsDefined(Outcome) || RowsReturned < 0 || RowsAffected < 0 || SqlState is { Length: > 5 } || Error is { Length: > MaximumErrorCharacters })
            throw new System.IO.InvalidDataException("Query History holds an invalid entry.");
    }
}

/// <summary>Tools ▸ Options ▸ Query Execution ▸ Query History. Applies to every connection; a saved profile can turn recording off for its server.</summary>
public sealed record QueryHistorySettings(bool Enabled = true, int RetentionDays = QueryHistorySettings.DefaultRetentionDays, int MaxExecutions = QueryHistorySettings.DefaultMaxExecutions, int MaxSqlKiB = QueryHistorySettings.DefaultMaxSqlKiB)
{
    public const int DefaultRetentionDays = 90, MinRetentionDays = 1, MaxRetentionDays = 3650;
    public const int DefaultMaxExecutions = 10000, MinExecutions = 100, MaxExecutionsLimit = 1000000;
    public const int DefaultMaxSqlKiB = 64, MinSqlKiB = 1, MaxSqlKiBLimit = 256;
    /// <summary>Size limit of the SQL kept per execution, in UTF-16 characters (1 KiB = 1024 characters).</summary>
    public int MaxSqlCharacters => MaxSqlKiB * 1024;
    /// <summary>Hand-edited files can hold anything: out-of-range values go back to their defaults.</summary>
    public QueryHistorySettings Normalized() => this with
    {
        RetentionDays = RetentionDays is >= MinRetentionDays and <= MaxRetentionDays ? RetentionDays : DefaultRetentionDays,
        MaxExecutions = MaxExecutions is >= MinExecutions and <= MaxExecutionsLimit ? MaxExecutions : DefaultMaxExecutions,
        MaxSqlKiB = MaxSqlKiB is >= MinSqlKiB and <= MaxSqlKiBLimit ? MaxSqlKiB : DefaultMaxSqlKiB
    };
}

/// <summary>What a query tab reports after an execution, before the history applies its limits.</summary>
public sealed record QueryHistoryCapture(string Sql, string Server, int Port, string Database, string User, DateTime StartedUtc, TimeSpan Elapsed,
    QueryHistoryOrigin Origin, QueryHistoryOutcome Outcome, long? RowsReturned = null, long? RowsAffected = null, string? SqlState = null, string? Error = null)
{
    /// <summary>Server, port and user come from the connection; database is the session's when known. The password is never read.</summary>
    public static QueryHistoryCapture FromConnection(string connectionString, string? database, string sql, DateTime startedUtc, TimeSpan elapsed, QueryHistoryOrigin origin,
        QueryHistoryOutcome outcome, long? rowsReturned, long? rowsAffected, Exception? error)
    {
        var c = new NpgsqlConnectionStringBuilder(connectionString);
        string? state = (error as PostgresException)?.SqlState;
        string? message = error is null ? null : error is PostgresException pg ? pg.MessageText : error is OperationCanceledException ? "Canceled." : error.Message;
        return new(sql, c.Host ?? "", c.Port, string.IsNullOrEmpty(database) ? c.Database ?? "" : database, c.Username ?? "", startedUtc, elapsed, origin, outcome,
            rowsReturned is < 0 ? null : rowsReturned, rowsAffected is < 0 ? null : rowsAffected, state, message);
    }

    internal QueryHistoryEntry ToEntry(QueryHistorySettings settings)
    {
        string full = QueryHistoryText.Sanitize(Sql);
        string kept = QueryHistoryText.Cut(full, settings.Normalized().MaxSqlCharacters);
        string? error = Error is null ? null : QueryHistoryText.Cut(QueryHistoryText.Sanitize(Error), QueryHistoryEntry.MaximumErrorCharacters);
        static string Name(string value) => QueryHistoryText.Cut(QueryHistoryText.Sanitize(value), QueryHistoryEntry.MaximumNameCharacters);
        var entry = new QueryHistoryEntry(Guid.NewGuid(), DateTime.SpecifyKind(StartedUtc.ToUniversalTime(), DateTimeKind.Utc), Math.Max(0, (long)Elapsed.TotalMilliseconds),
            Name(Server.ToLowerInvariant()), Port, Name(Database), Name(User), kept, full.Length, QueryHistoryText.HashOf(full), Origin, Outcome,
            RowsReturned, RowsAffected, SqlState is { Length: <= 5 } ? SqlState : null, error);
        entry.Validate();
        return entry;
    }
}

/// <summary>Text rules of Query History.</summary>
public static class QueryHistoryText
{
    /// <summary>Whitespace runs (spaces, tabs, line breaks) become one space and the ends are trimmed; case and everything else are kept.
    /// Only used to group executions, so a space inside a string literal also counts as one.</summary>
    public static string Normalize(string sql)
    {
        var text = new StringBuilder(sql.Length);
        bool space = false;
        foreach (char c in sql)
        {
            if (char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
            if (space) { text.Append(' '); space = false; }
            text.Append(c);
        }
        return text.ToString();
    }
    public static string HashOf(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sql))));
    /// <summary>Lone surrogates cannot be stored as UTF-8; they become U+FFFD.</summary>
    public static string Sanitize(string text)
    {
        StringBuilder? copy = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool lone = char.IsHighSurrogate(c) ? i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]) : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]));
            if (lone) { copy ??= new StringBuilder(text, 0, i, text.Length); copy.Append('�'); }
            else copy?.Append(c);
        }
        return copy?.ToString() ?? text;
    }
    /// <summary>At most the given characters, without splitting a surrogate pair.</summary>
    public static string Cut(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        int length = maximum;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
    /// <summary>First line of the SQL, for the grid.</summary>
    public static string Preview(string sql, int maximum = 200)
    {
        string flat = Normalize(sql);
        return flat.Length <= maximum ? flat : Cut(flat, maximum) + "…";
    }
}

/// <summary>One row per distinct query in the Query History window, newest execution first.</summary>
public sealed class QueryHistoryGroup(QueryHistoryKey key, IReadOnlyList<QueryHistoryEntry> executions, bool pinned)
{
    public QueryHistoryKey Key { get; } = key;
    public IReadOnlyList<QueryHistoryEntry> Executions { get; } = executions;
    public QueryHistoryEntry Latest => Executions[0];
    public bool Pinned { get; } = pinned;

    public static IReadOnlyList<QueryHistoryGroup> Build(QueryHistorySnapshot snapshot) =>
        snapshot.Entries.GroupBy(e => e.Key)
            .Select(g => new QueryHistoryGroup(g.Key, g.OrderByDescending(e => e.StartedUtc).ThenByDescending(e => e.Id).ToArray(), snapshot.Pins.Contains(g.Key)))
            .OrderByDescending(g => g.Pinned).ThenByDescending(g => g.Latest.StartedUtc).ToArray();
}
