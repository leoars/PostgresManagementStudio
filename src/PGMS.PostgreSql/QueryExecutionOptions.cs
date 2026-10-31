namespace PGMS.PostgreSql;

/// <summary>Npgsql command read timeout, in seconds; zero disables it.</summary>
public sealed record QueryExecutionOptions
{
    public int CommandTimeoutSeconds { get; }
    public QueryNotices? Notices { get; }
    public QueryExecutionOptions(int commandTimeoutSeconds = 0, QueryNotices? notices = null)
    {
        if(commandTimeoutSeconds is < 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds),"Use 0 through 86400 seconds.");
        CommandTimeoutSeconds=commandTimeoutSeconds;
        Notices=notices;
    }
}
