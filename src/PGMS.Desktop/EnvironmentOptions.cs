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
        ResultsDestination DefaultResultsDestination = ResultsDestination.Grid, QueryOptions? QueryDefaults = null, QueryHistorySettings? QueryHistory = null,
        int RecentFilesCount = RecentFileList.DefaultCount, IReadOnlyList<string>? RecentFiles = null,
        PGMS.PostgreSql.ScriptingOptions? Scripting = null, DesignerOptions? Designers = null, bool AutoSelectRecentConnection = true,
        IReadOnlyDictionary<string, string>? QueryShortcuts = null, FontsAndColorsSettings? FontsAndColors = null,
        IReadOnlyDictionary<string, string>? KeyboardShortcuts = null, IReadOnlyList<ExternalTool>? ExternalTools = null, IReadOnlyList<string>? HiddenToolbars = null, string? Theme = null,
        string? EditorAppearance = null, PresenterModeSettings? PresenterMode = null)
    {
        /// <summary>Tools ▸ Editor Appearance (PGMS-071, M1113); a missing or unknown name is Match Theme, the behavior of M1108.</summary>
        public EditorAppearanceChoice EditorAppearanceChoice => Enum.TryParse<EditorAppearanceChoice>(EditorAppearance, true, out var choice) && Enum.IsDefined(choice) ? choice : EditorAppearanceChoice.MatchTheme;
        /// <summary>View ▸ Presenter Mode ▸ Edit Presenter Mode Settings (M1113); files without it get the defaults.</summary>
        public PresenterModeSettings Presenter => PresenterMode ?? new();
        /// <summary>Tools ▸ Theme (PGMS-071, M1108); a missing or unknown name is Light, the look PGMS always had.</summary>
        public ThemeChoice ThemeChoice => Enum.TryParse<ThemeChoice>(Theme, true, out var choice) && Enum.IsDefined(choice) ? choice : ThemeChoice.Light;
        /// <summary>Environment ▸ Keyboard (PGMS-072, M1106): the shortcut the user gave each command, by command name ("Query.Execute").</summary>
        public IReadOnlyDictionary<string, string> CustomShortcuts => KeyboardShortcuts ?? new Dictionary<string, string>();
        /// <summary>Tools ▸ External Tools (PGMS-072, M1106), in menu order; entries without title or command are ignored.</summary>
        public IReadOnlyList<ExternalTool> Tools => (ExternalTools ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Title) && !string.IsNullOrWhiteSpace(t.Command)).ToList();
        /// <summary>View ▸ Toolbars and Tools ▸ Customize (PGMS-072, M1106): the toolbars the user turned off, by name.</summary>
        public bool ToolbarVisible(string name) => HiddenToolbars is null || !HiddenToolbars.Contains(name);
        /// <summary>Environment ▸ Fonts and Colors (PGMS-071); files without it keep the PGMS look.</summary>
        public FontsAndColorsSettings Appearance => FontsAndColors ?? new();
        /// <summary>Environment ▸ Keyboard ▸ Query Shortcuts (PGMS-072): the text for a shortcut, or null when none is set.</summary>
        public string? QueryShortcut(string shortcut) => QueryShortcuts is not null && QueryShortcuts.TryGetValue(shortcut, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        /// <summary>The shortcuts Query Shortcuts offers, in the SSMS 22.11 order without Ctrl+F1, which is View Help in PGMS.</summary>
        public static readonly IReadOnlyList<string> QueryShortcutKeys = ["Alt+F1", "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9", "Ctrl+0"];
        /// <summary>Object Explorer ▸ Scripting (PGMS-073); files without it script as before.</summary>
        public PGMS.PostgreSql.ScriptingOptions ScriptingSettings => Scripting ?? new();
        /// <summary>Designers ▸ Table and Database Designers (PGMS-073); an unknown table view falls back to Column Names.</summary>
        public DesignerOptions DesignerSettings => Designers is { } d && Enum.IsDefined(d.DefaultTableView) ? d : (Designers ?? new()) with { DefaultTableView = DiagramTableView.ColumnNames };
        /// <summary>Environment ▸ General ▸ Items shown in Recent Files (PGMS-059): 1 to 24, 10 by default as in SSMS.</summary>
        public int RecentCount => RecentFilesCount is >= 1 and <= RecentFileList.MaximumCount ? RecentFilesCount : RecentFileList.DefaultCount;
        /// <summary>Query Execution ▸ Query History (PGMS-043); files without it record history with the default limits.</summary>
        public QueryHistorySettings History => (QueryHistory ?? new QueryHistorySettings()).Normalized();
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

/// <summary>Designers ▸ Table and Database Designers ▸ Diagram Options, as in SSMS 22.11: the view of tables added to a diagram (Column Names) and
/// whether New Database Diagram opens Add Table (on).</summary>
public sealed record DesignerOptions(DiagramTableView DefaultTableView = DiagramTableView.ColumnNames, bool LaunchAddTableOnNewDiagram = true);

/// <summary>One Tools ▸ External Tools entry (PGMS-072, M1106), as in the Visual Studio shell of SSMS: it runs only when the user picks it in the
/// Tools menu. Arguments and the initial directory may hold the $(ItemPath)-style macros of <see cref="ExternalTools.Expand"/>.</summary>
public sealed record ExternalTool(string Title, string Command, string Arguments = "", string InitialDirectory = "", bool PromptForArguments = false);

/// <summary>Where a query tab sends its results (Query ▸ Results To). Stored by number in environment.json: keep the values.</summary>
public enum ResultsDestination { Grid = 0, Text = 1, File = 2 }
