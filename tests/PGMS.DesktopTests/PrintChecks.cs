using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-074 (M1096): File ▸ Page Setup… and File ▸ Print… for the active query. Nothing is sent to a printer: the pages go to a test sink.</summary>
internal static class PrintChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var setup = new PrintPageSetup();
        check(setup.Paper == "A4" && !setup.Landscape && setup.LeftMm == 25.4 && Math.Abs(setup.PageSize.Width - 210 * 96 / 25.4) < 0.01 && setup.Problem() is null, "Page Setup starts on A4 portrait with 25.4 mm margins, as SSMS showed");
        var turned = setup with { Landscape = true };
        check(turned.PageSize.Width > turned.PageSize.Height, "Landscape turns the page");
        check(setup with { LeftMm = -1 } is { } bad && bad.Problem() is not null && (setup with { LeftMm = 95, RightMm = 95 }).Problem() is not null && (setup with { Paper = "B9" }).Problem() is not null,
            "Negative or too large margins and an unknown paper are refused");

        // The document: every line, the editor font, wrapped inside the margins, with a header on each page.
        string text = string.Join("\r\n", Enumerable.Range(1, 200).Select(i => $"SELECT {i} AS linha;"));
        var flow = SqlPrinting.Document(text, new FontFamily("Consolas"), 14, setup);
        var all = new TextRange(flow.ContentStart, flow.ContentEnd).Text;
        check(all.Contains("SELECT 1 AS linha;", StringComparison.Ordinal) && all.Contains("SELECT 200 AS linha;", StringComparison.Ordinal) && flow.FontFamily.Source == "Consolas", "The printed document holds every line in the editor's font");
        var pages = SqlPrinting.Pages(flow, "SQLQuery1.sql", setup);
        int total = pages.PageCount;
        check(total >= 3, "200 lines take several A4 pages: " + total);
        var only = SqlPrinting.Pages(SqlPrinting.Document(text, new FontFamily("Consolas"), 14, setup), "SQLQuery1.sql", setup, 2, 3);
        check(only.PageCount == 2, "A page range prints only those pages");
        var firstPage = pages.GetPage(0);
        check(firstPage.Size.Width > 790 && firstPage.Size.Width < 796 && firstPage.Visual is ContainerVisual { Children.Count: 2 }, "Each page is A4 and carries the header over the text");

        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            var fileMenu = (MenuItem)window.FindName("FileMenu");
            var items = fileMenu.Items.OfType<MenuItem>().Select(m => m.Name).ToList();
            check(items.IndexOf("PageSetupMenu") > items.IndexOf("SaveAllMenu") && items.IndexOf("PrintMenu") == items.IndexOf("PageSetupMenu") + 1 && items.IndexOf("PrintMenu") < items.IndexOf("RecentFilesMenu"),
                "File ▸ Page Setup… and Print… come after Save All and before Recent Files, as in SSMS");
            check(!window.PrintActiveQuery(), "Without a query tab Print does nothing");

            window.PageSetupPrompt = current => current with { Paper = "Letter", Landscape = true, LeftMm = 10 };
            check(window.ShowPageSetup() && window.PageSetup is { Paper: "Letter", Landscape: true, LeftMm: 10 }, "Page Setup keeps the chosen paper, orientation and margins for the session");
            window.PageSetupPrompt = _ => null;
            check(!window.ShowPageSetup() && window.PageSetup.Paper == "Letter", "Cancel keeps the previous setup");

            var query = window.NewQueryTab("SELECT 1;\r\nSELECT 2;\r\nSELECT 3;"); await Idle();
            DocumentPaginator? printed = null; string? job = null;
            window.PrintSink = (p, name) => { printed = p; job = name; };
            PrintDialog? shown = null;
            window.PrintDialogPrompt = d => { shown = d; return false; };
            check(!window.PrintActiveQuery() && printed is null && shown is { UserPageRangeEnabled: true, SelectedPagesEnabled: false }, "Cancel in Print sends nothing; without a selection the Selection range is off");
            window.PrintDialogPrompt = _ => true;
            check(window.PrintActiveQuery() && printed is { PageCount: 1 } && job?.StartsWith("SQLQuery", StringComparison.Ordinal) == true, "Print sends the query's pages under the tab name: " + job);
            check(Math.Abs(printed!.GetPage(0).Size.Width - 279.4 * 96 / 25.4) < 0.5, "The pages follow Page Setup (Letter, landscape)");

            query.Editor.Select(query.Editor.Text.IndexOf("SELECT 2", StringComparison.Ordinal), "SELECT 2;".Length);
            window.PrintDialogPrompt = d => { shown = d; d.PageRangeSelection = PageRangeSelection.SelectedPages; return true; };
            printed = null;
            check(window.PrintActiveQuery() && shown!.SelectedPagesEnabled && printed?.Source is FlowDocument selected && new TextRange(selected.ContentStart, selected.ContentEnd).Text.Trim() == "SELECT 2;", "With a selection, Selection prints only the selected SQL");
            query.IsDirty = false;
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
        }
    }
}
