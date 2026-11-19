using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-084 (M1119): Show Execution Plan JSON, Show Execution Plan as Text and Compare Showplan from the plan context menu.</summary>
internal static class PlanSourceUiChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        string folder = Path.Combine(Path.GetTempPath(), "pgms-plan-compare-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var query = shell.NewQueryTab("SELECT g, g % 7 AS r FROM generate_series(1, 500) g WHERE g > 10 ORDER BY r");
            await query.ConnectAsync(connection);
            await query.ExplainPlanAsync();
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var diagram = (ExecutionPlanView)query.FindName("PlanDiagram");
            check(diagram.Statements.Count == 1, "The estimated plan is on screen");

            diagram.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            var items = diagram.ContextMenu.Items.OfType<MenuItem>().ToDictionary(i => i.Name);
            check(items["PlanShowJson"].Visibility == Visibility.Visible && items["PlanShowText"].Visibility == Visibility.Visible && items["PlanCompare"].Visibility == Visibility.Visible
                && (string)items["PlanShowJson"].Header == "Show Execution Plan _JSON" && (string)items["PlanShowText"].Header == "Show Execution Plan as Te_xt" && (string)items["PlanCompare"].Header == "Com_pare Showplan", "In a query tab the menu offers JSON, Text and Compare Showplan");

            int before = shell.OpenQueryCount();
            items["PlanShowJson"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var json = shell.LastPlanSource;
            check(json is { } j && j.Title.StartsWith("ExecutionPlan") && j.Title.EndsWith(".json") && j.View.Editor.Text.TrimStart().StartsWith("[") && j.View.Editor.Text.Contains("\"Node Type\"") && !j.View.HasSession && !j.View.IsDirty && j.View.Editor.SyntaxHighlighting is null && shell.OpenQueryCount() == before + 1,
                "Show Execution Plan JSON opens the indented JSON in a new tab, not connected and not changed");

            await shell.RunPlanCommandAsync(diagram, PlanCommand.ShowText, "q");
            var text = shell.LastPlanSource!.Value;
            check(text.Title.EndsWith(".txt") && text.View.Editor.Text.Contains("(cost=") && text.View.Editor.Text.Contains("->  ") && text.View.Editor.Text.Contains("Sort Key: "), "Show Execution Plan as Text opens the EXPLAIN text layout:\n" + text.View.Editor.Text);

            // Compare with a saved plan of another query.
            var other = shell.NewQueryTab("SELECT g FROM generate_series(1, 500) g WHERE g > 10");
            await other.ConnectAsync(connection); await other.ExplainPlanAsync();
            string file = Path.Combine(folder, "other.json");
            await other.ExportPlanAsync(file);
            int asked = 0;
            shell.PickComparePlanFile = () => { asked++; return null; };
            await shell.RunPlanCommandAsync(diagram, PlanCommand.Compare, "SQLQuery1.sql");
            check(asked == 1 && shell.PlanComparisons.Count == 0, "Cancelling the file choice opens nothing");
            shell.PickComparePlanFile = () => file;
            await shell.RunPlanCommandAsync(diagram, PlanCommand.Compare, "SQLQuery1.sql");
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var comparison = shell.PlanComparisons.LastOrDefault();
            check(comparison is not null && comparison.First.Statements.Count == 1 && comparison.Second.Statements.Count == 1, "Compare Showplan opens both plans side by side");
            check(comparison!.Differences.Any(d => d.Difference == QueryPlanComparison.OnlyFirst && d.Item.Contains("Sort")) && comparison.Differences.Any(d => d.Area == "Statement" && d.Difference.Length > 0), "The differences list the operator only in plan 1 and the changed totals");
            int all = comparison.DifferenceGrid.Items.Count;
            comparison.OnlyDifferencesBox.IsChecked = true; comparison.Filter();
            check(comparison.DifferenceGrid.Items.Count < all && comparison.DifferenceGrid.Items.OfType<PlanDifference>().All(d => d.Difference.Length > 0), "Show only differences hides the rows that agree");
            var sortRow = comparison.Differences.First(d => d.Item.Contains("Sort"));
            comparison.DifferenceGrid.SelectedItem = sortRow;
            check(comparison.First.SelectedOperator?.Title.Contains("Sort") == true, "Selecting an operator row selects it in its plan: " + comparison.First.SelectedOperator?.Title);
            check(((TextBlock)((DockPanel)((Grid)((Grid)comparison.Content).Children[0]).Children[0]).Children[0]).Text == "Plan 1: SQLQuery1.sql", "Each plan is named by where it came from");
            // B1 review (M1140): the boxes of the operators that differ are shaded in the drawing, as SSMS shades regions.
            var marked1 = comparison.First.MarkedOperators; var marked2 = comparison.Second.MarkedOperators;
            check(marked1.Any(m => m.Operator.Title.Contains("Sort") && m.Note == "Only in plan 1") && !marked2.Any(m => m.Note.StartsWith("Only in plan 1", StringComparison.Ordinal)),
                "The operator only in plan 1 is shaded in plan 1: " + string.Join(", ", marked1.Select(m => m.Operator.Title + "=" + m.Note)));
            int changedRows = comparison.Differences.Count(d => d.Area == "Operator" && d.Difference == QueryPlanComparison.Changed);
            check(marked1.Count(m => m.Note == "Different values in plan 2") == changedRows && marked2.Count(m => m.Note == "Different values in plan 1") == changedRows
                && marked1.Count + marked2.Count == comparison.Differences.Count(d => d.Area == "Operator" && d.Difference.Length > 0) + changedRows,
                $"Operators with other values are shaded in both plans, and operators that agree are not ({changedRows} changed)");
            check(!marked1.Any(m => m.Operator.IsStatement), "The statement box is never shaded");

            // Compare with Previous Plan: the plan the same tab showed before, without a file.
            diagram.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            var previousItem = diagram.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Name == "PlanComparePrevious");
            check(previousItem.Visibility == Visibility.Visible && !previousItem.IsEnabled && diagram.PreviousStatements is null, "Compare with Previous Plan is listed and gray while the tab has shown one plan only");
            query.Editor.Text = "SELECT g, g % 7 AS r FROM generate_series(1, 500) g WHERE g > 400 ORDER BY r LIMIT 5";
            await query.ExplainPlanAsync();
            query.IsDirty = false; // the text was changed only to get another plan; closing must not ask to save it
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            diagram.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            check(previousItem.IsEnabled && diagram.PreviousStatements is { Count: 1 }, "After a second plan in the tab, Compare with Previous Plan is enabled");
            int opened = shell.PlanComparisons.Count;
            await shell.RunPlanCommandAsync(diagram, PlanCommand.ComparePrevious, "SQLQuery1.sql");
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var withPrevious = shell.PlanComparisons.Count == opened + 1 ? shell.PlanComparisons[^1] : null;
            check(withPrevious is not null && withPrevious.Differences.Any(d => d.Item.Contains("Limit") && d.Difference == QueryPlanComparison.OnlySecond)
                && withPrevious.Second.MarkedOperators.Any(m => m.Operator.Title.Contains("Limit")),
                "Compare with Previous Plan puts the previous plan first and the current second, with the new Limit shaded in plan 2");

            File.WriteAllText(Path.Combine(folder, "bad.json"), "{}");
            shell.PickComparePlanFile = () => Path.Combine(folder, "bad.json");
            int count = shell.PlanComparisons.Count;
            await shell.RunPlanCommandAsync(diagram, PlanCommand.Compare, "q");
            check(shell.PlanComparisons.Count == count && ((TextBlock)shell.FindName("Status")).Text.StartsWith("The plan file could not be opened"), "A file that is not a plan is reported");

            var planTab = shell.OpenPlanDocument(diagram.Statements, "Execution plan");
            planTab.Diagram.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            check(planTab.Diagram.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Name == "PlanShowJson").Visibility == Visibility.Visible, "A plan in a tab of its own offers the same commands");
            var lonely = new ExecutionPlanView(); lonely.Show(diagram.Statements); lonely.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            check(lonely.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Name == "PlanCompare").Visibility == Visibility.Collapsed, "Without a host the items are hidden");
        }
        finally { shell.Close(); await shell.CloseCompletion; try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
