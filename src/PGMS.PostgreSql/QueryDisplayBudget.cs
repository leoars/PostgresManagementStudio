namespace PGMS.PostgreSql;

// Bounds retained UTF-16 text, not total managed heap, metadata or UI overhead.
// A row that exceeds any text budget is omitted whole; subsequent rows are drained.
public static class QueryDisplayBudget
{
    public const int MaxColumnsPerResult = 256;
    public const int MaxTotalColumns = 4096;
    public const int MaxCells = 250000;
    public const int MaxRows = 10000;
    public const int MaxResultSets = 100;
    public const int MaxCellCharacters = 65536;
    public const int MaxRowCharacters = 262144;
    public const int MaxTextCharacters = 8 * 1024 * 1024;
    public const int MaxBatchCells = 4096;
    public const int MaxBatchCharacters = 65536;
    public const string Description = "10,000 rows / 100 result sets / 256 columns per result / 4,096 total columns / 250,000 cells / 16 MiB retained text; 128 KiB per cell / 512 KiB per row (UTF-16).";
}
