using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PGMS.Desktop;

/// <summary>
/// File ▸ Page Setup… and File ▸ Print… (PGMS-074, M1096). SSMS 22.11 opens the Windows Page Setup and Print dialogs for the editor; PGMS has its own
/// Page Setup (WPF has none) and the Windows Print dialog, printing the active query's SQL, or its selection, with a header on each page.
/// The page setup lasts for the session, like the dialog's last values in SSMS.
/// </summary>
public partial class MainWindow
{
    internal PrintPageSetup PageSetup { get; set; } = new();
    /// <summary>Replaces the Page Setup dialog in tests: receives the current setup, returns the new one or null for Cancel.</summary>
    internal Func<PrintPageSetup, PrintPageSetup?>? PageSetupPrompt { get; set; }
    /// <summary>Replaces showing the Print dialog in tests: receives the prepared dialog, returns true for Print.</summary>
    internal Func<PrintDialog, bool>? PrintDialogPrompt { get; set; }
    /// <summary>Replaces sending to the printer in tests: receives the pages and the job name.</summary>
    internal Action<DocumentPaginator, string>? PrintSink { get; set; }

    private void PageSetupClicked(object sender, RoutedEventArgs e) => ShowPageSetup();

    internal bool ShowPageSetup()
    {
        var chosen = PageSetupPrompt is { } prompt ? prompt(PageSetup) : new PageSetupDialog(PageSetup) { Owner = this } is var dialog && dialog.ShowDialog() == true ? dialog.Result : null;
        if (chosen is null || chosen.Problem() is not null) return false;
        PageSetup = chosen;
        return true;
    }

    private void PrintClicked(object sender, RoutedEventArgs e) => PrintActiveQuery();

    /// <summary>Asks the printer and range, then prints. Returns false when there is no query tab or the user cancels.</summary>
    internal bool PrintActiveQuery()
    {
        if (ActiveQuery is not { } query) return false;
        string title = documents.TryGetValue(query, out var document) ? document.Title.TrimEnd('*') : "SQL";
        var dialog = new PrintDialog { UserPageRangeEnabled = true, SelectedPagesEnabled = query.SelectedSql is not null, CurrentPageEnabled = false };
        try
        {
            dialog.PrintTicket.PageOrientation = PageSetup.Landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            dialog.PrintTicket.PageMediaSize = PageSetup.Paper switch
            {
                "Letter" => new PageMediaSize(PageMediaSizeName.NorthAmericaLetter), "Legal" => new PageMediaSize(PageMediaSizeName.NorthAmericaLegal),
                "A3" => new PageMediaSize(PageMediaSizeName.ISOA3), "A5" => new PageMediaSize(PageMediaSizeName.ISOA5), _ => new PageMediaSize(PageMediaSizeName.ISOA4)
            };
        }
        catch (Exception ex) when (ex is PrintQueueException or InvalidOperationException) { } // No printer: the dialog says so.
        bool accepted = PrintDialogPrompt is { } prompt ? prompt(dialog) : dialog.ShowDialog() == true;
        if (!accepted) return false;
        bool selection = dialog.PageRangeSelection == PageRangeSelection.SelectedPages && query.SelectedSql is not null;
        string text = selection ? query.SelectedSql! : query.EditorText;
        var flow = SqlPrinting.Document(text, query.Editor.FontFamily, query.Editor.FontSize, PageSetup);
        var range = dialog.PageRangeSelection == PageRangeSelection.UserPages ? dialog.PageRange : (PageRange?)null;
        var pages = SqlPrinting.Pages(flow, title, PageSetup, range?.PageFrom, range?.PageTo);
        try
        {
            if (PrintSink is { } sink) sink(pages, title); else dialog.PrintDocument(pages, title);
            Status.Text = $"{title} sent to the printer ({pages.PageCount.ToString(CultureInfo.CurrentCulture)} page(s)).";
            return true;
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException) { ShowError(ex); return false; }
    }
}

/// <summary>File ▸ Page Setup: paper, orientation and margins in millimeters.</summary>
internal sealed class PageSetupDialog : Window
{
    private readonly ComboBox paper = new() { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly RadioButton portrait = new() { Content = "_Portrait", Margin = new Thickness(0, 0, 16, 0) }, landscape = new() { Content = "_Landscape" };
    private readonly TextBox left = Box("Left margin"), top = Box("Top margin"), right = Box("Right margin"), bottom = Box("Bottom margin");
    private readonly TextBlock error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    internal PrintPageSetup? Result { get; private set; }

    private static TextBox Box(string name) { var box = new TextBox { Width = 60, Margin = new Thickness(4, 0, 12, 0) }; System.Windows.Automation.AutomationProperties.SetName(box, name); return box; }
    private static string Mm(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    internal PageSetupDialog(PrintPageSetup setup)
    {
        Title = "Page Setup"; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var p in PrintPageSetup.Papers) paper.Items.Add(p.Name);
        paper.SelectedItem = setup.Paper; System.Windows.Automation.AutomationProperties.SetName(paper, "Paper size");
        (setup.Landscape ? landscape : portrait).IsChecked = true;
        left.Text = Mm(setup.LeftMm); top.Text = Mm(setup.TopMm); right.Text = Mm(setup.RightMm); bottom.Text = Mm(setup.BottomMm);
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        ok.Click += (_, _) => Accept();
        StackPanel Row(params UIElement[] items) { var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) }; foreach (var i in items) row.Children.Add(i); return row; }
        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                new TextBlock { Text = "Paper", FontWeight = FontWeights.SemiBold }, Row(new Label { Content = "_Size:", Target = paper, Padding = new Thickness(0, 0, 8, 0), Width = 50 }, paper),
                new TextBlock { Text = "Orientation", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) }, Row(portrait, landscape),
                new TextBlock { Text = "Margins (millimeters)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                Row(new TextBlock { Text = "Left:", Width = 50 }, left, new TextBlock { Text = "Right:", Width = 50 }, right),
                Row(new TextBlock { Text = "Top:", Width = 50 }, top, new TextBlock { Text = "Bottom:", Width = 50 }, bottom),
                error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } }
            }
        };
    }

    internal void Accept()
    {
        static double Read(TextBox box) => double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double v) ? v : double.NaN;
        var setup = new PrintPageSetup(paper.SelectedItem as string ?? "", landscape.IsChecked == true, Read(left), Read(top), Read(right), Read(bottom));
        if (setup.Problem() is { } problem) { error.Text = problem; return; }
        Result = setup;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
}
