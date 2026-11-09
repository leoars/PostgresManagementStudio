using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>The four option pages, shared by Query ▸ Query Options (one tab) and Tools ▸ Options (defaults of new tabs).</summary>
public partial class QueryOptionsPages : UserControl
{
    internal const int ExecutionGeneral = 0, ExecutionAdvanced = 1, ResultsGrid = 2, ResultsText = 3;
    private int page;

    public QueryOptionsPages() => InitializeComponent();

    internal void ShowPage(int index)
    {
        page = index;
        ExecutionGeneralPanel.Visibility = index == ExecutionGeneral ? Visibility.Visible : Visibility.Collapsed;
        ExecutionAdvancedPanel.Visibility = index == ExecutionAdvanced ? Visibility.Visible : Visibility.Collapsed;
        ResultsGridPanel.Visibility = index == ResultsGrid ? Visibility.Visible : Visibility.Collapsed;
        ResultsTextPanel.Visibility = index == ResultsText ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void Load(QueryOptions options) { for (int i = ExecutionGeneral; i <= ResultsText; i++) LoadPage(i, options); }

    /// <summary>Reset to Default changes only the page on screen, as in SSMS.</summary>
    private void ResetPage(object sender, RoutedEventArgs e) { ErrorText.Visibility = Visibility.Collapsed; LoadPage(page, new QueryOptions()); }
    internal void ResetCurrentPage() => ResetPage(this, new RoutedEventArgs());

    private void LoadPage(int index, QueryOptions o)
    {
        switch (index)
        {
            case ExecutionGeneral:
                TimeoutBox.Text = o.ExecutionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
                CheckTransactionsBox.IsChecked = o.CheckOpenTransactions;
                break;
            case ExecutionAdvanced:
                NoCountBox.IsChecked = o.NoCount;
                CompletionBox.IsChecked = o.ShowCompletionTime;
                IsolationBox.SelectedIndex = (int)o.Isolation;
                LockBox.Text = o.LockTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
                break;
            case ResultsGrid:
                RetainBox.IsChecked = o.RetainLineBreaks;
                GridDiscardBox.IsChecked = o.GridDiscardResults;
                JsonLinkBox.IsChecked = o.DisplayJsonAsLink;
                PageRowsBox.Text = o.GridPageRows.ToString(CultureInfo.InvariantCulture);
                break;
            case ResultsText:
                FormatBox.SelectedIndex = (int)o.TextFormat;
                DelimiterBox.Text = o.CustomDelimiter;
                TextHeadersBox.IsChecked = o.TextIncludeHeaders;
                TextQueryBox.IsChecked = o.TextIncludeQuery;
                RightAlignBox.IsChecked = o.TextRightAlignNumeric;
                TextDiscardBox.IsChecked = o.TextDiscardResults;
                MaxCharactersBox.Text = o.TextMaxCharacters.ToString(CultureInfo.InvariantCulture);
                UpdateDelimiter();
                break;
        }
    }

    private void FormatChanged(object sender, SelectionChangedEventArgs e) => UpdateDelimiter();
    // Right alignment needs padded columns, so it applies only to Column aligned.
    private void UpdateDelimiter()
    {
        if (DelimiterBox is null || RightAlignBox is null) return;
        DelimiterBox.IsEnabled = FormatBox.SelectedIndex == (int)TextOutputFormat.CustomDelimited;
        RightAlignBox.IsEnabled = FormatBox.SelectedIndex == (int)TextOutputFormat.ColumnAligned;
    }

    /// <summary>Null means a value is wrong: its page is shown with the message and the box selected.</summary>
    internal QueryOptions? TryRead()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!int.TryParse(TimeoutBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int timeout) || timeout > QueryOptions.MaxTimeoutSeconds)
            return Reject(ExecutionGeneral, TimeoutBox, $"Enter an execution time-out from 0 to {QueryOptions.MaxTimeoutSeconds} seconds.");
        if (!int.TryParse(LockBox.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lockTimeout) || lockTimeout is < -1 or > QueryOptions.MaxLockTimeoutMilliseconds)
            return Reject(ExecutionAdvanced, LockBox, $"Enter a lock timeout from -1 to {QueryOptions.MaxLockTimeoutMilliseconds} milliseconds.");
        if (!int.TryParse(PageRowsBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pageRows) || pageRows is < QueryOptions.MinGridPageRows or > QueryOptions.MaxGridPageRows)
            return Reject(ResultsGrid, PageRowsBox, $"Enter rows per page from {QueryOptions.MinGridPageRows} to {QueryOptions.MaxGridPageRows}.");
        var format = (TextOutputFormat)Math.Max(0, FormatBox.SelectedIndex);
        if (format == TextOutputFormat.CustomDelimited && DelimiterBox.Text.Length != 1)
            return Reject(ResultsText, DelimiterBox, "Enter one character as the custom delimiter.");
        if (!int.TryParse(MaxCharactersBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int maxCharacters) || maxCharacters is < 1 or > QueryOptions.MaxTextCharacters)
            return Reject(ResultsText, MaxCharactersBox, $"Enter a maximum from 1 to {QueryOptions.MaxTextCharacters} characters.");
        return new QueryOptions(timeout, CheckTransactionsBox.IsChecked == true,
            NoCountBox.IsChecked == true, (QueryIsolationLevel)Math.Max(0, IsolationBox.SelectedIndex), lockTimeout, CompletionBox.IsChecked == true,
            RetainBox.IsChecked == true, GridDiscardBox.IsChecked == true, JsonLinkBox.IsChecked == true,
            format, DelimiterBox.Text.Length == 1 ? DelimiterBox.Text : ";", TextHeadersBox.IsChecked == true, TextQueryBox.IsChecked == true, RightAlignBox.IsChecked == true, TextDiscardBox.IsChecked == true, maxCharacters, pageRows);
    }

    /// <summary>Page holding the last rejected value, for the host to select it in its tree.</summary>
    internal int RejectedPage { get; private set; }

    private QueryOptions? Reject(int index, TextBox box, string message)
    {
        RejectedPage = index;
        ShowPage(index);
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
        return null;
    }
}
