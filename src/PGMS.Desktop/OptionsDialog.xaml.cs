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
    public OptionsDialog(EnvironmentOptions options, Func<Task<int>>? clearHistory = null)
    {
        InitializeComponent();
        this.options = options; this.clearHistory = clearHistory;
        var document = options.Load();
        RestoreWindowBox.IsChecked = document.RestoreWindowPlacement;
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
    }

    /// <summary>Asks before clearing; tests replace it.</summary>
    internal Func<bool>? ConfirmClearHistory { get; set; }

    private async void ClearHistory(object sender, RoutedEventArgs e) => await ClearHistoryAsync();
    internal async Task ClearHistoryAsync()
    {
        if (clearHistory is null) return;
        bool confirmed = ConfirmClearHistory?.Invoke() ?? MessageBox.Show(this, QueryHistoryWindow.ClearQuestion, "Clear Query History", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
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
        options.Update(d => d with { RestoreWindowPlacement = restore, Window = restore ? d.Window : null, SelectTopRows = selectTop.Value, EditTopRows = editTop.Value, DefaultResultsDestination = destination, QueryDefaults = queryDefaults, QueryHistory = historySettings });
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); } // shown without ShowDialog
    }
}
