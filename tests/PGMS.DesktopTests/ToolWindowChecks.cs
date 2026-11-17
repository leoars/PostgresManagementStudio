using System.Windows.Controls;
using System.Windows.Threading;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>PGMS-079 (M1104): View ▸ Error List and View ▸ Output, without a server (failed runs are noted by the test).</summary>
internal static class ToolWindowChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            var errorMenu = (MenuItem)window.FindName("ErrorListMenu"); var outputMenu = (MenuItem)window.FindName("OutputMenu");
            var view = (ItemsControl)errorMenu.Parent;
            check(errorMenu.InputGestureText == "Ctrl+\\, E" && outputMenu.InputGestureText == "Ctrl+Alt+O" && view.Items.IndexOf(errorMenu) == view.Items.IndexOf(window.FindName("TemplateExplorerMenu")) + 1 && view.Items.IndexOf(outputMenu) == view.Items.IndexOf(errorMenu) + 1,
                "View ▸ Error List and Output come after Template Explorer, with the SSMS shortcuts");

            var first = window.NewQueryTab("select * from missing_table"); await Idle();
            var second = window.NewQueryTab("select 1"); await Idle();
            window.ShowToolWindow("errorlist"); await Idle();
            var anchorables = window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().ToList();
            check(anchorables.Any(a => a.ContentId == "errorlist" && a.Title == "Error List" && a.IsVisible) && window.ErrorListRows.Count == 0 && window.ErrorListCountText == "0 Errors", "Error List opens docked and empty");
            first.ReportIssueForTests("select * from missing_table", 15, 0);
            first.NoteExecutionForTests(new Npgsql.PostgresException("relation \"missing_table\" does not exist", "ERROR", "ERROR", "42P01"));
            second.NoteExecutionForTests(new InvalidOperationException("The connection was lost."));
            for (int i = 0; i < 20 && window.ErrorListRows.Count < 2; i++) { await Task.Delay(25); await Idle(); }
            var rows = window.ErrorListRows;
            check(rows.Count == 2 && window.ErrorListCountText == "2 Errors", "Each tab whose last run failed has a row: " + rows.Count);
            var located = rows.Single(r => ReferenceEquals(r.Query, first));
            check(located is { Code: "42P01", Description: "relation \"missing_table\" does not exist", Line: 1, Column: 15 } && located.File.StartsWith("SQLQuery", StringComparison.Ordinal),
                "A server error has its SQLSTATE, message, tab and the position it reported: " + located);
            check(rows.Single(r => ReferenceEquals(r.Query, second)) is { Code: "", Line: null }, "An error without a position has no line");
            var firstEditor = (TextEditor)first.FindName("Editor");
            window.GoToError(located); await Idle();
            check(firstEditor.CaretOffset == 14, "Going to the row puts the caret on the error: " + firstEditor.CaretOffset);
            first.NoteExecutionForTests(null);
            for (int i = 0; i < 20 && window.ErrorListRows.Count != 1; i++) { await Task.Delay(25); await Idle(); }
            check(window.ErrorListRows.Count == 1 && window.ErrorListCountText == "1 Error", "A successful run removes the tab's row");

            window.ShowToolWindow("output"); await Idle();
            check(window.DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Any(a => a.ContentId == "output" && a.IsVisible), "Output opens docked");
            check(window.OutputLines.Any(l => l.Contains(": Failed in 5 ms. 42P01: relation \"missing_table\" does not exist", StringComparison.Ordinal)) && window.OutputLines.Any(l => l.Contains(": Succeeded in 5 ms, 1 row.", StringComparison.Ordinal)),
                "Output logs each execution with its outcome: " + string.Join(" | ", window.OutputLines.TakeLast(3)));
            window.WriteOutput("hello\nworld");
            check(window.OutputLines[^1].EndsWith("  hello world", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(window.OutputLines[^1], @"^\d\d:\d\d:\d\d  ") && window.OutputBox.Text.EndsWith("hello world", StringComparison.Ordinal),
                "A message is one line with the time, also in the pane");
            int before = window.OutputLines.Count;
            ((TextBlock)window.FindName("Status")).Text = "Status line for Output";
            check(window.OutputLines.Count == before + 1 && window.OutputLines[^1].EndsWith("Status line for Output", StringComparison.Ordinal), "Status messages of the window go to Output");
            for (int i = 0; i < 5100; i++) window.WriteOutput("line " + i);
            check(window.OutputLines.Count == 5000 && window.OutputLines[^1].EndsWith("line 5099", StringComparison.Ordinal), "Output keeps the last 5,000 lines");
            window.ClearOutput();
            check(window.OutputLines.Count == 0 && window.OutputBox.Text == "", "Clear All empties Output");
        }
        finally
        {
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
        }
    }
}
