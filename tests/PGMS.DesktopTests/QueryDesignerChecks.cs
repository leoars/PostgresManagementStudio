using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>Query Designer (PGMS-032, M1047): Design Query in Editor, the dialog panes, Change Type and what OK leaves in the editor.</summary>
internal static class QueryDesignerChecks
{
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    internal static async Task RunAsync(MainWindow window, string connection, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        await Sql("""
            CREATE TABLE public.qdg_customers (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE public.qdg_orders (id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES public.qdg_customers(id), total numeric(10,2), status text);
            INSERT INTO public.qdg_customers VALUES (1,'Ana'),(2,'Bruno');
            INSERT INTO public.qdg_orders VALUES (10,1,20,'open'),(11,2,40,'paid');
            """);
        string script = "SELECT o.id, c.name\nFROM public.qdg_orders AS o INNER JOIN public.qdg_customers AS c ON c.id = o.customer_id\nWHERE o.total > 5\n\nSELECT 1 AS um\n";
        var query = await window.OpenQueryTextAsync(connection, script);
        for (int i = 0; i < 100 && !query.IsConnected; i++) await Task.Delay(50);
        await Idle();
        var editor = (ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor");
        void Select(int start, int length) { editor.Select(start, length); editor.CaretOffset = start + length; }
        string first = script[..script.IndexOf("\n\n", StringComparison.Ordinal)];
        var menu = (MenuItem)window.FindName("DesignQueryMenu");
        check(menu.InputGestureText == "Ctrl+Shift+Q" && menu.Header as string == "_Design Query in Editor…", "Query has Design Query in Editor with its shortcut");
        check(editor.ContextMenu!.Items.OfType<MenuItem>().Any(m => m.Command == MainWindow.DesignQueryInEditor), "The editor's context menu has Design Query in Editor");

        string? refused = null;
        window.QueryDesignerRefused = text => refused = text;
        QueryDesignerDialog? opened = null;
        Func<QueryDesignerDialog, Task<bool>> Run(Func<QueryDesignerDialog, Task<bool>> drive) => async dialog =>
        {
            opened = dialog; dialog.AddTableOnOpen = false; dialog.DialogHost = false;
            dialog.Designer.NoticePrompt = _ => { };
            dialog.Show();
            await dialog.Ready; await Idle();
            try { return await drive(dialog); } finally { dialog.Close(); }
        };

        // Selection of two statements: the message of SSMS, nothing opens.
        window.QueryDesignerHost = Run(_ => Task.FromResult(false));
        Select(0, script.TrimEnd().Length);
        await window.OpenQueryDesignerAsync();
        check(refused?.StartsWith("The text that you have selected cannot be represented graphically using the Query Designer.", StringComparison.Ordinal) == true && opened is null, "Two statements selected are refused with the message of SSMS and no dialog opens");
        refused = null;
        Select(script.IndexOf("WHERE", StringComparison.Ordinal), "WHERE o.total > 5".Length);
        await window.OpenQueryDesignerAsync();
        check(refused is not null && opened is null, "A fragment that is not a statement is refused");
        refused = null;

        // Selection: opens filled, Cancel changes nothing, OK replaces only the selection.
        Select(0, first.Length);
        window.QueryDesignerHost = Run(dialog =>
        {
            var d = dialog.Designer;
            check(d.Sources.Select(b => b.Reference).SequenceEqual(["o", "c"]) && d.IsRepresentable, "A selected statement opens in the panes");
            check(dialog.Title == "Query Designer" && d.IsPaneVisible(0) && d.IsPaneVisible(1) && d.IsPaneVisible(2) && !d.IsPaneVisible(3), "The dialog has Diagram, Criteria and SQL, without Results");
            check(d.Rows.Single(r => r.Column == "total") is { Output: false, Filter: "> 5" }, "A filtered column not in the list shows without output");
            return Task.FromResult(false);
        });
        await window.OpenQueryDesignerAsync();
        check(editor.Text == script.Replace("\n", Environment.NewLine, StringComparison.Ordinal) || editor.Text == script, "Cancel leaves the editor as it was");

        Select(0, first.Length);
        window.QueryDesignerHost = Run(async dialog =>
        {
            var d = dialog.Designer;
            d.SetOutput(d.Sources[0], "status", true); await Idle();
            return await dialog.AcceptAsync();
        });
        await window.OpenQueryDesignerAsync(); await Idle();
        check(editor.Text.Contains("o.status", StringComparison.Ordinal) && editor.Text.Contains("SELECT 1 AS um", StringComparison.Ordinal) && editor.SelectionLength == 0, "OK replaces only the selection and leaves the rest of the script");
        check(editor.Text[editor.CaretOffset..].TrimStart().StartsWith("SELECT 1 AS um", StringComparison.Ordinal) && editor.Text[..editor.CaretOffset].TrimEnd().EndsWith("5)", StringComparison.Ordinal), "The caret ends after the inserted statement, as in SSMS");
        check(await Scalar("SELECT count(*) FROM public.qdg_orders") is 2L && query.IsBusy is false, "OK runs nothing");

        // A semicolon that ended the selection stays.
        editor.Text = "SELECT id FROM public.qdg_orders; SELECT 2;";
        Select(0, "SELECT id FROM public.qdg_orders;".Length);
        window.QueryDesignerHost = Run(dialog => dialog.AcceptAsync());
        await window.OpenQueryDesignerAsync(); await Idle();
        check(editor.Text.Contains("qdg_orders; SELECT 2;", StringComparison.Ordinal), "A selection that ended in a semicolon keeps it: " + editor.Text);

        // UNION opens in the SQL pane only and comes back as written.
        string union = "SELECT id FROM public.qdg_orders UNION SELECT id FROM public.qdg_customers";
        editor.Text = union; Select(0, union.Length);
        window.QueryDesignerHost = Run(async dialog =>
        {
            check(!dialog.Designer.IsRepresentable && !dialog.Designer.IsPaneVisible(0) && !dialog.Designer.IsPaneVisible(1) && dialog.Designer.IsPaneVisible(2) && dialog.Designer.Sql == union, "A query the panes cannot read opens in the SQL pane only");
            return await dialog.AcceptAsync();
        });
        await window.OpenQueryDesignerAsync(); await Idle();
        check(editor.Text == union, "OK on it leaves the text as written");

        // No selection: the statement under the caret comes in, and OK puts the new one in its place (user's request).
        await Sql("""
            CREATE SCHEMA qdg_mixed;
            CREATE TABLE qdg_mixed."Origens" ("CodigoOrigem" integer PRIMARY KEY, "Nome" text);
            CREATE TABLE qdg_mixed."Usuarios" ("CodigoUsuario" integer PRIMARY KEY, "Nome" text, "CodigoOrigem" integer REFERENCES qdg_mixed."Origens"("CodigoOrigem"));
            """);
        string designed = "SELECT      \"Usuarios\".\"CodigoUsuario\", \"Origens\".\"Nome\" AS \"Origem\"\nFROM        qdg_mixed.\"Usuarios\" INNER JOIN\n            qdg_mixed.\"Origens\" ON \"Usuarios\".\"CodigoOrigem\" = \"Origens\".\"CodigoOrigem\"";
        editor.Text = designed; editor.Select(0, 0); editor.CaretOffset = designed.IndexOf("INNER", StringComparison.Ordinal);
        window.QueryDesignerHost = Run(async dialog =>
        {
            var d = dialog.Designer;
            check(d.IsRepresentable && d.Sources.Select(b => b.Reference).SequenceEqual(["Usuarios", "Origens"]) && d.Design.Joins.Count == 1 && d.Rows.Single(r => r.Column == "Nome").Alias == "Origem", "With the caret in the statement the designer imports it, quoted names and alias included: " + d.Sql);
            check(d.Sources[0].X != d.Sources[1].X || d.Sources[0].Y != d.Sources[1].Y, "A statement read into the designer opens with its tables apart");
            d.SetOutput(d.Sources[0], "Nome", true); await Idle();
            return await dialog.AcceptAsync();
        });
        await window.OpenQueryDesignerAsync(); await Idle();
        check(editor.Text.Split("SELECT").Length == 2 && editor.Text.Contains("\"Usuarios\".\"Nome\"", StringComparison.Ordinal), "OK replaces the statement instead of adding a copy: " + editor.Text);

        string several = "SELECT id FROM public.qdg_orders\n\nSELECT id, name FROM public.qdg_customers\n\n";
        editor.Text = several; editor.Select(0, 0); editor.CaretOffset = several.IndexOf("name", StringComparison.Ordinal);
        window.QueryDesignerHost = Run(async dialog =>
        {
            check(dialog.Designer.Sources.Single().Reference == "qdg_customers", "With several statements it takes the one under the caret");
            return await dialog.AcceptAsync();
        });
        await window.OpenQueryDesignerAsync(); await Idle();
        check(editor.Text.StartsWith("SELECT id FROM public.qdg_orders", StringComparison.Ordinal) && editor.Text.Contains("qdg_customers.name", StringComparison.Ordinal) && editor.Text.Split("SELECT").Length == 3, "OK replaces only that statement: " + editor.Text);

        editor.Text = "CREATE TABLE x (a int)"; editor.Select(0, 0); editor.CaretOffset = 3;
        opened = null; window.QueryDesignerHost = Run(_ => Task.FromResult(false));
        await window.OpenQueryDesignerAsync();
        check(refused?.StartsWith("The statement at the cursor", StringComparison.Ordinal) == true && opened is null, "A statement the designer does not write is refused and nothing opens");
        refused = null;

        string withBlank = "SELECT id FROM public.qdg_orders\n\n";
        editor.Text = withBlank; editor.Select(0, 0); editor.CaretOffset = withBlank.Length;
        window.QueryDesignerHost = Run(dialog => { check(dialog.Designer.Sources.Count == 0, "On an empty line after a statement the designer starts empty"); return Task.FromResult(false); });
        await window.OpenQueryDesignerAsync(); await Idle();
        await Sql("DROP SCHEMA qdg_mixed CASCADE");

        var designButton = (Button)query.FindName("DesignQueryButton");
        check(designButton.Command == MainWindow.DesignQueryInEditor && designButton.IsEnabled, "The query tab has a button for the designer");

        // No selection: empty, inserts at the caret.
        editor.Text = "-- start\n"; editor.Select(editor.Text.Length, 0); editor.CaretOffset = editor.Text.Length;
        var relations = await new QueryDesignService(connection).ListRelationsAsync();
        var tables = relations.Where(r => r.Name is "qdg_customers" or "qdg_orders").ToArray();
        window.QueryDesignerHost = Run(async dialog =>
        {
            var d = dialog.Designer;
            check(d.Sources.Count == 0 && d.Sql.Split('\n')[0].TrimEnd() == "SELECT", "With no selection the designer starts empty");
            check(!await dialog.AcceptAsync() is false && dialog.ResultSql.Length == 0, "OK with no table leaves nothing to insert");
            await d.AddSourcesAsync(tables); await Idle();
            check(d.Design.Joins.Count == 1 && d.Sql.Contains("INNER JOIN", StringComparison.Ordinal), "Add Table joins by the foreign key");
            d.SetOutput(d.Sources[0], "name", true); await Idle();
            return await dialog.AcceptAsync();
        });
        await window.OpenQueryDesignerAsync(); await Idle();
        check(editor.Text.StartsWith("-- start", StringComparison.Ordinal) && editor.Text.Contains("qdg_customers.name", StringComparison.Ordinal) && editor.CaretOffset == editor.Text.Length, "OK inserts at the caret");

        // Add Related Tables.
        await Sql("""
            CREATE TABLE public.qdg_items (id integer PRIMARY KEY, order_id integer REFERENCES public.qdg_orders(id));
            DO $$ BEGIN FOR i IN 1..12 LOOP EXECUTE format('CREATE TABLE public.qdg_fan_%s (id integer PRIMARY KEY, order_id integer REFERENCES public.qdg_orders(id))', i); END LOOP; END $$;
            """);
        editor.Text = ""; editor.CaretOffset = 0;
        window.QueryDesignerHost = Run(async dialog =>
        {
            var d = dialog.Designer;
            await d.AddSourcesAsync(tables.Where(r => r.Name == "qdg_orders").ToArray()); await Idle();
            var menu2 = new ContextMenu(); d.FillMenu(menu2, new DesignerHit(d.Sources[0], null));
            check(menu2.Items.OfType<MenuItem>().Select(i => i.Name).SequenceEqual(["DesignerColumnNames", "DesignerNameOnly", "DesignerSelectAllColumns", "DesignerAddRelated", "DesignerRemoveTable"]) && menu2.Items.OfType<MenuItem>().Single(i => i.Name == "DesignerAddRelated").Header as string == "Add Related Tables", "The table menu has Add Related Tables");
            await d.AddRelatedTablesAsync(d.Sources[0]); await Idle();
            check(d.Sources.Count == 15 && d.Sources.Any(b => b.Reference == "qdg_customers") && d.Sources.Any(b => b.Reference == "qdg_items"), "Add Related Tables brings tables on both sides of the foreign keys");
            check(d.Design.Joins.Count == 14 && d.Sql.Contains("qdg_items.order_id", StringComparison.Ordinal), "Each one comes joined by its foreign key: " + d.Sql);
            d.UpdateLayout();
            var rects = d.Sources.Select(b => new Rect(b.X, b.Y, d.BoxOf(b).DesiredSize.Width, d.BoxOf(b).DesiredSize.Height)).ToArray();
            check(rects.All(r => r.Right <= d.Surface.Width && r.Bottom <= d.Surface.Height), "Many related tables stay inside the Diagram pane, on new rows");
            check(!rects.SelectMany((a, i) => rects.Skip(i + 1).Select(b => a.IntersectsWith(b))).Any(x => x), "They do not overlap");
            d.ArrangeTables(); await Idle();
            rects = d.Sources.Select(b => new Rect(b.X, b.Y, d.BoxOf(b).DesiredSize.Width, d.BoxOf(b).DesiredSize.Height)).ToArray();
            check(rects.All(r => r.Left >= 20 && r.Top >= 20 && r.Right <= d.Surface.Width && r.Bottom <= d.Surface.Height) && !rects.SelectMany((a, i) => rects.Skip(i + 1).Select(b => a.IntersectsWith(b))).Any(x => x), "Arrange Tables lays them out apart, inside the pane");
            check(!d.Sql.Contains("Arrange", StringComparison.Ordinal) && d.Sources.Count == 15, "Arrange Tables changes the layout only");
            d.MoveBox(d.Sources[0], new Point(3000, 2000)); await Idle();
            check(d.Surface.Width >= 3000 + d.BoxOf(d.Sources[0]).DesiredSize.Width && d.Surface.Height >= 2000 + d.BoxOf(d.Sources[0]).DesiredSize.Height, "The pane grows so a table is never cut off");
            d.ArrangeTables(); await Idle();
            await d.UndoAsync(); await Idle();
            check(d.Sources.Count == 1, "One Undo takes them all out");
            await d.RedoAsync(); await Idle();
            await d.AddRelatedTablesAsync(d.Sources[0]); await Idle();
            check(d.Sources.Count == 15 && dialog.FeedbackText.Contains("no related tables", StringComparison.Ordinal), "With nothing left to add it says so and adds nothing");
            return false;
        });
        await window.OpenQueryDesignerAsync(); await Idle();
        await Sql("DO $$ BEGIN FOR i IN 1..12 LOOP EXECUTE format('DROP TABLE public.qdg_fan_%s', i); END LOOP; END $$; DROP TABLE public.qdg_items");

        // Selecting a table and removing it with Delete.
        editor.Text = ""; editor.CaretOffset = 0;
        window.QueryDesignerHost = Run(async dialog =>
        {
            var d = dialog.Designer;
            await d.AddSourcesAsync(tables); await Idle();
            d.SetOutput(d.Sources[0], "name", true); await Idle();
            var customers = d.Sources.Single(b => b.Reference == "qdg_customers");
            var title = Descendants(d.BoxOf(customers)).OfType<Border>().First(b => b.Tag is DesignerHit { Column: null });
            void Mouse(UIElement on, RoutedEvent kind) => on.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = kind });
            void Delete() => d.Surface.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(d.Surface)!, 0, System.Windows.Input.Key.Delete) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Delete(); await Idle();
            check(d.Sources.Count == 2, "Delete with no table selected removes nothing");
            Mouse(title, UIElement.MouseDownEvent); Mouse(d.Surface, UIElement.MouseUpEvent); await Idle();
            check(d.SelectedBox == customers && d.BoxOf(customers).BorderThickness.Left == 2 && d.BoxOf(d.Sources.Single(b => b != customers)).BorderThickness.Left == 1, "A click selects the table and marks it");
            Mouse(d.Surface, UIElement.MouseDownEvent); Mouse(d.Surface, UIElement.MouseUpEvent); await Idle();
            check(d.SelectedBox is null && d.BoxOf(customers).BorderThickness.Left == 1, "A click on the empty area clears the selection");
            Mouse(title, UIElement.MouseDownEvent); Mouse(d.Surface, UIElement.MouseUpEvent); await Idle();
            Delete(); await Idle();
            check(d.Sources.Single().Reference == "qdg_orders" && d.Design.Joins.Count == 0 && d.Rows.All(r => r.Table != "qdg_customers") && !d.Sql.Contains("qdg_customers", StringComparison.Ordinal) && d.SelectedBox is null, "Delete takes the selected table out of the query, with its joins and rows: " + d.Sql);
            check(await Scalar("SELECT count(*) FROM public.qdg_customers") is 2L, "The table in the database stays");
            await d.UndoAsync(); await Idle();
            check(d.Sources.Count == 2 && d.Design.Joins.Count == 1, "Undo brings the table back");
            return false;
        });
        await window.OpenQueryDesignerAsync(); await Idle();

        // Change Type.
        editor.Text = ""; editor.CaretOffset = 0;
        window.QueryDesignerHost = Run(async dialog =>
        {
            var d = dialog.Designer;
            string? notice = null; d.NoticePrompt = text => notice = text;
            await d.AddSourcesAsync(tables); await Idle();
            await d.ChangeTypeAsync(DesignStatement.Update); await Idle();
            check(notice?.Contains("one table", StringComparison.Ordinal) == true && d.Statement == DesignStatement.Select, "Update with two tables is refused with the reason");
            d.RemoveSource(d.Sources[0]); await Idle();
            var orders = d.Sources.Single();
            d.SetOutput(orders, "total", true); d.SetOutput(orders, "status", true); await Idle();

            void Picture(string name)
            {
                var content = (FrameworkElement)dialog.Content;
                dialog.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var picture = new System.Windows.Media.DrawingVisual();
                using (var context = picture.RenderOpen()) { context.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); context.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); }
                bitmap.Render(picture);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "acceptance", "M1047-query-designer"));
                System.IO.Directory.CreateDirectory(folder);
                using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name));
                encoder.Save(file);
            }
            Picture("pgms-select.png");

            await d.ChangeTypeAsync(DesignStatement.Update); await Idle();
            Picture("pgms-update.png");
            check(d.Sql.StartsWith("UPDATE      public.qdg_orders\nSET         total =, status =", StringComparison.Ordinal), "Update writes SET with the marked columns, as in SSMS: " + d.Sql);
            check(d.IsColumnShown("Set") && d.IsColumnShown("New Value") && d.IsColumnShown("Table") && d.IsColumnShown("Filter") && !d.IsColumnShown("Alias") && !d.IsColumnShown("Sort Type") && !d.IsColumnShown("Output"), "Update shows Set and New Value and hides the columns of SELECT");
            var total = d.Rows.Single(r => r.Column == "total"); total.NewValue = "1"; total.Filter = "> 0"; d.ReadGrid();
            d.Rows.Single(r => r.Column == "status").NewValue = "'x'"; d.ReadGrid();
            check(d.Sql.Contains("SET         total = 1, status = 'x'", StringComparison.Ordinal) && d.Sql.Contains("WHERE       (total > 0)", StringComparison.Ordinal), "New Value and Filter go into the statement: " + d.Sql);
            string update = d.Sql;
            await d.ApplySqlAsync(update); await Idle();
            check(d.Statement == DesignStatement.Update && d.Rows.Single(r => r.Column == "status").NewValue == "'x'", "The UPDATE reads back into the panes");
            string? verified = null; d.NoticePrompt = text => verified = text;
            check(await d.VerifyAsync() && verified!.Contains("verified", StringComparison.Ordinal) && await Scalar("SELECT status FROM public.qdg_orders WHERE id=10") is "open", "Verify checks an UPDATE against the server and changes nothing");

            await d.ChangeTypeAsync(DesignStatement.Delete); await Idle();
            check(d.Sql == "DELETE FROM public.qdg_orders\nWHERE       (total > 0)", "Delete keeps the filter: " + d.Sql);
            check(d.IsColumnShown("Filter") && !d.IsColumnShown("Set") && !d.IsColumnShown("New Value") && d.Rows.Single() is { Column: "total" }, "Delete shows Column, Table and the filters only");
            check(Boxes(d).OfType<CheckBox>().Count() == 0, "Delete has no check boxes in the Diagram pane");

            await d.UndoAsync(); await Idle();
            check(d.Statement == DesignStatement.Update && d.Sql == update && d.CanRedo, "Undo returns to the previous statement");
            await d.RedoAsync(); await Idle();
            check(d.Statement == DesignStatement.Delete, "Redo applies it again");
            await d.UndoAsync(); await Idle();

            await d.ChangeTypeAsync(DesignStatement.InsertValues); await Idle();
            check(d.Sql.StartsWith("INSERT INTO public.qdg_orders\n            (total, status)\nVALUES      (1, 'x')", StringComparison.Ordinal), "Insert Values writes the column list and the values: " + d.Sql);
            check(d.IsColumnShown("New Value") && !d.IsColumnShown("Table") && !d.IsColumnShown("Filter"), "Insert Values shows Column and New Value");

            await d.ChangeTypeAsync(DesignStatement.Select); await Idle();
            d.TargetPrompt = _ => null;
            await d.ChangeTypeAsync(DesignStatement.InsertResults); await Idle();
            check(d.Statement == DesignStatement.Select, "Cancel in Choose Target leaves the statement as it was");
            d.TargetPrompt = list => list.First(r => r.Name == "qdg_customers");
            await d.ChangeTypeAsync(DesignStatement.InsertResults); await Idle();
            check(d.Sql.StartsWith("INSERT INTO public.qdg_customers\nSELECT      ", StringComparison.Ordinal) && d.IsColumnShown("Append"), "Insert Results writes into the chosen target and shows Append: " + d.Sql);
            d.Rows[0].Append = "id"; d.Rows[1].Append = "name"; d.ReadGrid();
            check(d.Sql.StartsWith("INSERT INTO public.qdg_customers\n            (id, name)\nSELECT", StringComparison.Ordinal), "Append becomes the target column list: " + d.Sql);

            d.MakeTablePrompt = () => null;
            await d.ChangeTypeAsync(DesignStatement.MakeTable); await Idle();
            check(d.Statement == DesignStatement.InsertResults, "Cancel in Make Table leaves the statement as it was");
            d.MakeTablePrompt = () => "public.qdg_made";
            await d.ChangeTypeAsync(DesignStatement.MakeTable); await Idle();
            check(d.Sql.Contains("\nINTO        public.qdg_made\nFROM", StringComparison.Ordinal) && !d.IsColumnShown("Append"), "Make Table writes SELECT INTO: " + d.Sql);
            check(await Scalar("SELECT to_regclass('public.qdg_made')::text") is DBNull, "Make Table creates nothing by itself");

            var (popup, grid, sql) = (new ContextMenu(), new ContextMenu(), new ContextMenu());
            d.FillMenu(popup, null); d.FillGridMenu(grid); d.FillSqlMenu(sql);
            string[] Names(ContextMenu m) => m.Items.OfType<MenuItem>().Select(i => i.Name).ToArray();
            check(Names(popup).SequenceEqual(["DesignerExecute", "DesignerAddGroupBy", "DesignerChangeType", "DesignerRedo", "DesignerUndo", "DesignerAddTable", "DesignerArrange", "DesignerPane"]) && !popup.Items.OfType<MenuItem>().First().IsEnabled, "The Diagram pane menu of the dialog follows SSMS, with Execute SQL disabled");
            check(Names(sql).SequenceEqual(["DesignerExecute", "DesignerVerify", "DesignerChangeType", "DesignerRedo", "DesignerUndo", "DesignerCut", "DesignerCopy", "DesignerPaste", "DesignerDelete", "DesignerSelectAll", "DesignerPane"]), "The SQL pane menu follows SSMS");
            check(Names(grid).SequenceEqual(["DesignerExecute", "DesignerChangeType", "DesignerRedo", "DesignerUndo", "DesignerPane"]), "The Criteria pane menu follows SSMS, without Cut, Copy and Paste");
            check(popup.Items.OfType<MenuItem>().Single(i => i.Name == "DesignerChangeType").Items.OfType<MenuItem>().Select(i => i.Header as string).SequenceEqual(["Select", "Insert Results…", "Insert Values", "Update", "Delete", "Make Table…"]), "Change Type has the six statements of SSMS");

            // Query Definitions Differ, Undo: OK keeps the dialog open.
            d.DifferPrompt = _ => false;
            string kept = d.Sql;
            d.Sql = "SELECT id FROM public.qdg_orders UNION SELECT 1";
            check(!await dialog.AcceptAsync() && d.Sql == kept, "Refusing the SQL in Query Definitions Differ keeps the dialog open");
            return false;
        });
        await window.OpenQueryDesignerAsync(); await Idle();

        // A statement chosen in the editor reopens in its mode.
        string text = "UPDATE public.qdg_orders SET status = 'x' WHERE id = 1";
        editor.Text = text; Select(0, text.Length);
        window.QueryDesignerHost = Run(dialog =>
        {
            check(dialog.Designer.Statement == DesignStatement.Update && dialog.Designer.Rows.Count == 2 && dialog.Designer.Rows[0] is { Column: "status", NewValue: "'x'", Output: true } && dialog.Designer.Rows[1] is { Column: "id", Output: false, Filter: "= 1" }, "A selected UPDATE opens with Set, New Value and Filter");
            return Task.FromResult(false);
        });
        await window.OpenQueryDesignerAsync(); await Idle();
        check(opened!.Designer.Sql.StartsWith("UPDATE", StringComparison.Ordinal), "The UPDATE stays an UPDATE");

        window.QueryDesignerHost = null; window.QueryDesignerRefused = null;
        query.IsDirty = false;
        check(await window.CloseQueryAsync(query), "The query tab closes after the designer");
        await Sql("DROP TABLE public.qdg_orders, public.qdg_customers");
    }

    private static IEnumerable<DependencyObject> Boxes(QueryDesignerControl d)
    {
        foreach (var box in d.Sources)
            foreach (var child in Descendants(d.BoxOf(box))) yield return child;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
