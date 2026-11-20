using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Options. Environment ▸ General holds the option to restore the window size and position (pedido do usuário, 08/11/2026);
/// Object Explorer ▸ Commands (SQL Server Object Explorer ▸ Commands in SSMS) holds the row counts of Select Top n Rows and Edit Top n Rows.</summary>
public partial class OptionsDialog : Window
{
    private readonly EnvironmentOptions options;
    private readonly Func<Task<int>>? clearHistory;

    /// <param name="clearHistory">Clears Query History at once (not on OK) and returns how many executions were removed; null hides the button.</param>
    /// <param name="commands">The main menu commands for Environment ▸ Keyboard; null lists none (dialogs opened without the main window).</param>
    public OptionsDialog(EnvironmentOptions options, Func<Task<int>>? clearHistory = null, IReadOnlyList<KeyboardCommand>? commands = null)
    {
        InitializeComponent();
        this.options = options; this.clearHistory = clearHistory;
        var document = options.Load();
        RestoreWindowBox.IsChecked = document.RestoreWindowPlacement;
        RecentFilesBox.Text = document.RecentCount.ToString(CultureInfo.InvariantCulture);
        SelectTopBox.Text = document.SelectTop.ToString(CultureInfo.CurrentCulture);
        EditTopBox.Text = document.EditTop.ToString(CultureInfo.CurrentCulture);
        DefaultResultsBox.SelectedIndex = document.DefaultResults switch { ResultsDestination.Text => 1, ResultsDestination.File => 2, _ => 0 };
        QueryPages.Load(document.QueryOptionDefaults);
        var history = document.History;
        HistoryEnabledBox.IsChecked = history.Enabled;
        HistoryDaysBox.Text = history.RetentionDays.ToString(CultureInfo.InvariantCulture);
        HistoryCountBox.Text = history.MaxExecutions.ToString(CultureInfo.InvariantCulture);
        HistorySizeBox.Text = history.MaxSqlKiB.ToString(CultureInfo.InvariantCulture);
        ClearHistoryButton.Visibility = clearHistory is null ? Visibility.Collapsed : Visibility.Visible;
        var scripting = document.ScriptingSettings;
        ScriptHeadersBox.IsChecked = scripting.IncludeDescriptiveHeaders; ScriptDatabaseBox.IsChecked = scripting.ScriptDatabaseContext; ScriptExistenceBox.IsChecked = scripting.CheckObjectExistence;
        var designers = document.DesignerSettings;
        DefaultTableViewBox.SelectedItem = DefaultTableViewBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == designers.DefaultTableView.ToString());
        LaunchAddTableBox.IsChecked = designers.LaunchAddTableOnNewDiagram;
        SelectRecentBox.IsChecked = document.AutoSelectRecentConnection;
        CheckForUpdatesBox.IsChecked = document.CheckForUpdates;
        // Same as Microsoft Windows (no saved language) first, then the languages by their own names.
        LanguageBox.Items.Add(new ComboBoxItem { Content = "Same as Microsoft Windows", Tag = "" });
        foreach (var (code, name) in Localization.Languages) LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (document.Language ?? "")) ?? LanguageBox.Items[0];
        QueryShortcutsGrid.ItemsSource = shortcutRows = EnvironmentOptions.Document.QueryShortcutKeys.Select(k => new QueryShortcutRow(k) { Text = document.QueryShortcut(k) ?? "" }).ToList();
        var editor = document.EditorSettings;
        EditorWordWrapBox.IsChecked = editor.WordWrap; EditorLineNumbersBox.IsChecked = editor.LineNumbers; EditorIntelliSenseBox.IsChecked = editor.IntelliSense;
        EditorTabSizeBox.Text = editor.TabSize.ToString(CultureInfo.InvariantCulture);
        EditorInsertSpacesBox.IsChecked = editor.InsertSpaces; EditorKeepTabsBox.IsChecked = !editor.InsertSpaces;
        LoadFontsAndColors(document.Appearance);
        LoadKeyboard(document, commands ?? []);
        // M1141: Environment ▸ AutoRecover, Documents, Tabs and Windows.
        var recovery = document.Recovery;
        AutoRecoverBox.IsChecked = recovery.Enabled;
        AutoRecoverIntervalBox.Text = recovery.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        AutoRecoverLifetimeBox.Text = recovery.LifetimeDays.ToString(CultureInfo.InvariantCulture);
        var files = document.DocumentOptions;
        DetectChangesBox.IsChecked = files.DetectChanges; ReloadUnmodifiedBox.IsChecked = files.ReloadUnmodified; AllowReadOnlyBox.IsChecked = files.AllowEditingReadOnly;
        OpenFromActiveBox.IsChecked = files.OpenFromActiveDirectory; CheckLineEndingsBox.IsChecked = files.CheckLineEndings; SaveInBackgroundBox.IsChecked = files.SaveInBackground;
        UseSaveEncodingBox.IsChecked = files.UseSaveEncoding;
        SaveEncodingBox.SelectedItem = SaveEncodingBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == files.SaveEncoding.ToString());
        var tabs = document.TabsSettings;
        InsertTabsRightBox.IsChecked = tabs.InsertTabsToRight; SkipLayoutConfirmBox.IsChecked = tabs.SkipLayoutConfirmation;
        LoadFormatter(document.FormatterSettings);
        // M1143: Table Options, Object Explorer ▸ General and rename, Live Query Statistics.
        OverrideTimeoutBox.IsChecked = designers.OverrideTimeout;
        DesignerTimeoutBox.Text = designers.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        WarnNoPrimaryKeyBox.IsChecked = designers.WarnNoPrimaryKey;
        var explorer = document.ExplorerSettings;
        HideSystemObjectsBox.IsChecked = explorer.HideSystemObjects; ConfirmRenameBox.IsChecked = explorer.ConfirmRename;
        LivePollingBox.Text = document.LiveStatistics.PollingMilliseconds.ToString(CultureInfo.InvariantCulture);
    }

    private (CheckBox Box, Func<SqlFormatterSettings, bool> Get, Func<SqlFormatterSettings, bool, SqlFormatterSettings> Set)[] FormatterBoxes => [
        (FormatAlignBodiesBox, s => s.AlignClauseBodies, (s, v) => s with { AlignClauseBodies = v }), (FormatOnSaveBox, s => s.FormatOnSave, (s, v) => s with { FormatOnSave = v }),
        (FormatMultiSelectBox, s => s.MultilineSelectList, (s, v) => s with { MultilineSelectList = v }), (FormatMultiSetBox, s => s.MultilineSetItems, (s, v) => s with { MultilineSetItems = v }),
        (FormatMultiWhereBox, s => s.MultilineWherePredicates, (s, v) => s with { MultilineWherePredicates = v }), (FormatMultiGroupBox, s => s.MultilineGroupBy, (s, v) => s with { MultilineGroupBy = v }),
        (FormatMultiHavingBox, s => s.MultilineHavingPredicates, (s, v) => s with { MultilineHavingPredicates = v }), (FormatMultiOrderBox, s => s.MultilineOrderBy, (s, v) => s with { MultilineOrderBy = v }),
        (FormatMultiValuesBox, s => s.MultilineValues, (s, v) => s with { MultilineValues = v }),
        (FormatNlFromBox, s => s.NewLineBeforeFrom, (s, v) => s with { NewLineBeforeFrom = v }), (FormatNlWhereBox, s => s.NewLineBeforeWhere, (s, v) => s with { NewLineBeforeWhere = v }),
        (FormatNlGroupBox, s => s.NewLineBeforeGroupBy, (s, v) => s with { NewLineBeforeGroupBy = v }), (FormatNlHavingBox, s => s.NewLineBeforeHaving, (s, v) => s with { NewLineBeforeHaving = v }),
        (FormatNlOrderBox, s => s.NewLineBeforeOrderBy, (s, v) => s with { NewLineBeforeOrderBy = v }), (FormatNlOffsetBox, s => s.NewLineBeforeOffset, (s, v) => s with { NewLineBeforeOffset = v }),
        (FormatNlWindowBox, s => s.NewLineBeforeWindow, (s, v) => s with { NewLineBeforeWindow = v }), (FormatNlReturningBox, s => s.NewLineBeforeReturning, (s, v) => s with { NewLineBeforeReturning = v }),
        (FormatNlJoinBox, s => s.NewLineBeforeJoin, (s, v) => s with { NewLineBeforeJoin = v }), (FormatNlAfterJoinBox, s => s.NewLineAfterJoin, (s, v) => s with { NewLineAfterJoin = v }),
        (FormatNlOnBox, s => s.NewLineBeforeOn, (s, v) => s with { NewLineBeforeOn = v }), (FormatNlCloseBox, s => s.NewLineBeforeClosingParenthesis, (s, v) => s with { NewLineBeforeClosingParenthesis = v }),
        (FormatTypeSpaceBox, s => s.SpaceBetweenTypeAndParameters, (s, v) => s with { SpaceBetweenTypeAndParameters = v })];

    private void LoadFormatter(SqlFormatterSettings settings)
    {
        foreach (var (box, get, _) in FormatterBoxes) box.IsChecked = get(settings);
        FormatCasingBox.SelectedItem = FormatCasingBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == settings.KeywordCasing.ToString());
        FormatIndentBox.Text = settings.IndentationSize.ToString(CultureInfo.InvariantCulture);
        FormatNewLinesBox.Text = settings.NewLinesAfterStatement.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The SQL Formatter pages (M1142), or null after showing what is wrong.</summary>
    private SqlFormatterSettings? ReadFormatter()
    {
        int? indent = ParseRange(FormatIndentBox.Text, SqlFormatterSettings.MinIndentation, SqlFormatterSettings.MaxIndentation);
        int? lines = ParseRange(FormatNewLinesBox.Text, 0, SqlFormatterSettings.MaxNewLines);
        if (indent is null || lines is null)
        {
            var (wrong, page) = indent is null ? (FormatIndentBox, FormatIndentationPage) : (FormatNewLinesBox, FormatNewLinePage);
            var error = indent is null ? FormatterError : FormatNewLinesError;
            error.Text = indent is null ? $"Enter an indentation size from {SqlFormatterSettings.MinIndentation} to {SqlFormatterSettings.MaxIndentation}." : $"Enter a number of new lines from 0 to {SqlFormatterSettings.MaxNewLines}.";
            error.Visibility = Visibility.Visible;
            page.IsSelected = true;
            System.Windows.Input.FocusManager.SetFocusedElement(this, wrong);
            wrong.Focus(); wrong.SelectAll();
            return null;
        }
        var settings = new SqlFormatterSettings(Enum.Parse<SqlKeywordCasing>((string)((ComboBoxItem)FormatCasingBox.SelectedItem).Tag)) with { IndentationSize = indent.Value, NewLinesAfterStatement = lines.Value };
        foreach (var (box, _, set) in FormatterBoxes) settings = set(settings, box.IsChecked == true);
        return settings;
    }

    /// <summary>The Environment pages of M1141, or null after showing what is wrong.</summary>
    private (AutoRecoverSettings Recovery, DocumentSettings Documents, TabsAndWindowsSettings Tabs)? ReadEnvironmentPages()
    {
        int? interval = ParseRange(AutoRecoverIntervalBox.Text, AutoRecoverSettings.MinInterval, AutoRecoverSettings.MaxInterval);
        int? lifetime = ParseRange(AutoRecoverLifetimeBox.Text, AutoRecoverSettings.MinLifetime, AutoRecoverSettings.MaxLifetime);
        if (interval is null || lifetime is null)
        {
            var wrong = interval is null ? AutoRecoverIntervalBox : AutoRecoverLifetimeBox;
            AutoRecoverError.Text = interval is null ? $"Enter a backup interval from {AutoRecoverSettings.MinInterval} to {AutoRecoverSettings.MaxInterval} minutes." : $"Enter a lifetime from {AutoRecoverSettings.MinLifetime} to {AutoRecoverSettings.MaxLifetime} days.";
            AutoRecoverError.Visibility = Visibility.Visible;
            AutoRecoverPage.IsSelected = true;
            System.Windows.Input.FocusManager.SetFocusedElement(this, wrong);
            wrong.Focus(); wrong.SelectAll();
            return null;
        }
        var recovery = new AutoRecoverSettings(AutoRecoverBox.IsChecked == true, interval.Value, lifetime.Value);
        var documents = new DocumentSettings(DetectChangesBox.IsChecked == true, ReloadUnmodifiedBox.IsChecked == true, AllowReadOnlyBox.IsChecked == true, OpenFromActiveBox.IsChecked == true,
            CheckLineEndingsBox.IsChecked == true, SaveInBackgroundBox.IsChecked == true, UseSaveEncodingBox.IsChecked == true, Enum.Parse<SqlFileFormat>((string)((ComboBoxItem)SaveEncodingBox.SelectedItem).Tag));
        var tabs = new TabsAndWindowsSettings(InsertTabsRightBox.IsChecked == true, SkipLayoutConfirmBox.IsChecked == true);
        return (recovery, documents, tabs);
    }

    private readonly List<QueryShortcutRow> shortcutRows = [];
    /// <summary>One row of Environment ▸ Query Shortcuts.</summary>
    public sealed class QueryShortcutRow(string shortcut)
    {
        public string Shortcut { get; } = shortcut;
        public string Text { get; set; } = "";
    }
    internal IReadOnlyList<QueryShortcutRow> QueryShortcutRows => shortcutRows;

    /// <summary>Asks before clearing; tests replace it.</summary>
    internal Func<bool>? ConfirmClearHistory { get; set; }

    private async void ClearHistory(object sender, RoutedEventArgs e) => await ClearHistoryAsync();
    internal async Task ClearHistoryAsync()
    {
        if (clearHistory is null) return;
        bool confirmed = ConfirmClearHistory?.Invoke() ?? LocalizedMessageBox.Show(this, QueryHistoryWindow.ClearQuestion, "Clear Query History", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;
        ClearHistoryButton.IsEnabled = false; ClearHistoryStatus.Text = "Clearing…";
        try { int removed = await clearHistory(); ClearHistoryStatus.Text = $"Query History cleared: {removed:N0} execution(s) removed."; }
        catch (Exception ex) when (QueryHistoryRecorder.IsDiskError(ex)) { ClearHistoryStatus.Text = "Query History was not cleared. " + ex.Message; }
        finally { ClearHistoryButton.IsEnabled = true; }
    }

    /// <summary>Whole number within the range, digits only.</summary>
    internal static int? ParseRange(string text, int minimum, int maximum) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= minimum && value <= maximum ? value : null;

    private void PageChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (CommandsPanel is null || QueryResultsPanel is null || QueryPages is null) return; // General is selected in XAML before the panels exist; it is already the visible page
        // A category node shows its first page, as the SSMS tree does.
        var page = e.NewValue as TreeViewItem;
        if (page is { Items.Count: > 0 } && page.Items[0] is TreeViewItem first) page = first;
        GeneralPanel.Visibility = page == GeneralPage ? Visibility.Visible : Visibility.Collapsed;
        QueryResultsPanel.Visibility = page == QueryResultsPage ? Visibility.Visible : Visibility.Collapsed;
        CommandsPanel.Visibility = page == CommandsPage ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = page == HistoryPage ? Visibility.Visible : Visibility.Collapsed;
        ScriptingPanel.Visibility = page == ScriptingPage ? Visibility.Visible : Visibility.Collapsed;
        QueryShortcutsPanel.Visibility = page == QueryShortcutsPage ? Visibility.Visible : Visibility.Collapsed;
        KeyboardPanel.Visibility = page == KeyboardPage ? Visibility.Visible : Visibility.Collapsed;
        DesignersPanel.Visibility = page == DesignersPage ? Visibility.Visible : Visibility.Collapsed;
        ConnectionDialogPanel.Visibility = page == ConnectionDialogPage ? Visibility.Visible : Visibility.Collapsed;
        ProductUpdatesPanel.Visibility = page == ProductUpdatesPage ? Visibility.Visible : Visibility.Collapsed;
        InternationalPanel.Visibility = page == InternationalPage ? Visibility.Visible : Visibility.Collapsed;
        FontsAndColorsPanel.Visibility = page == FontsAndColorsPage ? Visibility.Visible : Visibility.Collapsed;
        TextEditorPanel.Visibility = page == TextEditorPage ? Visibility.Visible : Visibility.Collapsed;
        AutoRecoverPanel.Visibility = page == AutoRecoverPage ? Visibility.Visible : Visibility.Collapsed;
        DocumentsPanel.Visibility = page == DocumentsPage ? Visibility.Visible : Visibility.Collapsed;
        TabsPanel.Visibility = page == TabsPage ? Visibility.Visible : Visibility.Collapsed;
        TaskListPanel.Visibility = page == TaskListPage ? Visibility.Visible : Visibility.Collapsed;
        WindowsPanel.Visibility = page == WindowsPage ? Visibility.Visible : Visibility.Collapsed;
        FormatAlignmentPanel.Visibility = page == FormatAlignmentPage ? Visibility.Visible : Visibility.Collapsed;
        FormatFormattingPanel.Visibility = page == FormatFormattingPage ? Visibility.Visible : Visibility.Collapsed;
        FormatGeneralPanel.Visibility = page == FormatGeneralPage ? Visibility.Visible : Visibility.Collapsed;
        FormatIndentationPanel.Visibility = page == FormatIndentationPage ? Visibility.Visible : Visibility.Collapsed;
        FormatMultilinePanel.Visibility = page == FormatMultilinePage ? Visibility.Visible : Visibility.Collapsed;
        FormatNewLinePanel.Visibility = page == FormatNewLinePage ? Visibility.Visible : Visibility.Collapsed;
        FormatSpacingPanel.Visibility = page == FormatSpacingPage ? Visibility.Visible : Visibility.Collapsed;
        ExplorerGeneralPanel.Visibility = page == ExplorerGeneralPage ? Visibility.Visible : Visibility.Collapsed;
        LiveStatisticsPanel.Visibility = page == LiveStatisticsPage ? Visibility.Visible : Visibility.Collapsed;
        int shared = Array.IndexOf(SharedPages, page);
        QueryPages.Visibility = shared >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shared >= 0) QueryPages.ShowPage(shared);
    }

    /// <summary>Tree nodes of the pages shared with Query ▸ Query Options, in the order of the QueryOptionsPages indexes.</summary>
    private TreeViewItem[] SharedPages => [ExecutionGeneralPage, ExecutionAdvancedPage, ResultsGridPage, ResultsTextPage];

    /// <summary>Whole numbers from 1 up, digits only: no thousands separators, which would read "12.5" as 125 in pt-BR.</summary>
    internal static int? ParseRowCount(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : null;

    internal void Accept(object sender, RoutedEventArgs e)
    {
        if (ParseRange(RecentFilesBox.Text, 1, RecentFileList.MaximumCount) is not { } recentCount)
        {
            GeneralError.Text = $"Enter a whole number of recent files from 1 to {RecentFileList.MaximumCount}.";
            GeneralError.Visibility = Visibility.Visible;
            GeneralPage.IsSelected = true;
            System.Windows.Input.FocusManager.SetFocusedElement(this, RecentFilesBox);
            RecentFilesBox.Focus(); RecentFilesBox.SelectAll();
            return;
        }
        int? selectTop = ParseRowCount(SelectTopBox.Text), editTop = ParseRowCount(EditTopBox.Text);
        if (selectTop is null || editTop is null)
        {
            var wrong = selectTop is null ? SelectTopBox : EditTopBox;
            CommandsError.Text = $"Enter a whole number from 1 to {int.MaxValue.ToString("N0", CultureInfo.CurrentCulture)} for the {(selectTop is null ? "Select Top" : "Edit Top")} <n> Rows command.";
            CommandsError.Visibility = Visibility.Visible;
            CommandsPage.IsSelected = true;
            // Logical focus too: when the dialog is not the active window, Focus() alone does not move it, and activation restores it.
            System.Windows.Input.FocusManager.SetFocusedElement(this, wrong);
            wrong.Focus(); wrong.SelectAll();
            return;
        }
        if (ParseRange(EditorTabSizeBox.Text, TextEditorSettings.MinTabSize, TextEditorSettings.MaxTabSize) is not { } tabSize)
        {
            TextEditorError.Text = $"Enter a tab and indent size from {TextEditorSettings.MinTabSize} to {TextEditorSettings.MaxTabSize}.";
            TextEditorError.Visibility = Visibility.Visible;
            TextEditorPage.IsSelected = true;
            System.Windows.Input.FocusManager.SetFocusedElement(this, EditorTabSizeBox);
            EditorTabSizeBox.Focus(); EditorTabSizeBox.SelectAll();
            return;
        }
        var textEditor = new TextEditorSettings(EditorWordWrapBox.IsChecked == true, EditorLineNumbersBox.IsChecked == true, tabSize, EditorInsertSpacesBox.IsChecked == true, EditorIntelliSenseBox.IsChecked == true);
        if (QueryPages.TryRead() is not { } queryDefaults) { SharedPages[QueryPages.RejectedPage].IsSelected = true; return; }
        int? days = ParseRange(HistoryDaysBox.Text, QueryHistorySettings.MinRetentionDays, QueryHistorySettings.MaxRetentionDays);
        int? count = ParseRange(HistoryCountBox.Text, QueryHistorySettings.MinExecutions, QueryHistorySettings.MaxExecutionsLimit);
        int? size = ParseRange(HistorySizeBox.Text, QueryHistorySettings.MinSqlKiB, QueryHistorySettings.MaxSqlKiBLimit);
        if (days is null || count is null || size is null)
        {
            var (wrong, message) = days is null ? (HistoryDaysBox, $"Enter a whole number of days from {QueryHistorySettings.MinRetentionDays} to {QueryHistorySettings.MaxRetentionDays:N0}.")
                : count is null ? (HistoryCountBox, $"Enter a whole number of executions from {QueryHistorySettings.MinExecutions} to {QueryHistorySettings.MaxExecutionsLimit:N0}.")
                : (HistorySizeBox, $"Enter a whole number of KiB from {QueryHistorySettings.MinSqlKiB} to {QueryHistorySettings.MaxSqlKiBLimit}.");
            HistoryError.Text = message; HistoryError.Visibility = Visibility.Visible;
            HistoryPage.IsSelected = true;
            System.Windows.Input.FocusManager.SetFocusedElement(this, wrong);
            wrong.Focus(); wrong.SelectAll();
            return;
        }
        var historySettings = new QueryHistorySettings(HistoryEnabledBox.IsChecked == true, days.Value, count.Value, size.Value);
        bool restore = RestoreWindowBox.IsChecked == true;
        // Turning the option off also forgets the saved size and position.
        var destination = DefaultResultsBox.SelectedIndex switch { 1 => ResultsDestination.Text, 2 => ResultsDestination.File, _ => ResultsDestination.Grid };
        var scripting = new PGMS.PostgreSql.ScriptingOptions(ScriptHeadersBox.IsChecked == true, ScriptDatabaseBox.IsChecked == true, ScriptExistenceBox.IsChecked == true);
        var tableView = Enum.Parse<DiagramTableView>((string)((ComboBoxItem)DefaultTableViewBox.SelectedItem).Tag);
        var designers = new DesignerOptions(tableView, LaunchAddTableBox.IsChecked == true, OverrideTimeoutBox.IsChecked == true,
            ParseRange(DesignerTimeoutBox.Text, DesignerOptions.MinTimeout, DesignerOptions.MaxTimeout) ?? 3600, WarnNoPrimaryKeyBox.IsChecked == true);
        bool selectRecent = SelectRecentBox.IsChecked == true, checkUpdates = CheckForUpdatesBox.IsChecked == true;
        string? language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } code ? code : null;
        if (ReadFontsAndColors() is not { } fontsAndColors) return;
        if (ReadEnvironmentPages() is not { } environment) return;
        if (ReadFormatter() is not { } formatter) return;
        int? designerTimeout = ParseRange(DesignerTimeoutBox.Text, DesignerOptions.MinTimeout, DesignerOptions.MaxTimeout) ?? (OverrideTimeoutBox.IsChecked == true ? null : 3600);
        int? polling = ParseRange(LivePollingBox.Text, LiveQueryStatisticsSettings.Min, LiveQueryStatisticsSettings.Max);
        if (designerTimeout is null || polling is null)
        {
            var (wrong, error, page, message) = designerTimeout is null
                ? (DesignerTimeoutBox, DesignersError, DesignersPage, $"Enter a time-out from {DesignerOptions.MinTimeout} to {DesignerOptions.MaxTimeout:N0} seconds.")
                : (LivePollingBox, LivePollingError, LiveStatisticsPage, $"Enter a polling interval from {LiveQueryStatisticsSettings.Min} to {LiveQueryStatisticsSettings.Max:N0} milliseconds.");
            error.Text = message; error.Visibility = Visibility.Visible; page.IsSelected = true;
            if (designerTimeout is null) OverrideTimeoutBox.IsChecked = true;
            System.Windows.Input.FocusManager.SetFocusedElement(this, wrong);
            wrong.Focus(); wrong.SelectAll();
            return;
        }
        designers = designers with { TimeoutSeconds = designerTimeout.Value };
        var explorerOptions = new ObjectExplorerSettings(HideSystemObjectsBox.IsChecked == true, ConfirmRenameBox.IsChecked == true);
        var live = new LiveQueryStatisticsSettings(polling.Value);
        QueryShortcutsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var shortcuts = shortcutRows.Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToDictionary(r => r.Shortcut, r => r.Text.Trim());
        options.Update(d => d with { RestoreWindowPlacement = restore, Window = restore ? d.Window : null, SelectTopRows = selectTop.Value, EditTopRows = editTop.Value, DefaultResultsDestination = destination, QueryDefaults = queryDefaults, QueryHistory = historySettings,
            RecentFilesCount = recentCount, RecentFiles = d.RecentFiles?.Take(recentCount).ToList(), Scripting = scripting, Designers = designers, AutoSelectRecentConnection = selectRecent, CheckForUpdates = checkUpdates, QueryShortcuts = shortcuts,
            OptionsRevision = EnvironmentOptions.Document.CurrentOptionsRevision, KeyboardShortcuts = keyboardShortcuts.Count == 0 ? null : new Dictionary<string, string>(keyboardShortcuts), FontsAndColors = fontsAndColors == new FontsAndColorsSettings() ? null : fontsAndColors,
            TextEditor = textEditor == new TextEditorSettings() ? null : textEditor, Language = language,
            AutoRecover = environment.Recovery == new AutoRecoverSettings() ? null : environment.Recovery, Documents = environment.Documents == new DocumentSettings() ? null : environment.Documents,
            TabsAndWindows = environment.Tabs == new TabsAndWindowsSettings() ? null : environment.Tabs, SqlFormatter = formatter == new SqlFormatterSettings() ? null : formatter,
            ObjectExplorer = explorerOptions == new ObjectExplorerSettings() ? null : explorerOptions, LiveQueryStatistics = live == new LiveQueryStatisticsSettings() ? null : live });
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); } // shown without ShowDialog
    }
}
