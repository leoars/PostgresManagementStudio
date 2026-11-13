using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>Indexes in the diagram tab (PGMS-044, M1061): the Indexes section, Show Indexes, the warning of foreign keys without an index, Refresh,
/// and the indexes kept by diagram. Runs in the throwaway database of <see cref="DatabaseDiagramChecks"/>.</summary>
internal static class DiagramIndexUiChecks
{
    internal static async Task RunAsync(MainWindow window, string connection, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Sql("""
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE TABLE public.ixui_parent (id integer PRIMARY KEY, email text NOT NULL CONSTRAINT ixui_parent_email_key UNIQUE, name text NOT NULL, bio text);
            CREATE INDEX ixui_parent_name_trgm ON public.ixui_parent USING gin (name gin_trgm_ops);
            CREATE TABLE public.ixui_child (id integer PRIMARY KEY, parent_id integer CONSTRAINT ixui_child_parent_fk REFERENCES public.ixui_parent(id), note text);
            CREATE TABLE public.ixui_ok (id integer PRIMARY KEY, parent_id integer CONSTRAINT ixui_ok_parent_fk REFERENCES public.ixui_parent(id));
            CREATE INDEX ixui_ok_parent ON public.ixui_ok (parent_id);
            """);
        window.DiagramConfirmPrompt = _ => true;
        var view = (await window.NewDiagramAsync(connection, showAddTable: false))!;
        await Idle();
        view.ConfirmPrompt = _ => true;
        await view.AddTablesAsync((await view.CandidateTablesAsync()).Where(c => c.Name.StartsWith("ixui_", StringComparison.Ordinal)).ToArray());
        await Idle(); view.UpdateLayout();
        DiagramTableModel T(string name) => view.Tables.Single(t => t.Name == name);
        FrameworkElement? Named(DiagramTableModel t, string name) => Descendants(view.BoxOf(t)).OfType<FrameworkElement>().FirstOrDefault(e => AutomationProperties.GetName(e) == name && e.IsVisible);
        IEnumerable<FrameworkElement> NamedStart(DiagramTableModel t, string start) => Descendants(view.BoxOf(t)).OfType<FrameworkElement>().Where(e => AutomationProperties.GetName(e).StartsWith(start, StringComparison.Ordinal) && e.IsVisible);
        Point Center(FrameworkElement e) => e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), view.DiagramSurface);
        void Click(FrameworkElement e, int clicks = 1) { var at = Center(e); view.Press(at, clicks); view.Release(at); }
        MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(i => i.Name == name);
        bool Has(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Any(i => i.Name == name);
        string Text(TextBlock block) => string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));

        var parent = T("ixui_parent"); var child = T("ixui_child"); var ok = T("ixui_ok");
        check(parent.Catalog.Indexes.Select(i => i.Name).SequenceEqual(["ixui_parent_pkey", "ixui_parent_email_key", "ixui_parent_name_trgm"]) && child.Catalog.UnindexedForeignKeys.Single().Name == "ixui_child_parent_fk" && ok.Catalog.UnindexedForeignKeys.Count == 0,
            "Adding tables reads their indexes and the foreign keys without one");
        var header = Named(parent, "Indexes, 3, collapsed");
        check(header is not null && view.IndexesShown && !parent.IndexesExpanded && Descendants(header!).OfType<TextBlock>().Any(b => Text(b) == "▸ Indexes (3) · GIN"), "Each table shows a closed Indexes section with the count and the methods other than b-tree");
        check(header!.ToolTip is string headerTip && headerTip.Contains("ixui_parent_name_trgm  GIN (name gin_trgm_ops)") && headerTip.Contains("ixui_parent_email_key  (email)  UNIQUE"), "The closed section lists every index in its tooltip");
        check(NamedStart(parent, "Index ").Count() == 0, "Closed, the section shows no index rows");

        // Closed, the section does not widen the table: the layout keeps its spacing.
        double widthWith = view.BoxOf(parent).ActualWidth, heightWith = view.BoxOf(parent).ActualHeight;
        view.ToggleIndexes(); await Idle(); view.UpdateLayout();
        check(!view.IndexesShown && Named(parent, "Indexes, 3, collapsed") is null && Named(child, "Foreign keys without an index") is null && view.IsDirty, "Show Indexes off hides the sections and the warning, and marks the diagram changed");
        check(Math.Abs(view.BoxOf(T("ixui_parent")).ActualWidth - widthWith) < 0.5 && Math.Abs(heightWith - view.BoxOf(T("ixui_parent")).ActualHeight - 18) < 0.5, "The closed section adds one row and no width");
        var area = view.FillMenu(new ContextMenu(), null);
        check(Has(area, "DiagramShowIndexes") && !Item(area, "DiagramShowIndexes").IsChecked && Has(area, "DiagramRefresh") && Item(area, "DiagramRefresh").IsEnabled, "The diagram menu has Show Indexes and Refresh");
        view.ToggleIndexes(); await Idle(); view.UpdateLayout();
        check(Item(view.FillMenu(new ContextMenu(), null), "DiagramShowIndexes").IsChecked && Named(T("ixui_parent"), "Indexes, 3, collapsed") is not null, "Show Indexes is on by default and turns back on");

        // Open the section with a click on its header.
        Click(Named(T("ixui_parent"), "Indexes, 3, collapsed")!);
        await Idle(); view.UpdateLayout();
        parent = T("ixui_parent");
        var rows = NamedStart(parent, "Index ").ToArray();
        check(parent.IndexesExpanded && Named(parent, "Indexes, 3, expanded") is not null && rows.Length == 3, "A click on the header opens the section with one row per index");
        var trgmRow = rows.Single(r => AutomationProperties.GetName(r).StartsWith("Index ixui_parent_name_trgm", StringComparison.Ordinal));
        check(AutomationProperties.GetName(trgmRow) == "Index ixui_parent_name_trgm, Index, GIN, (name gin_trgm_ops)" && Descendants(trgmRow).OfType<TextBlock>().Any(b => Text(b) == "ixui_parent_name_trgm  GIN  (name gin_trgm_ops)"),
            "The trigram index row shows its name, GIN and the operator class");
        check(trgmRow.ToolTip is string rowTip && rowTip.Contains("Operator class: name gin_trgm_ops") && rowTip.Contains("CREATE INDEX ixui_parent_name_trgm ON public.ixui_parent USING gin (name gin_trgm_ops)"), "The row tooltip has the operator class and the definition");
        var glyphs = rows.Select(r => Descendants(r).OfType<Canvas>().Select(AutomationProperties.GetName).Single(n => n.Length > 0)).ToArray();
        check(glyphs.SequenceEqual(["Primary key index", "Unique index", "Index"]), "Primary key, unique and plain index have their own glyphs");
        check(Descendants(view.BoxOf(parent)).OfType<Canvas>().Count(c => AutomationProperties.GetName(c) == "Primary key") == 1, "The primary key glyph of the column is not counted twice");
        check(view.BoxOf(parent).ActualHeight > heightWith + 18 * 3 - 0.5, "The open section makes the table taller");
        Click(Named(parent, "Indexes, 3, expanded")!, clicks: 1);
        await Idle(); view.UpdateLayout();
        check(!T("ixui_parent").IndexesExpanded && NamedStart(T("ixui_parent"), "Index ").Count() == 0, "Another click closes it");
        Click(Named(T("ixui_parent"), "Indexes, 3, collapsed")!);
        await Idle(); view.UpdateLayout();
        parent = T("ixui_parent");

        if (Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") is { Length: > 0 } folder)
        {
            var surface = view.DiagramSurface;
            double right = view.Tables.Max(t => t.X + view.BoxOf(t).ActualWidth) + 20, bottom = view.Tables.Max(t => t.Y + view.BoxOf(t).ActualHeight) + 20;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)right * 2, (int)bottom * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            System.IO.Directory.CreateDirectory(folder);
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, "indexes.png")); encoder.Save(file);
        }

        // Double click on an index row opens Indexes/Keys on that index.
        Window? shown = null;
        view.DialogShown = d => { shown = d; d.Dispatcher.BeginInvoke(d.Close); };
        var uniqueRow = NamedStart(parent, "Index ixui_parent_email_key").Single();
        var uniquePoint = Center(uniqueRow);
        view.Press(uniquePoint, 1); view.Release(uniquePoint); view.Press(uniquePoint, 2); view.Release(uniquePoint);
        await Idle();
        check(shown is DesignerObjectsDialog, "A double click on an index row opens Indexes/Keys");
        shown = null;
        var rowMenu = view.FillMenu(new ContextMenu(), new DiagramHit(T("ixui_parent"), null, DiagramHitPart.Index, "ixui_parent_email_key"));
        Item(rowMenu, "DiagramIndexes").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Idle();
        check(shown is DesignerObjectsDialog && !Has(rowMenu, "DiagramUnindexedForeignKeys"), "Indexes/Keys from the menu of an index row; no Find Unindexed Foreign Keys on a table without the warning");
        view.DialogShown = null;

        // The warning of a foreign key without an index.
        child = T("ixui_child");
        var warning = Named(child, "Foreign keys without an index");
        check(warning is not null && Named(T("ixui_ok"), "Foreign keys without an index") is null && Named(parent, "Foreign keys without an index") is null, "Only the table with a foreign key without an index shows the warning");
        check(warning!.ToolTip is string warningTip && warningTip.Contains("ixui_child_parent_fk (parent_id)") && warningTip.Contains("Find Unindexed Foreign Keys"), "The warning names the key and says how to fix it");
        check(Has(view.FillMenu(new ContextMenu(), new DiagramHit(child, null, DiagramHitPart.Title)), "DiagramUnindexedForeignKeys"), "The table menu has Find Unindexed Foreign Keys… when the warning shows");
        ForeignKeyIndexScanWindow? scan = null;
        int scanned = -1, checkedRows = -1, otherTables = -1; string? scanTitle = null;
        window.UnindexedForeignKeysShown = w =>
        {
            scan = w;
            w.Loaded += async (_, _) =>
            {
                for (int i = 0; i < 200 && w.Rows.Count == 0; i++) await Task.Delay(30);
                scanned = w.Rows.Count; checkedRows = w.Rows.Count(r => r.Selected);
                otherTables = w.Rows.Count(r => r.Key.Key.Table != "ixui_child");
                scanTitle = w.Title;
                w.Close();
            };
        };
        Click(warning);
        for (int i = 0; i < 300 && (scan is null || scan.IsVisible || scanned < 0); i++) await Task.Delay(30);
        await Idle();
        window.UnindexedForeignKeysShown = null;
        check(scan is not null && scan.Owner == window, "A click on the warning opens Find Unindexed Foreign Keys");
        check(scanned > 0 && checkedRows == 0, "Opened from the warning, the window scans by itself and checks nothing");
        check(otherTables == 0 && scanTitle?.EndsWith(".ixui_child", StringComparison.Ordinal) == true, "The window is about the clicked table only, named in the title");
        view.SelectAt(new Point(child.X + 10, child.Y + 8));
        view.SetTableView(DiagramTableView.NameOnly);
        await Idle(); view.UpdateLayout();
        check(Named(T("ixui_child"), "Foreign keys without an index") is not null && NamedStart(T("ixui_child"), "Indexes,").Count() == 0, "Name Only keeps the warning in the title and shows no section");

        // Keys shows the columns of every index, as SSMS 22.11 does.
        view.SelectAt(new Point(parent.X + 10, parent.Y + 8));
        view.SetTableView(DiagramTableView.Keys);
        await Idle();
        check(T("ixui_parent").VisibleColumns.Select(c => c.Name).SequenceEqual(["id", "email", "name"]), "Keys shows the columns of keys and indexes, not the others");
        view.SetTableView(DiagramTableView.ColumnNames);

        // Saved by diagram, with the indexes from the catalog.
        view.NamePromptOverride = _ => "IndexUi";
        check(await view.SaveAsync() && !view.IsDirty, "The diagram saves");
        int id = view.Info!.Id;
        using (var saved = JsonDocument.Parse((string)(await Scalar($"SELECT definition::text FROM pgms.diagrams WHERE diagram_id={id}"))!))
        {
            var placements = saved.RootElement.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
            check(saved.RootElement.GetProperty("format").GetInt32() == 2 && saved.RootElement.GetProperty("showIndexes").GetBoolean() && placements["ixui_parent"].GetProperty("indexesExpanded").GetBoolean() && !placements["ixui_child"].GetProperty("indexesExpanded").GetBoolean()
                && placements["ixui_parent"].GetProperty("indexes").GetArrayLength() == 3 && placements["ixui_child"].GetProperty("unindexedForeignKeys")[0].GetProperty("name").GetString() == "ixui_child_parent_fk",
                "The saved diagram keeps Show Indexes, the open section and the indexes of each table");
        }

        // Refresh reads the indexes again without marking the diagram changed.
        await Sql("CREATE INDEX ixui_child_parent ON public.ixui_child (parent_id)");
        await view.RefreshAsync(); await Idle(); view.UpdateLayout();
        check(T("ixui_child").Catalog.UnindexedForeignKeys.Count == 0 && Named(T("ixui_child"), "Foreign keys without an index") is null && T("ixui_child").Catalog.Indexes.Any(i => i.Name == "ixui_child_parent") && !view.IsDirty,
            "Refresh reads the new index, the warning goes away and the diagram stays unchanged");

        // Reopen: the section stays open; a diagram of format 1 opens with the sections closed.
        window.DiagramClosePrompt = _ => MessageBoxResult.No;
        check(await window.CloseDiagramAsync(view), "The diagram closes");
        async Task<DatabaseDiagramView> Open(int diagram, string name)
        {
            var opened = window.OpenDiagram(connection, diagram, name);
            for (int i = 0; i < 100 && (opened.IsBusy || opened.Tables.Count == 0); i++) await Task.Delay(50);
            await Idle(); opened.UpdateLayout();
            return opened;
        }
        var reopened = await Open(id, "IndexUi");
        check(reopened.Tables.Single(t => t.Name == "ixui_parent").IndexesExpanded && reopened.IndexesShown && !reopened.IsDirty, "Reopened, the open section and Show Indexes are as saved");
        await window.CloseDiagramAsync(reopened);
        int oldId = (int)(await Scalar("""INSERT INTO pgms.diagrams (name, definition) VALUES ('IndexOld', '{"format":1,"tables":[{"schema":"public","name":"ixui_ok","x":10,"y":10}],"zoom":1}') RETURNING diagram_id"""))!;
        var old = await Open(oldId, "IndexOld");
        var oldTable = old.Tables.Single();
        check(!old.IsDirty && old.IndexesShown && !oldTable.IndexesExpanded && Descendants(old.BoxOf(oldTable)).OfType<FrameworkElement>().Any(e => AutomationProperties.GetName(e) == "Indexes, 2, collapsed"), "A diagram saved before PGMS-044 opens unchanged, with closed sections");
        await window.CloseDiagramAsync(old);
        await Sql($"DELETE FROM pgms.diagrams WHERE diagram_id IN ({id}, {oldId}); DROP TABLE public.ixui_ok, public.ixui_child, public.ixui_parent");
        window.DiagramClosePrompt = null;
    }

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
