namespace PGMS.PostgreSql;

/// <summary>Npgsql command read timeout, in seconds; zero disables it.</summary>
/// <summary>Query Options ▸ Execution ▸ Advanced ▸ isolation of later transactions. Stored by number: keep the values.</summary>
public enum QueryIsolationLevel { ServerDefault = 0, ReadCommitted = 1, RepeatableRead = 2, Serializable = 3 }

public sealed record QueryExecutionOptions
{
    public int CommandTimeoutSeconds { get; }
    public QueryNotices? Notices { get; }
    /// <summary>Also sends a batch with <see cref="QueryBatch.StatementEnd"/> after each statement, with the rows it affected or returned (Results to Text/File).</summary>
    public bool ReportStatements { get; init; }
    /// <summary>When set, the plan each statement really used is collected while it runs, without running it twice.</summary>
    public ActualPlanCapture? ActualPlans { get; init; }
    /// <summary>Reads every row and keeps none (Discard results after execution): no columns or rows are delivered, only counts.</summary>
    public bool DiscardResults { get; init; }
    /// <summary>Lifts the row, cell and total text limits: the grid divides the result into pages (M1053). The limits per cell, per row and on columns stay.</summary>
    public bool PagedDisplay { get; init; }
    /// <summary>When set, the display budget is not applied: every row is delivered and each value keeps only its first characters, up to this
    /// limit (Results to File writes all rows; text output never shows more than this per column).</summary>
    public int? TextCellLimit
    {
        get => textCellLimit;
        init
        {
            if (value is < 1 or > QueryDisplayBudget.MaxCellCharacters) throw new ArgumentOutOfRangeException(nameof(TextCellLimit), $"Use 1 through {QueryDisplayBudget.MaxCellCharacters} characters.");
            textCellLimit = value;
        }
    }
    private readonly int? textCellLimit;
    public QueryExecutionOptions(int commandTimeoutSeconds = 0, QueryNotices? notices = null)
    {
        if(commandTimeoutSeconds is < 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds),"Use 0 through 86400 seconds.");
        CommandTimeoutSeconds=commandTimeoutSeconds;
        Notices=notices;
    }
}
