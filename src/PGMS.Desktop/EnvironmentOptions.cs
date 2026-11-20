using System.IO;
using System.Text.Json;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Options ▸ Environment ▸ General, and the main window size and position it remembers (pedido do usuário, 08/11/2026).
/// Losing the file only costs the convenience, so failures are ignored.</summary>
/// <summary>Tools ▸ Options ▸ Text Editor ▸ Transact-SQL (PGMS-082, M1122): General, Tabs and IntelliSense. The defaults are the editor as it was before
/// (no word wrap, line numbers, tabs of 4 kept as tabs, IntelliSense on).</summary>
public sealed record TextEditorSettings(bool WordWrap = false, bool LineNumbers = true, int TabSize = 4, bool InsertSpaces = false, bool IntelliSense = true)
{
    public const int MinTabSize = 1, MaxTabSize = 16;
    public TextEditorSettings Normalized() => this with { TabSize = TabSize is >= MinTabSize and <= MaxTabSize ? TabSize : 4 };
}

/// <summary>Environment ▸ Tabs and Windows (B1 review, M1141). Insert new tabs to the right starts off, as in SSMS 22.11 (M1143): a new tab goes
/// first, after the pinned ones; applying a saved window layout asks first, as in SSMS, unless the confirmation is skipped.</summary>
public sealed record TabsAndWindowsSettings(bool InsertTabsToRight = false, bool SkipLayoutConfirmation = false);

/// <summary>Environment ▸ Documents (B1 review, M1141), with the SSMS defaults. A query file changed outside PGMS is noticed when PGMS is activated:
/// reloaded at once when its tab has no unsaved changes, otherwise PGMS asks. SaveEncoding applies to every save when UseSaveEncoding is on.</summary>
public sealed record DocumentSettings(bool DetectChanges = true, bool ReloadUnmodified = true, bool AllowEditingReadOnly = true, bool OpenFromActiveDirectory = true,
    bool CheckLineEndings = true, bool SaveInBackground = false, bool UseSaveEncoding = false, SqlFileFormat SaveEncoding = SqlFileFormat.Utf8);

/// <summary>Environment ▸ AutoRecover (B1 review, M1141). The interval is in minutes, 5 by default, as in SSMS 22.11 (M1143); drafts older than the
/// lifetime are discarded instead of restored. IntervalSeconds is only read from files written by M1141, which counted seconds (see
/// <see cref="EnvironmentOptions.Document.Migrated"/>), and never written.</summary>
public sealed record AutoRecoverSettings(bool Enabled = true, int IntervalMinutes = 5, int LifetimeDays = 7,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? IntervalSeconds = null)
{
    public const int DefaultInterval = 5, MinInterval = 1, MaxInterval = 60, MinLifetime = 1, MaxLifetime = 365;
    public AutoRecoverSettings Normalized() => this with { IntervalMinutes = IntervalMinutes is >= MinInterval and <= MaxInterval ? IntervalMinutes : DefaultInterval, LifetimeDays = LifetimeDays is >= MinLifetime and <= MaxLifetime ? LifetimeDays : 7, IntervalSeconds = null };
    /// <summary>M1141 counted seconds, 10 by default: the old default becomes the SSMS 5 minutes; a value the user chose is rounded up to whole minutes.</summary>
    public AutoRecoverSettings FromLegacySeconds() => IntervalSeconds is not { } seconds ? this
        : this with { IntervalMinutes = seconds == 10 ? DefaultInterval : Math.Clamp((seconds + 59) / 60, MinInterval, MaxInterval), IntervalSeconds = null };
}

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
        string? EditorAppearance = null, PresenterModeSettings? PresenterMode = null, bool CheckForUpdates = true, string? SkippedUpdateVersion = null,
        TextEditorSettings? TextEditor = null, string? Language = null,
        TabsAndWindowsSettings? TabsAndWindows = null, DocumentSettings? Documents = null, AutoRecoverSettings? AutoRecover = null,
        SqlFormatterSettings? SqlFormatter = null, ObjectExplorerSettings? ObjectExplorer = null, LiveQueryStatisticsSettings? LiveQueryStatistics = null,
        int OptionsRevision = 0)
    {
        /// <summary>The revision of the defaults this file was written with; files without it are older than M1143.</summary>
        public const int CurrentOptionsRevision = 1;

        /// <summary>M1143 (user's decision of 19/11/2026): six defaults of M1141 that differed from SSMS 22.11 now follow SSMS. A setting the user never
        /// changed is not in the file (Options stores only what differs from the default) and takes the new default by itself. In a file older than this
        /// revision, a value still equal to the old default was not chosen by the user (M1141 wrote the whole record when any of its fields changed, and
        /// always wrote Designers), so it moves to the new default too; any other value is kept. Applied on every load; the next write records the revision.</summary>
        internal Document Migrated() => OptionsRevision >= CurrentOptionsRevision ? this : this with
        {
            OptionsRevision = CurrentOptionsRevision,
            TabsAndWindows = TabsAndWindows is { InsertTabsToRight: true } tabs ? tabs with { InsertTabsToRight = false } : TabsAndWindows,
            AutoRecover = AutoRecover?.FromLegacySeconds(),
            Designers = Designers is { OverrideTimeout: false, TimeoutSeconds: 3600 } designers ? designers with { OverrideTimeout = true } : Designers,
            ObjectExplorer = ObjectExplorer is { HideSystemObjects: true } explorer ? explorer with { HideSystemObjects = false } : ObjectExplorer,
            LiveQueryStatistics = LiveQueryStatistics is { PollingMilliseconds: 1000 } live ? live with { PollingMilliseconds = LiveQueryStatisticsSettings.DefaultPolling } : LiveQueryStatistics,
            SqlFormatter = SqlFormatter is { SpaceBetweenTypeAndParameters: false } formatter ? formatter with { SpaceBetweenTypeAndParameters = true } : SqlFormatter,
        };

        /// <summary>Object Explorer ▸ General and Commands, Live Query Statistics (M1141).</summary>
        public ObjectExplorerSettings ExplorerSettings => ObjectExplorer ?? new();
        public LiveQueryStatisticsSettings LiveStatistics => (LiveQueryStatistics ?? new()).Normalized();
        /// <summary>SQL Formatter (M1142); files without it format as Format SQL always did.</summary>
        public SqlFormatterSettings FormatterSettings => (SqlFormatter ?? new()).Normalized();
        /// <summary>Environment ▸ Tabs and Windows, Documents and AutoRecover (M1141); files without them keep the behavior of before.</summary>
        public TabsAndWindowsSettings TabsSettings => TabsAndWindows ?? new();
        public DocumentSettings DocumentOptions => Documents is { } d && Enum.IsDefined(d.SaveEncoding) ? d : (Documents ?? new()) with { SaveEncoding = SqlFileFormat.Utf8 };
        public AutoRecoverSettings Recovery => (AutoRecover ?? new()).Normalized();
        /// <summary>Text Editor ▸ Transact-SQL (PGMS-082, M1122); files without it keep the editor as it always was.</summary>
        public TextEditorSettings EditorSettings => (TextEditor ?? new()).Normalized();
        /// <summary>Tools ▸ Editor Appearance (PGMS-071, M1113); a missing or unknown name is Match Theme, the behavior of M1108.</summary>
        public EditorAppearanceChoice EditorAppearanceChoice => Enum.TryParse<EditorAppearanceChoice>(EditorAppearance, true, out var choice) && Enum.IsDefined(choice) ? choice : EditorAppearanceChoice.MatchTheme;
        /// <summary>View ▸ Presenter Mode ▸ Edit Presenter Mode Settings (M1113); files without it get the defaults.</summary>
        public PresenterModeSettings Presenter => PresenterMode ?? new();
        /// <summary>Tools ▸ Theme (PGMS-071, M1108, M1123); a missing or unknown name is Cool Breeze, the look PGMS always had. Only names count:
        /// a number in the file is not a theme.</summary>
        public ThemeChoice ThemeChoice => Theme is { } name && !int.TryParse(name, out _) && Enum.TryParse<ThemeChoice>(name, true, out var choice) && Enum.IsDefined(choice) ? choice : ThemeChoice.CoolBreeze;
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

    // Changes queued by UpdateInBackground, written in order on a worker thread (M1132); Load and Update wait for them first.
    private static readonly object pendingGate = new();
    private static Task pendingWrites = Task.CompletedTask; // shared by every instance: they may name the same file

    private static void WaitForPendingWrites()
    {
        Task pending;
        lock (pendingGate) pending = pendingWrites;
        if (!pending.IsCompleted) pending.Wait();
    }

    /// <summary>Applies <paramref name="change"/> off the calling thread, after earlier queued changes; a later Load or Update sees it.</summary>
    public void UpdateInBackground(Func<Document, Document> change)
    {
        lock (pendingGate) pendingWrites = pendingWrites.ContinueWith(_ => UpdateCore(change), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public Document Load()
    {
        WaitForPendingWrites();
        return LoadCore();
    }

    private Document LoadCore()
    {
        try { return (JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) is { Version: 1 } document ? document : new()).Migrated(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Document().Migrated(); }
    }

    public void Update(Func<Document, Document> change)
    {
        WaitForPendingWrites();
        UpdateCore(change);
    }

    private void UpdateCore(Func<Document, Document> change)
    {
        try
        {
            string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(change(LoadCore())));
            File.Move(temporary, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Designers ▸ Table and Database Designers ▸ Diagram Options, as in SSMS 22.11: the view of tables added to a diagram (Column Names) and
/// whether New Database Diagram opens Add Table (on).</summary>
/// <summary>Designers ▸ Table and Database Designers. Table Options (B1 review, M1141): the transaction time-out of Table Designer saves (on with
/// 3600 s by default, as in SSMS 22.11, M1143; off waits as long as needed) and the warning when a table is saved without a primary key (off, as in SSMS).</summary>
public sealed record DesignerOptions(DiagramTableView DefaultTableView = DiagramTableView.ColumnNames, bool LaunchAddTableOnNewDiagram = true,
    bool OverrideTimeout = true, int TimeoutSeconds = 3600, bool WarnNoPrimaryKey = false)
{
    public const int MinTimeout = 1, MaxTimeout = 86400;
    /// <summary>The time-out to give the save, in seconds; 0 means none.</summary>
    public int SaveTimeout => OverrideTimeout && TimeoutSeconds is >= MinTimeout and <= MaxTimeout ? TimeoutSeconds : 0;
}

/// <summary>SQL Server Object Explorer ▸ General and Commands (B1 review, M1141), PostgreSQL counterparts. System objects (pg_catalog,
/// information_schema, template databases) are listed by default, as SSMS 22.11 starts with Hide system objects off (M1143); renaming a key,
/// constraint or trigger asks first, as in SSMS.</summary>
public sealed record ObjectExplorerSettings(bool HideSystemObjects = false, bool ConfirmRename = true);

/// <summary>Query Execution ▸ Live Query Statistics (B1 review, M1141): how often the live statistics are read, in milliseconds (250 by default, the
/// SSMS 22.11 node polling interval, M1143).</summary>
public sealed record LiveQueryStatisticsSettings(int PollingMilliseconds = LiveQueryStatisticsSettings.DefaultPolling)
{
    public const int DefaultPolling = 250, Min = 250, Max = 60000;
    public LiveQueryStatisticsSettings Normalized() => PollingMilliseconds is >= Min and <= Max ? this : new();
}

/// <summary>One Tools ▸ External Tools entry (PGMS-072, M1106), as in the Visual Studio shell of SSMS: it runs only when the user picks it in the
/// Tools menu. Arguments and the initial directory may hold the $(ItemPath)-style macros of <see cref="ExternalTools.Expand"/>.</summary>
public sealed record ExternalTool(string Title, string Command, string Arguments = "", string InitialDirectory = "", bool PromptForArguments = false);

/// <summary>Where a query tab sends its results (Query ▸ Results To). Stored by number in environment.json: keep the values.</summary>
public enum ResultsDestination { Grid = 0, Text = 1, File = 2 }
