using System.IO;
using System.Text.Json;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Options ▸ Environment ▸ General, and the main window size and position it remembers (pedido do usuário, 08/11/2026).
/// Losing the file only costs the convenience, so failures are ignored.</summary>
public sealed class EnvironmentOptions(string path)
{
    /// <summary>Normal (not maximized) bounds in device-independent pixels, and whether the window was maximized.</summary>
    public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);
    /// <summary>RestoreWindowPlacement starts on, as the user chose; SSMS also reopens where it was closed. SelectTopRows and EditTopRows are the
    /// Object Explorer ▸ Commands values for Select Top n Rows and Edit Top n Rows (1000 and 200, as in SSMS); files without them get the defaults.</summary>
    public sealed record Document(int Version = 1, bool RestoreWindowPlacement = true, WindowPlacement? Window = null, int SelectTopRows = DefaultSelectTopRows, int EditTopRows = DefaultEditTopRows,
        ResultsDestination DefaultResultsDestination = ResultsDestination.Grid, QueryOptions? QueryDefaults = null)
    {
        /// <summary>Query Execution and Query Results pages: what new query tabs start with.</summary>
        public QueryOptions QueryOptionDefaults => (QueryDefaults ?? new QueryOptions()).Normalized();
        /// <summary>Query Results ▸ General ▸ Default destination for results of new query tabs; an unknown value falls back to Grid.</summary>
        public ResultsDestination DefaultResults => Enum.IsDefined(DefaultResultsDestination) ? DefaultResultsDestination : ResultsDestination.Grid;
        /// <summary>The value to use: a missing, zero or negative value (hand-edited file) falls back to the default.</summary>
        public int SelectTop => SelectTopRows > 0 ? SelectTopRows : DefaultSelectTopRows;
        public int EditTop => EditTopRows > 0 ? EditTopRows : DefaultEditTopRows;
    }

    public const int DefaultSelectTopRows = 1000, DefaultEditTopRows = 200;

    public static string DefaultPath => Path.Combine(Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGMS"), "environment.json");
    public string FilePath => path;

    public Document Load()
    {
        try { return JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { Version: 1 } document ? document : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Update(Func<Document, Document> change)
    {
        try
        {
            string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(change(Load())));
            File.Move(temporary, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Where a query tab sends its results (Query ▸ Results To). Stored by number in environment.json: keep the values.</summary>
public enum ResultsDestination { Grid = 0, Text = 1, File = 2 }
