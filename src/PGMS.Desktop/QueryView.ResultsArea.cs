using System.Windows;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>PGMS-050 (M1110): the results area as in SSMS 22.11 (M1067): Results, Messages and the other output tabs on top of the grid, and no
/// export strip over the results. The export options (format, rows, separator, headers, BOM) come up from the grid's Save Results As… and go away
/// with ✕; they stay up in that query window until hidden.</summary>
public partial class QueryView
{
    internal bool ExportOptionsVisible => ExportOptions.Visibility == Visibility.Visible;

    /// <summary>Shows (or hides) the export options; shown from Save Results As…, the Export button gets the focus.</summary>
    internal void ShowExportOptions(bool show)
    {
        ExportOptions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            OutputTabs.SelectedItem = ResultsTab;
            Dispatcher.BeginInvoke(() => { if (ExportCsvButton.IsEnabled) ExportCsvButton.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void HideExportOptions(object sender, RoutedEventArgs e) => ShowExportOptions(false);

    /// <summary>Tests: the file Save Grid Results would ask for (null = canceled), instead of the Windows dialog.</summary>
    internal Func<string?>? SaveGridResultsPath { get; set; }

    /// <summary>The file types of the SSMS Save Grid Results dialog (inferred from the SSMS of earlier versions; the 22.11 dialog was not opened, since
    /// that needs an executed query): comma-separated .csv, tab-separated .txt and all files (comma-separated).</summary>
    internal const string SaveGridResultsFilter = "CSV (Comma delimited) (*.csv)|*.csv|Text (Tab delimited) (*.txt)|*.txt|All files (*.*)|*.*";

    /// <summary>Save Results As… on a grid (M1114): asks for a file and writes the displayed rows of that result, with headers, UTF-8 without BOM;
    /// tab-separated for .txt, comma-separated otherwise. The export options strip stays under Export Options….</summary>
    internal async void SaveGridResults(System.Windows.Controls.DataGrid grid)
    {
        if (IsBusy) { QueryStatus.Text = "Wait for the current operation before saving results."; return; }
        foreach (var tab in ResultSets.Items.OfType<System.Windows.Controls.TabItem>()) if (tab.Content == grid) ResultSets.SelectedItem = tab;
        string? path;
        if (SaveGridResultsPath is { } pick) path = pick();
        else
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Title = "Save Grid Results", Filter = SaveGridResultsFilter, DefaultExt = ".csv", FileName = "Results.csv", AddExtension = true };
            path = picker.ShowDialog(Window.GetWindow(this)) == true ? picker.FileName : null;
        }
        if (path is null) return;
        bool tabs = string.Equals(System.IO.Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);
        try { await ExportResultsAsync(path, System.IO.File.Exists(path), default, false, new CsvExportOptions(tabs ? '\t' : ',', true, false)); }
        catch (OperationCanceledException) { QueryStatus.Text = "Saving results canceled."; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { QueryStatus.Text = "Saving results failed: " + ex.Message; }
    }
}
