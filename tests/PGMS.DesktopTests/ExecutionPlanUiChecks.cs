using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ExecutionPlanUiChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        CheckLayout(check);
        var query = new QueryView(); var window = new Window { Content = query, Width = 1100, Height = 760, ShowInTaskbar = false }; window.Show();
        try
        {
            await query.ConnectAsync(connection); var editor = (TextEditor)query.FindName("Editor");
            var diagram = (ExecutionPlanView)query.FindName("PlanDiagram"); var tabs = (TabControl)query.FindName("OutputTabs"); var status = (TextBlock)query.FindName("PlanStatus"); var properties = (DataGrid)query.FindName("PlanProperties");
            check(((TabItem)query.FindName("PlanTab")).Header as string == "Execution plan", "The tab has the SSMS name");
            editor.Text = "CREATE TEMP TABLE orders_ui(id int primary key, c text); CREATE TEMP TABLE items_ui(id int, o int, q int); INSERT INTO orders_ui SELECT g, 'c'||(g%9) FROM generate_series(1,500) g; INSERT INTO items_ui SELECT g, (g%500)+1, g%7 FROM generate_series(1,3000) g; ANALYZE orders_ui; ANALYZE items_ui;";
            await query.ExecuteAsync();

            query.IncludeActualPlan = true;
            editor.Text = "SELECT o.c, sum(i.q) AS total FROM orders_ui o JOIN items_ui i ON i.o = o.id WHERE o.id < 400 GROUP BY o.c ORDER BY total DESC LIMIT 5;\nSELECT count(*) FROM items_ui;";
            tabs.SelectedIndex = 0;
            await query.ExecuteAsync();
            await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(diagram.Statements.Count == 2 && status.Text.Contains("Actual plan"), "Execute with Include Actual Execution Plan fills the plan: " + status.Text);
            check(tabs.SelectedIndex == 0 && ((TabControl)query.FindName("ResultSets")).Items.Count == 2, "Results stay in front and keep their rows");
            check(diagram.Statements[0].Header.StartsWith("Query 1: Query cost (relative to the batch): ", StringComparison.Ordinal) && diagram.Statements[0].QueryText.StartsWith("SELECT o.c", StringComparison.Ordinal), "Each statement has its header and text");
            check(diagram.SelectedOperator?.IsStatement == true && properties.Items.Count > 0, "The first statement box starts selected and fills Properties");
            check(diagram.OperatorCount > 5, "The diagram draws every operator of every statement, drew " + diagram.OperatorCount);

            ExecutionPlanOperator? TryFind(ExecutionPlanOperator op, Func<ExecutionPlanOperator, bool> test) { if (test(op)) return op; foreach (var c in op.Children) if (TryFind(c, test) is { } f) return f; return null; }
            var join = TryFind(diagram.Statements[0].Root, o => o.Title.EndsWith("Join", StringComparison.Ordinal) || o.Title == "Nested Loop") ?? throw new Exception("no join in plan");
            diagram.Select(join);
            await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(ReferenceEquals(properties.ItemsSource, join.Properties), "Selecting an operator shows its server properties");
            check(join.Subtitle is not null && join.Lines.Any(l => l.StartsWith("Cost: ", StringComparison.Ordinal)) && join.Lines.Any(l => l.EndsWith(" of", StringComparison.Ordinal)), "Join box shows kind, cost and rows: " + string.Join("|", join.Lines));
            var card = (StackPanel)ExecutionPlanView.Card(join);
            check(card.Children.Count > 5 && card.Children[0] is TextBlock { Text: var title } && title == join.Title, "The operator card lists its summary");

            diagram.Zoom = 1;
            var menu = diagram.ContextMenu;
            string[] names = menu.Items.OfType<MenuItem>().Select(m => m.Name).ToArray();
            check(names.SequenceEqual(["PlanSaveAs", "PlanShowInNewTab", "PlanFindNode", "PlanZoomIn", "PlanZoomOut", "PlanCustomZoom", "PlanZoomFit"]), "Context menu offers save, new tab, find and zoom");
            menu.Items.OfType<MenuItem>().Single(m => m.Name == "PlanZoomIn").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(Math.Abs(diagram.Zoom - 1.25) < 0.001, "Zoom In enlarges by one step");
            menu.Items.OfType<MenuItem>().Single(m => m.Name == "PlanZoomOut").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(Math.Abs(diagram.Zoom - 1) < 0.001, "Zoom Out returns");
            diagram.Zoom = 100; check(diagram.Zoom == 4, "Zoom has an upper limit"); diagram.Zoom = 1;
            int? zoomAsked = null;
            diagram.CustomZoomPrompt = current => { zoomAsked = current; return 150; };
            menu.Items.OfType<MenuItem>().Single(m => m.Name == "PlanCustomZoom").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(zoomAsked == 100 && Math.Abs(diagram.Zoom - 1.5) < 0.001, "Custom Zoom starts from the current percentage and applies the chosen one");
            diagram.CustomZoomPrompt = _ => null;
            menu.Items.OfType<MenuItem>().Single(m => m.Name == "PlanCustomZoom").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(Math.Abs(diagram.Zoom - 1.5) < 0.001, "Cancelling Custom Zoom keeps the zoom"); diagram.Zoom = 1;
            check(ExecutionPlanView.ParseZoom("10") == 10 && ExecutionPlanView.ParseZoom("400") == 400 && ExecutionPlanView.ParseZoom("9") is null && ExecutionPlanView.ParseZoom("401") is null && ExecutionPlanView.ParseZoom("1,5") is null && ExecutionPlanView.ParseZoom("") is null, "Custom Zoom accepts whole numbers from 10 to 400");

            tabs.SelectedItem = query.FindName("PlanTab"); await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // Find Node
            menu.Items.OfType<MenuItem>().Single(m => m.Name == "PlanFindNode").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await query.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(diagram.IsFindOpen, "Find Node opens its bar");
            var findBar = FindAll<Border>(diagram).First(b => b.Child is StackPanel { Orientation: Orientation.Horizontal } p && p.Children.OfType<ComboBox>().Count() == 2);
            var findPanel = (StackPanel)findBar.Child; var findCombos = findPanel.Children.OfType<ComboBox>().ToArray(); var findText = findPanel.Children.OfType<TextBox>().Single();
            check((string)findCombos[0].SelectedItem == "Node Type" && findCombos[0].Items.Contains("Plan Rows"), "The list holds the properties of the plan and starts on Node Type");
            diagram.Select(diagram.Statements[0].Root); findText.Text = "scan";
            check(diagram.FindNode() && diagram.SelectedOperator?.Properties["Node Type"].Contains("Scan", StringComparison.Ordinal) == true, "Find Node goes to an operator that contains the text");
            var firstFound = diagram.SelectedOperator; var scans = ExecutionPlanSearch.Operators(diagram.Statements).Where(o => ExecutionPlanSearch.Matches(o, "Node Type", ExecutionPlanComparison.Contains, "scan")).ToList();
            check(diagram.FindNode() && !ReferenceEquals(diagram.SelectedOperator, firstFound) && diagram.FindNode(false) && ReferenceEquals(diagram.SelectedOperator, firstFound) && scans.Count > 1, "Next and previous walk through the matches");
            findText.Text = "no such node";
            check(!diagram.FindNode() && ReferenceEquals(diagram.SelectedOperator, firstFound) && findPanel.Children.OfType<TextBlock>().Single().Text.Contains("No matching", StringComparison.Ordinal), "With no match the selection stays and the bar says so");
            findBar.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(findBar)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            check(!diagram.IsFindOpen, "Esc closes the bar");

            // Arrow tooltip
            var withRows = ExecutionPlanView.ArrowRows(join.Children[0]).ToDictionary(p => p.Key, p => p.Value);
            check(withRows.ContainsKey("Actual Number of Rows") && withRows.ContainsKey("Estimated Number of Rows") && withRows["Estimated Row Size"].EndsWith(" B", StringComparison.Ordinal) && withRows.ContainsKey("Estimated Data Size"), "The arrow tooltip lists the rows it carries");
            check(FindAll<System.Windows.Shapes.Polyline>(diagram).Count(line => line.ToolTip is ToolTip && line.IsHitTestVisible && line.StrokeThickness >= 10) == diagram.OperatorCount - diagram.Statements.Count, "Every arrow can show a tooltip");

            string directory = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "plan-ui", Guid.NewGuid().ToString("N"))); System.IO.Directory.CreateDirectory(directory);
            string destination = System.IO.Path.Combine(directory, "second.json");
            diagram.Select(diagram.Statements[1].Root);
            await query.ExportPlanAsync(destination);
            using (var saved = System.Text.Json.JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(destination)))
                check(saved.RootElement[0].GetProperty("Query Text").GetString()!.Contains("count(*)", StringComparison.Ordinal), "Export follows the statement of the selected operator");

            tabs.SelectedItem = query.FindName("PlanTab");
            await ResultExportUiChecks.Capture(window, query, "m1045-plan-actual", 1400, 900, check);

            // Saved plan file
            var savedPlan = await QueryPlanFiles.LoadAsync(destination);
            var reopened = ExecutionPlanBuilder.Build([savedPlan]);
            check(savedPlan.Analyzed && reopened.Count == 1 && reopened[0].QueryText.Contains("count(*)", StringComparison.Ordinal) && reopened[0].Root.Children[0].ActualRows is not null, "A saved plan reads back with its text and actual rows");
            async Task<bool> Refused(string content) { string bad = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"); await System.IO.File.WriteAllTextAsync(bad, content); try { await QueryPlanFiles.LoadAsync(bad); return false; } catch (System.IO.InvalidDataException) { return true; } }
            check(await Refused("not json") && await Refused("{}") && await Refused("[]") && await Refused("[{\"Plan\":{}}]") && await Refused("[{\"Plan\":1}]"), "Files that are not a plan are refused");

            query.IncludeActualPlan = false;
            editor.Text = "SELECT count(*) FROM orders_ui";
            await query.ExplainPlanAsync();
            check(diagram.Statements.Count == 1 && diagram.Statements[0].Root.Children[0].ActualRows is null && !diagram.Statements[0].Root.Children[0].Lines.Any(l => l.EndsWith(" of", StringComparison.Ordinal)), "Estimated plan has no invented actual rows");
            check(diagram.Statements[0].QueryText == editor.Text, "Estimated plan shows the SQL it explained");
        }
        finally { query.Cancel(); await query.DisposeAsync(); window.Close(); }

        var shell = new MainWindow { Width = 1100, Height = 700, ShowInTaskbar = false }; shell.Show();
        try
        {
            System.Windows.Input.ApplicationCommands.New.Execute(null, shell);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var button = (ToggleButton)shell.FindName("ActualPlanButton");
            check(button.IsEnabled && button.IsChecked == false, "Include Actual Execution Plan starts off in a new tab");
            shell.ToggleActualPlan();
            check(button.IsChecked == true, "The button shows the state of the tab");
            System.Windows.Input.ApplicationCommands.New.Execute(null, shell);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(button.IsChecked == false, "The option belongs to each tab");

            // Plan documents
            shell.ShowErrorOverride = _ => { };
            string planFolder = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "plan-ui", Guid.NewGuid().ToString("N"))); System.IO.Directory.CreateDirectory(planFolder);
            string planFile = System.IO.Path.Combine(planFolder, "saved.json"), badFile = System.IO.Path.Combine(planFolder, "bad.json");
            await System.IO.File.WriteAllTextAsync(planFile, "[{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"t\",\"Startup Cost\":0.0,\"Total Cost\":1.5,\"Plan Rows\":10,\"Plan Width\":4}}]");
            await System.IO.File.WriteAllTextAsync(badFile, "{}");
            check(await shell.OpenPlanFileAsync(badFile) is null && shell.PlanDocuments.Count == 0, "A bad plan file opens no tab");
            var saved = await shell.OpenPlanFileAsync(planFile);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(saved is not null && shell.PlanDocuments.Count == 1 && saved.Diagram.Statements.Count == 1 && saved.Diagram.SelectedOperator?.IsStatement == true && saved.Diagram.Statements[0].Root.Children[0].Title == "Seq Scan", "File ▸ Open Execution Plan shows the saved plan in a tab");
            var fromQuery = shell.QueryDocuments.First();
            var queryDiagram = (ExecutionPlanView)fromQuery.FindName("PlanDiagram");
            queryDiagram.Show(saved!.Diagram.Statements);
            queryDiagram.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Name == "PlanShowInNewTab").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(shell.PlanDocuments.Count == 2 && shell.PlanDocuments[1].Diagram.Statements[0].Root.Children[0].Title == "Seq Scan", "Show Execution Plan In New Tab opens the plan in its own tab");
            string resaved = System.IO.Path.Combine(planFolder, "again.json");
            shell.PlanDocuments[1].PickSaveFile = () => resaved;
            await shell.PlanDocuments[1].SaveSelectedAsync();
            check(System.IO.File.Exists(resaved) && (await QueryPlanFiles.LoadAsync(resaved)).Json.Contains("Seq Scan", StringComparison.Ordinal), "A plan tab can be saved");
        }
        finally { shell.Close(); }
    }

    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        void Visit(DependencyObject node)
        {
            if (node is T match) found.Add(match);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Visit(child);
        }
        Visit(root);
        return found;
    }

    private static void CheckLayout(Action<bool, string> check)
    {
        ExecutionPlanOperator Op(string name, params ExecutionPlanOperator[] children) => new() { Title = name, Properties = new Dictionary<string, string>(), Children = children };
        ExecutionPlanOperator a = Op("a"), b = Op("b"), c = Op("c", a, b), d = Op("d"), e = Op("e", d), root = Op("root", c, e);
        Size Measure(ExecutionPlanOperator op) => new(op == root ? 60 : 120, op == c ? 110 : 80);
        var places = ExecutionPlanLayout.Place(root, Measure);
        var rects = places.Values.ToList();
        check(rects.SelectMany((r, i) => rects.Skip(i + 1).Select(o => r.IntersectsWith(o))).All(hit => !hit), "Operators never overlap");
        double Center(ExecutionPlanOperator op) => places[op].Top + places[op].Height / 2;
        check(Math.Abs(Center(root) - Center(c)) < 0.01 && Math.Abs(Center(c) - Center(a)) < 0.01, "The first input sits on its parent's row");
        check(places[root].Right < places[c].Left && places[c].Right < places[a].Left, "Inputs are placed to the right of their parent");
        check(places[b].Top > places[a].Top && places[e].Top > places[b].Top, "Later inputs go below the earlier ones");
        check(ExecutionPlanLayout.ArrowThickness(0) == 2 && ExecutionPlanLayout.ArrowThickness(5957) > ExecutionPlanLayout.ArrowThickness(10) && ExecutionPlanLayout.ArrowThickness(1e30) == 12, "Arrows grow with rows, within limits");
    }
}
