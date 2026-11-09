using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>View Designer (PGMS-031, M1046): the four panes kept in step, Execute, Verify and Save.</summary>
internal static class ViewDesignerChecks
{
    internal static async Task RunAsync(MainWindow window, string connection, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Sql("""
            CREATE TABLE public.vd_customers (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE public.vd_orders (id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES public.vd_customers(id), placed date, total numeric(10,2));
            INSERT INTO public.vd_customers VALUES (1,'Ana'),(2,'Bruno');
            INSERT INTO public.vd_orders VALUES (10,1,'2026-01-05',20.5),(11,2,'2026-02-07',40),(12,2,'2026-03-01',5);
            CREATE VIEW public.vd_orders_view AS SELECT o.id, c.name, o.total FROM public.vd_orders o JOIN public.vd_customers c ON c.id = o.customer_id WHERE o.total > 10;
            CREATE VIEW public.vd_union AS SELECT id FROM public.vd_orders UNION SELECT id FROM public.vd_customers;
            """);

        var view = window.OpenViewDesigner(connection, "public", "vd_orders_view", addTableOnOpen: false);
        for (int i = 0; i < 100 && (view.IsBusy || view.Designer.Sources.Count == 0); i++) await Task.Delay(50);
        await Idle();
        var designer = view.Designer;
        string? notice = null;
        designer.NoticePrompt = text => notice = text;
        DesignerSourceBox B(string reference) => designer.Sources.Single(b => b.Reference == reference);
        check(designer.Sources.Select(b => b.Reference).SequenceEqual(["o", "c"]) && designer.IsRepresentable && !view.IsDirty && view.Title == "public.vd_orders_view", "Design shows the tables of the view in the Diagram pane and is not dirty");
        check(designer.Sql.StartsWith("SELECT      o.id, c.name, o.total", StringComparison.Ordinal) && designer.Sql.Contains("INNER JOIN", StringComparison.Ordinal) && designer.Sql.Contains("WHERE       (o.total > 10", StringComparison.Ordinal), "The SQL pane lays the query out as SSMS does: " + designer.Sql);
        check(designer.Rows.Select(r => r.Column).SequenceEqual(["id", "name", "total"]) && designer.Rows[0].Table == "o" && designer.Rows[2].Filter.StartsWith("> 10", StringComparison.Ordinal), "The Criteria pane has a row per column, with the filter");
        check(Enumerable.Range(0, 4).All(designer.IsPaneVisible), "The four panes start visible");
        var surface = designer.Surface;
        check(Texts(designer.BoxOf(B("o"))).Contains("* (All Columns)") && Texts(designer.BoxOf(B("o"))).Contains("customer_id"), "A table box lists (All Columns) and the columns");
        check(Descendants(designer.BoxOf(B("o"))).OfType<TextBlock>().Single(t => t.Text == "id").FontWeight == FontWeights.Bold, "The primary key is bold, as in SSMS");
        check(Checks(designer.BoxOf(B("o")), "o.id") && !Checks(designer.BoxOf(B("o")), "o.customer_id"), "Columns in the query are ticked");
        check(Descendants(designer.BoxOf(B("o"))).OfType<Path>().Count(x => x.ToolTip as string == "Filtered") == 1, "A filtered column shows the funnel");
        check(designer.Lines.Children.OfType<Polyline>().Any(p => p.Tag is DesignerJoinLine), "The join is drawn");
        check(designer.Lines.Children.OfType<Canvas>().Count() == 2, "A join on a primary key has key and ∞ ends");

        // Panes in step.
        designer.SetOutput(B("o"), "customer_id", true); await Idle();
        check(designer.Sql.Contains("o.customer_id", StringComparison.Ordinal) && designer.Rows.Any(r => r.Column == "customer_id") && view.IsDirty && view.Title.EndsWith('*'), "Ticking a column adds it to the Criteria and SQL panes");
        designer.SetOutput(B("o"), "customer_id", false); await Idle();
        check(designer.Rows.All(r => r.Column != "customer_id") && !designer.Sql.Contains("o.customer_id, ", StringComparison.Ordinal), "Clearing it takes the row away");
        designer.SetOutput(B("o"), "total", false); await Idle();
        check(designer.Rows.Single(r => r.Column == "total") is { Output: false, Filter.Length: > 0 } && designer.Sql.Contains("(o.total > 10", StringComparison.Ordinal) && !designer.Sql.Split('\n')[0].Contains("total", StringComparison.Ordinal), "Clearing a filtered column keeps the filter without output");
        designer.SetOutput(B("o"), "total", true); await Idle();

        var row = designer.Rows.Single(r => r.Column == "name");
        row.SortType = "Descending"; designer.ReadGrid();
        check(designer.Sql.Contains("ORDER BY c.name DESC", StringComparison.Ordinal) && row.SortOrder == "1", "Sort Type in the grid orders the query and numbers the sort");
        row.SortType = ""; row.SortOrder = ""; row.Alias = "customer"; designer.ReadGrid();
        check(designer.Sql.Contains("c.name AS customer", StringComparison.Ordinal) && !designer.Sql.Contains("ORDER BY", StringComparison.Ordinal), "An alias goes into the SQL");
        row.Alias = ""; row.Filter = "'Ana'"; designer.ReadGrid();
        check(designer.Sql.Contains("(c.name = 'Ana')", StringComparison.Ordinal), "A filter without an operator means equals");
        row.Or1 = "LIKE 'B%'"; designer.ReadGrid();
        check(designer.Sql.Contains(" OR ", StringComparison.Ordinal) && designer.Sql.Contains("(c.name LIKE 'B%')", StringComparison.Ordinal), "The Or column goes into the WHERE as an alternative");
        row.Filter = ""; row.Or1 = ""; designer.ReadGrid();
        designer.SetGrouped(true); await Idle();
        check(designer.Sql.Contains("GROUP BY o.id, c.name, o.total", StringComparison.Ordinal) && designer.Sql.Contains("HAVING      (o.total > 10", StringComparison.Ordinal) && !designer.Sql.Contains("WHERE", StringComparison.Ordinal) && designer.CriteriaGrid.Columns.Single(c => (string)c.Header == "Group By").Visibility == Visibility.Visible, "Add Group By groups every row, shows the column and moves the filter to HAVING, as in SSMS");
        designer.Rows.Single(r => r.Column == "total").GroupBy = "Sum"; designer.ReadGrid();
        check(designer.Sql.Contains("SUM(o.total)", StringComparison.Ordinal) && designer.Sql.Contains("GROUP BY o.id, c.name\n", StringComparison.Ordinal), "An aggregate in Group By changes the SQL");
        designer.SetGrouped(false); await Idle();
        check(!designer.Sql.Contains("GROUP BY", StringComparison.Ordinal), "Removing Group By takes it away");

        // Execute and Verify.
        await designer.ExecuteAsync(); await Idle();
        check(designer.ResultsGrid.Items.Count == 2 && designer.ResultStatus.StartsWith("2 row", StringComparison.Ordinal) && !designer.IsQueryChangedShown, "Execute SQL fills the Results pane");
        designer.SetOutput(B("o"), "placed", true); await Idle();
        check(designer.IsQueryChangedShown, "Changing the query after running it shows Query Changed");
        check(await designer.VerifyAsync() && notice == "The SQL syntax has been verified against the data source.", "Verify SQL Syntax says what SSMS says");
        designer.ClearResults();
        check(designer.ResultsGrid.ItemsSource is null && !designer.IsQueryChangedShown, "Clear Results empties the pane");
        designer.SetPane(3, false);
        check(!designer.IsPaneVisible(3), "A pane can be hidden"); designer.SetPane(3, true);

        // Menus.
        string[] Items(object? target) { var menu = new ContextMenu(); designer.FillMenu(menu, target); return menu.Items.OfType<MenuItem>().Select(m => m.Name).ToArray(); }
        check(Items(null).SequenceEqual(["DesignerExecute", "DesignerAddGroupBy", "DesignerAddTable", "DesignerPane", "DesignerClearResults"]), "Empty area menu follows SSMS, without derived tables and Properties");
        check(Items(new DesignerHit(B("o"), null)).SequenceEqual(["DesignerColumnNames", "DesignerNameOnly", "DesignerSelectAllColumns", "DesignerRemoveTable"]), "Table menu follows SSMS, without Properties");
        var line = new DesignerJoinLine("o", "c");
        var joinMenu = new ContextMenu(); designer.FillMenu(joinMenu, line);
        check(joinMenu.Items.OfType<MenuItem>().Select(m => (string)m.Header).SequenceEqual(["Remove", "Select All Rows from o", "Select All Rows from c"]), "Join menu follows SSMS, without Properties");
        designer.ToggleAllRows(line, true); await Idle();
        check(designer.Sql.Contains("LEFT OUTER JOIN", StringComparison.Ordinal), "Select All Rows from the left table makes a left join");
        designer.ToggleAllRows(line, false); await Idle();
        check(designer.Sql.Contains("FULL OUTER JOIN", StringComparison.Ordinal), "From both sides makes a full join");
        designer.ToggleAllRows(line, true); designer.ToggleAllRows(line, false); await Idle();
        check(designer.Sql.Contains("INNER JOIN", StringComparison.Ordinal), "Off on both sides is an inner join again");

        await designer.ExecuteAsync(); await Idle();
        if (Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") is { Length: > 0 } folder)
        {
            view.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var picture = new DrawingVisual(); using (var context = picture.RenderOpen()) { context.DrawRectangle(Brushes.White, null, new Rect(0, 0, view.ActualWidth, view.ActualHeight)); context.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, view.ActualWidth, view.ActualHeight)); }
            bitmap.Render(picture);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            System.IO.Directory.CreateDirectory(folder); using var file = System.IO.File.Create(System.IO.Path.Combine(folder, "view-designer.png")); encoder.Save(file);
        }

        // SQL pane back to the others.
        await designer.ApplySqlAsync("SELECT o.id FROM public.vd_orders AS o"); await Idle();
        check(designer.Sources.Single().Reference == "o" && designer.Rows.Single().Column == "id" && designer.Design.Joins.Count == 0, "A valid edit of the SQL pane changes the Diagram and Criteria panes");
        string good = "SELECT o.id, c.name FROM public.vd_orders AS o INNER JOIN public.vd_customers AS c ON c.id = o.customer_id";
        await designer.ApplySqlAsync(good); await Idle();
        check(designer.Sources.Count == 2 && B("c").X > B("o").X, "Tables keep their places or are laid out side by side");
        designer.Sql = good + " zz"; string? problem = null;
        designer.DifferPrompt = p => { problem = p; return false; };
        await designer.ApplySqlAsync(designer.Sql); await Idle();
        check(problem is not null && designer.Sql == good && designer.IsRepresentable, "Undo in Query Definitions Differ puts the last good SQL back");
        string union = "SELECT id FROM public.vd_orders UNION SELECT id FROM public.vd_customers";
        designer.Sql = union; designer.DifferPrompt = _ => true;
        await designer.ApplySqlAsync(union); await Idle();
        check(!designer.IsRepresentable && designer.Sql == union, "Ignore keeps SQL the panes cannot show");
        await designer.ExecuteAsync(); await Idle();
        check(designer.ResultStatus.StartsWith("5 row", StringComparison.Ordinal), "SQL the panes cannot show still runs");
        designer.SetOutput(B("o"), "placed", true);
        check(designer.Sql == union, "While the query cannot be represented, the panes do not write over it");
        designer.DifferPrompt = _ => throw new InvalidOperationException("not expected");
        await designer.ApplySqlAsync(good); await Idle();
        check(designer.IsRepresentable, "Valid SQL brings the panes back");

        // Saving an existing view.
        window.ViewDesignerClosePrompt = _ => MessageBoxResult.No;
        TableDesignChange? reviewed = null; view.ReviewPrompt = c => { reviewed = c; return false; };
        check(!await view.SaveAsync() && reviewed!.Sql.StartsWith("CREATE OR REPLACE VIEW \"public\".\"vd_orders_view\" AS", StringComparison.Ordinal) && view.IsDirty, "Save shows CREATE OR REPLACE and can be declined");
        view.ReviewPrompt = _ => true;
        await designer.ApplySqlAsync("SELECT o.id FROM public.vd_orders AS o");
        check(!await view.SaveAsync() && view.FeedbackText.Contains("cannot remove", StringComparison.Ordinal) && await Scalar("SELECT count(*) FROM information_schema.columns WHERE table_name='vd_orders_view'") is 3L, "Removing a column is refused with the reason and nothing is saved");
        await designer.ApplySqlAsync("SELECT o.id, c.name, o.total, o.placed FROM public.vd_orders AS o INNER JOIN public.vd_customers AS c ON c.id = o.customer_id WHERE (o.total > 10)");
        await Sql("CREATE OR REPLACE VIEW public.vd_orders_view AS SELECT o.id, c.name, o.total FROM public.vd_orders o JOIN public.vd_customers c ON c.id = o.customer_id WHERE o.total > 11");
        check(!await view.SaveAsync() && view.FeedbackText.Contains("changed since", StringComparison.Ordinal), "A view changed elsewhere is not overwritten");
        await Sql("CREATE OR REPLACE VIEW public.vd_orders_view AS SELECT o.id, c.name, o.total FROM public.vd_orders o JOIN public.vd_customers c ON c.id = o.customer_id WHERE o.total > 10");
        check(await view.SaveAsync() && !view.IsDirty && await Scalar("SELECT count(*) FROM information_schema.columns WHERE table_name='vd_orders_view'") is 4L, "Adding a column at the end is saved: " + view.FeedbackText);
        check(window.OpenViewDesigner(connection, "public", "vd_orders_view", addTableOnOpen: false) == view && window.ViewDesigners.Count == 1, "Opening an open view activates its tab");
        check(await window.CloseViewDesignerAsync(view) && window.ViewDesigners.Count == 0, "A saved view closes without asking");

        // A view the panes cannot show.
        var unionView = window.OpenViewDesigner(connection, "public", "vd_union", addTableOnOpen: false);
        for (int i = 0; i < 100 && unionView.IsBusy; i++) await Task.Delay(50);
        await Idle();
        check(!unionView.Designer.IsRepresentable && unionView.Designer.Sql.Contains("UNION", StringComparison.Ordinal) && !unionView.IsDirty, "A view with UNION opens in the SQL pane only, with no question");
        await window.CloseViewDesignerAsync(unionView);

        var dmlView = window.OpenViewDesigner(connection, "public", null, addTableOnOpen: false);
        for (int i = 0; i < 100 && dmlView.IsBusy; i++) await Task.Delay(50);
        await Idle();
        dmlView.Designer.DifferPrompt = _ => true;
        await dmlView.Designer.ApplySqlAsync("DELETE FROM public.vd_orders");
        check(!dmlView.Designer.IsRepresentable, "The View Designer keeps a DELETE out of its panes");
        await dmlView.Designer.ApplySqlAsync(""); dmlView.Designer.Sql = "";
        window.ViewDesignerClosePrompt = _ => MessageBoxResult.No;
        await window.CloseViewDesignerAsync(dmlView);

        // New View.
        var created = window.OpenViewDesigner(connection, "public", null, addTableOnOpen: false);
        for (int i = 0; i < 100 && created.IsBusy; i++) await Task.Delay(50);
        await Idle();
        var fresh = created.Designer;
        check(created.Title == "public.View_1" && created.IsNew && !created.IsDirty && fresh.IsRepresentable, "New View starts empty with the name View_1");
        var relations = await new QueryDesignService(connection).ListRelationsAsync();
        check(relations.Any(r => r.Name == "vd_customers") && relations.Any(r => r.Name == "vd_orders_view"), "Add Table lists tables and views");
        await fresh.AddSourcesAsync(relations.Where(r => r.Name is "vd_customers" or "vd_orders").ToArray()); await Idle();
        check(fresh.Design.Joins.Single() is { Left: "vd_customers", LeftColumn: "id", Right: "vd_orders", RightColumn: "customer_id", Kind: DesignJoinKind.Inner } && fresh.Sql.Contains("public.vd_customers INNER JOIN", StringComparison.Ordinal) && created.IsDirty, "Adding two tables joins them by their foreign key, as in SSMS");
        await fresh.AddSourcesAsync(relations.Where(r => r.Name == "vd_customers").ToArray()); await Idle();
        check(fresh.Sources[^1].Reference == "vd_customers_1" && fresh.Sql.Contains("AS vd_customers_1", StringComparison.Ordinal), "A table added twice gets an alias");
        fresh.RemoveSource(fresh.Sources[^1]); await Idle();
        check(fresh.Sources.Count == 2 && !fresh.Sql.Contains("vd_customers_1", StringComparison.Ordinal), "Remove takes the table out");
        fresh.SelectAllColumns(fresh.Sources[0]); await Idle();
        check(fresh.Rows.Count == 2 && fresh.Sql.Contains("vd_customers.name", StringComparison.Ordinal), "Select All Columns ticks every column");
        var orders = fresh.Sources[1];
        fresh.RemoveJoin(new("vd_customers", "vd_orders")); await Idle();
        check(fresh.Sql.Contains("CROSS JOIN", StringComparison.Ordinal), "Without a join the tables are crossed");
        fresh.AddJoin(fresh.Sources[0], "id", orders, "customer_id"); await Idle();
        check(fresh.Sql.Contains("INNER JOIN", StringComparison.Ordinal), "Dragging a column to another table joins them");
        fresh.MoveBox(orders, new Point(500, 120)); await Idle();
        check(Canvas.GetLeft(fresh.BoxOf(orders)) == 500 && orders.Y == 120, "A table moves in the Diagram pane");
        created.ReviewPrompt = _ => true;
        created.NamePromptOverride = suggested => suggested == "View_1" ? null : "x";
        check(!await created.SaveAsync() && created.IsNew, "Cancel in Choose Name saves nothing");
        created.NamePromptOverride = _ => "vd_orders_view";
        check(!await created.SaveAsync() && created.FeedbackText.Contains("42P07", StringComparison.Ordinal) && created.IsNew, "A new view never replaces another: " + created.FeedbackText);
        created.NamePromptOverride = _ => "vd_new";
        check(await created.SaveAsync() && !created.IsNew && created.Title == "public.vd_new" && await Scalar("SELECT count(*) FROM public.vd_new") is 3L, "Save creates the view: " + created.FeedbackText);
        fresh.SetOutput(orders, "total", true); await Idle();
        created.ReviewPrompt = c => { reviewed = c; return true; };
        check(await created.SaveAsync() && reviewed!.Sql.StartsWith("CREATE OR REPLACE VIEW", StringComparison.Ordinal), "After the first save it is an existing view");
        await window.CloseViewDesignerAsync(created);

        await Sql("DROP VIEW public.vd_new, public.vd_orders_view, public.vd_union; DROP TABLE public.vd_orders, public.vd_customers");
    }

    private static bool Checks(Border box, string name) => Descendants(box).OfType<CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Output " + name).IsChecked == true;
    private static string[] Texts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToArray();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
