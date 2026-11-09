using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Query ▸ Query Options for one tab; Tools ▸ Options holds the defaults of new tabs (PGMS-033). Only options with a PostgreSQL or client
/// meaning are here (map in docs/MARCO-M1044.md). LockTimeoutMilliseconds -1 keeps the server's value, like the -1 SSMS shows; 0 waits without limit.</summary>
public sealed record QueryOptions(
    int ExecutionTimeoutSeconds = 0, bool CheckOpenTransactions = true,
    bool NoCount = false, QueryIsolationLevel Isolation = QueryIsolationLevel.ServerDefault, int LockTimeoutMilliseconds = -1, bool ShowCompletionTime = true,
    bool RetainLineBreaks = true, bool GridDiscardResults = false, bool DisplayJsonAsLink = true,
    TextOutputFormat TextFormat = TextOutputFormat.ColumnAligned, string CustomDelimiter = ";", bool TextIncludeHeaders = true, bool TextIncludeQuery = false, bool TextRightAlignNumeric = false, bool TextDiscardResults = false, int TextMaxCharacters = TextResultsWriter.DefaultMaxCharacters,
    int GridPageRows = QueryOptions.DefaultGridPageRows)
{
    public const int MaxTimeoutSeconds = 86400, MaxLockTimeoutMilliseconds = 86400000, MaxTextCharacters = 8192, DefaultGridPageRows = 100000, MinGridPageRows = 100, MaxGridPageRows = 1000000;

    /// <summary>Hand-edited files can hold anything: out-of-range values go back to their defaults.</summary>
    public QueryOptions Normalized() => this with
    {
        ExecutionTimeoutSeconds = ExecutionTimeoutSeconds is >= 0 and <= MaxTimeoutSeconds ? ExecutionTimeoutSeconds : 0,
        Isolation = Enum.IsDefined(Isolation) ? Isolation : QueryIsolationLevel.ServerDefault,
        LockTimeoutMilliseconds = LockTimeoutMilliseconds is >= -1 and <= MaxLockTimeoutMilliseconds ? LockTimeoutMilliseconds : -1,
        TextFormat = Enum.IsDefined(TextFormat) ? TextFormat : TextOutputFormat.ColumnAligned,
        CustomDelimiter = CustomDelimiter is { Length: 1 } ? CustomDelimiter : ";",
        TextMaxCharacters = TextMaxCharacters is >= 1 and <= MaxTextCharacters ? TextMaxCharacters : TextResultsWriter.DefaultMaxCharacters,
        GridPageRows = GridPageRows is >= MinGridPageRows and <= MaxGridPageRows ? GridPageRows : DefaultGridPageRows
    };

    internal TextResultsFormat TextLayout => new(TextFormat, CustomDelimiter[0], TextIncludeHeaders, TextRightAlignNumeric);
}
