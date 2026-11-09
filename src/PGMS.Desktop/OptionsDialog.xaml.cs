using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Options. Environment ▸ General holds the option to restore the window size and position (pedido do usuário, 08/11/2026);
/// Object Explorer ▸ Commands (SQL Server Object Explorer ▸ Commands in SSMS) holds the row counts of Select Top n Rows and Edit Top n Rows.</summary>
public partial class OptionsDialog : Window
{
    private readonly EnvironmentOptions options;

    public OptionsDialog(EnvironmentOptions options)
    {
        InitializeComponent();
        this.options = options;
        var document = options.Load();
        RestoreWindowBox.IsChecked = document.RestoreWindowPlacement;
        SelectTopBox.Text = document.SelectTop.ToString(CultureInfo.CurrentCulture);
        EditTopBox.Text = document.EditTop.ToString(CultureInfo.CurrentCulture);
        DefaultResultsBox.SelectedIndex = document.DefaultResults switch { ResultsDestination.Text => 1, ResultsDestination.File => 2, _ => 0 };
        QueryPages.Load(document.QueryOptionDefaults);
    }

    private void PageChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (CommandsPanel is null || QueryResultsPanel is null || QueryPages is null) return; // General is selected in XAML before the panels exist; it is already the visible page
        // A category node shows its first page, as the SSMS tree does.
        var page = e.NewValue as TreeViewItem;
        if (page is { Items.Count: > 0 } && page.Items[0] is TreeViewItem first) page = first;
        GeneralPanel.Visibility = page == GeneralPage ? Visibility.Visible : Visibility.Collapsed;
        QueryResultsPanel.Visibility = page == QueryResultsPage ? Visibility.Visible : Visibility.Collapsed;
        CommandsPanel.Visibility = page == CommandsPage ? Visibility.Visible : Visibility.Collapsed;
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
        bool restore = RestoreWindowBox.IsChecked == true;
        // Turning the option off also forgets the saved size and position.
        var destination = DefaultResultsBox.SelectedIndex switch { 1 => ResultsDestination.Text, 2 => ResultsDestination.File, _ => ResultsDestination.Grid };
        options.Update(d => d with { RestoreWindowPlacement = restore, Window = restore ? d.Window : null, SelectTopRows = selectTop.Value, EditTopRows = editTop.Value, DefaultResultsDestination = destination, QueryDefaults = queryDefaults });
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); } // shown without ShowDialog
    }
}
