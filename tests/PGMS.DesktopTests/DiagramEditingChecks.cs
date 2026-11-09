using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>Stage 2 of Database Diagrams (PGMS-028): Table View, columns in the diagram, New Table, annotations, page breaks, Copy Diagram.</summary>
internal static class DiagramEditingChecks
{
    internal static async Task RunAsync(MainWindow window, string connection, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Sql("""
            CREATE TABLE public.e2_parent (id integer PRIMARY KEY, code text NOT NULL UNIQUE, note text);
            CREATE TABLE public.e2_child (id integer PRIMARY KEY, parent_id integer CONSTRAINT fk_e2 REFERENCES public.e2_parent(id), amount numeric(10,2) DEFAULT 0, remark text);
            CREATE TABLE public.e2_plain (a integer, b integer);
            """);
        window.DiagramConfirmPrompt = _ => true;
        var view = (await window.NewDiagramAsync(connection, showAddTable: false))!;
        await Idle();
        view.ConfirmPrompt = _ => true;
        var candidates = await view.CandidateTablesAsync();
        await view.AddTablesAsync(candidates.Where(c => c.Name.StartsWith("e2_", StringComparison.Ordinal)).ToArray());
        await Idle();
        DiagramTableModel T(string name) => view.Tables.Single(t => t.Name == name);
        Point Title(DiagramTableModel t) => new(t.X + 10, t.Y + 8);
        void Choose(DiagramTableModel t) { view.UpdateLayout(); view.SelectAt(Title(t)); }
        void ChooseColumn(DiagramTableModel t, string column)
        {
            view.UpdateLayout();
            var row = view.RowOf(t, column);
            var point = row.TranslatePoint(new Point(row.ActualWidth / 2, row.ActualHeight / 2), view.DiagramSurface);
            view.SelectAt(point);
        }
        string[] TextsOf(DiagramTableModel t) => Descendants(view.BoxOf(t)).OfType<TextBlock>().Where(b => b.Visibility == Visibility.Visible).Select(b => b.Text).ToArray();

        // Menus.
        Choose(T("e2_child"));
        var titleMenu = view.FillMenu(new ContextMenu(), new DiagramHit(T("e2_child"), null, DiagramHitPart.Title));
        MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(i => i.Name == name);
        check(new[] { "DiagramSetPrimaryKey", "DiagramInsertColumn", "DiagramDeleteColumn" }.All(n => !Item(titleMenu, n).IsEnabled), "On the table title the column items are grey, as in SSMS");
        var tableView = Item(titleMenu, "DiagramTableView").Items.OfType<MenuItem>().Select(i => i.Name).ToArray();
        check(tableView.SequenceEqual(["DiagramViewStandard", "DiagramViewColumnNames", "DiagramViewKeys", "DiagramViewNameOnly", "DiagramViewCustom", "DiagramModifyCustom"]) && Item(titleMenu, "DiagramTableView").Items.OfType<MenuItem>().Single(i => i.Name == "DiagramViewColumnNames").IsChecked, "Table View has the modes of SSMS, with Column Names as the default");
        ChooseColumn(T("e2_child"), "remark");
        var rowMenu = view.FillMenu(new ContextMenu(), new DiagramHit(T("e2_child"), "remark", DiagramHitPart.Column));
        check(view.SelectedColumns.SequenceEqual(["remark"]) && new[] { "DiagramSetPrimaryKey", "DiagramInsertColumn", "DiagramDeleteColumn" }.All(n => Item(rowMenu, n).IsEnabled) && (string)Item(rowMenu, "DiagramSetPrimaryKey").Header == "Set Primary Key", "A click on a row selects the column and enables the column items");
        ChooseColumn(T("e2_child"), "id");
        check((string)Item(view.FillMenu(new ContextMenu(), new DiagramHit(T("e2_child"), "id", DiagramHitPart.Column)), "DiagramSetPrimaryKey").Header == "Remove Primary Key", "On a column of the primary key the item is Remove Primary Key");

        // Table View.
        var child = T("e2_child");
        Choose(child);
        view.SetTableView(DiagramTableView.Keys);
        await Idle();
        check(TextsOf(child).Contains("id") && TextsOf(child).Contains("parent_id") && !TextsOf(child).Contains("amount") && !TextsOf(child).Contains("remark"), "Keys shows only the columns of the keys");
        view.SetTableView(DiagramTableView.NameOnly);
        await Idle();
        check(TextsOf(child).Length == 1 && view.BoxOf(child).ActualHeight < 30, "Name Only shows only the title");
        check(view.Relations.Single(r => r.Key.Name == "fk_e2") is { } hidden && view.RoutePoints(hidden).Count >= 2, "The relationship line stays on a table showing only its name");
        view.SetTableView(DiagramTableView.Standard);
        await Idle();
        check(TextsOf(child).Contains("Column Name") && TextsOf(child).Contains("Data Type") && TextsOf(child).Contains("Allow Nulls") && TextsOf(child).Contains("numeric(10,2)"), "Standard shows the column name, data type and allow nulls");
        check(Descendants(view.BoxOf(child)).OfType<CheckBox>().Count() == 4, "Standard has an Allow Nulls box in each row");
        double nameWidth = Descendants(view.BoxOf(child)).OfType<TextBlock>().First(b => b.Text == "Data Type").TranslatePoint(new Point(0, 0), view.BoxOf(child)).X;
        double typeCell = Descendants(view.BoxOf(child)).OfType<TextBlock>().First(b => b.Text == "numeric(10,2)").TranslatePoint(new Point(0, 0), view.BoxOf(child)).X;
        check(Math.Abs(nameWidth - typeCell) < 0.5, "The header and the rows line up");
        view.CustomColumnsPrompt = current => current.SequenceEqual(DiagramColumnFields.Standard) ? [DiagramColumnFields.DefaultValue] : null;
        view.ModifyCustomView();
        await Idle();
        check(child.View == DiagramTableView.Custom && TextsOf(child).Contains("Default Value") && TextsOf(child).Contains("0") && !TextsOf(child).Contains("Data Type"), "Modify Custom chooses the fields and shows the table in Custom");
        view.SetTableView(DiagramTableView.Standard);
        await Idle();

        // Editing in Standard.
        check(view.BeginCellEdit(child, "remark", DiagramColumnFields.DataType) && view.Editor is { Text: "text" }, "A cell of the Standard view opens for typing");
        view.Editor!.Text = "not a type!"; check(!view.CommitEdit() && child.Column("remark")!.Type == "text" && view.FeedbackText.Contains("data type"), "An invalid type is refused and the column stays");
        view.BeginCellEdit(child, "remark", DiagramColumnFields.DataType); view.Editor!.Text = "varchar(40)";
        check(view.CommitEdit() && child.Column("remark")!.Type == "varchar(40)" && view.Editor is null && TextsOf(child).Contains("varchar(40)") && view.TableTitle(child).EndsWith('*'), "A valid type is applied and the table gets *");
        view.BeginCellEdit(child, "parent_id"); view.Editor!.Text = "owner_id"; view.CommitEdit();
        check(child.Column("owner_id") is not null && child.ForeignKeys.Single(k => k.Name == "fk_e2").Columns.SequenceEqual(["owner_id"]), "Renaming a column in the diagram follows into its foreign key");
        view.BeginCellEdit(child, "owner_id"); view.Editor!.Text = "id"; view.CommitEdit();
        check(view.FeedbackText.Contains("more than once") && child.Column("owner_id") is not null, "A repeated column name is refused");
        view.BeginCellEdit(child, "remark"); view.Editor!.Text = "typing"; view.CancelEdit();
        check(child.Column("remark") is not null && view.Editor is null, "Esc undoes the typing");
        var boxes = Descendants(view.BoxOf(child)).OfType<CheckBox>().ToArray();
        check(!boxes[0].IsEnabled && boxes[3].IsEnabled, "The primary key column cannot be changed to allow nulls");
        boxes[3].IsChecked = false; boxes[3].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Idle();
        check(!child.Column("remark")!.Nullable, "Allow Nulls changes the column");
        view.ReviewPrompt = _ => true;

        // The slow second click opens the cell; a double click does not.
        var remark = view.RowOf(child, "remark");
        var remarkCell = Descendants(remark).OfType<TextBlock>().First(b => b.Text == "varchar(40)");
        var at = remarkCell.TranslatePoint(new Point(4, 6), view.DiagramSurface);
        view.Press(at); view.Release(at);
        check(view.SelectedColumns.SequenceEqual(["remark"]) && view.Editor is null, "A first click selects the row without opening it");
        view.Press(at); view.Release(at);
        check(view.Editor is null, "The second click waits for the double click time");
        await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 200)); await Idle();
        check(view.Editor is { Text: "varchar(40)" }, "A slow second click opens the clicked cell");
        view.CancelEdit();

        // Primary key, insert and delete.
        var plain = T("e2_plain");
        Choose(plain); view.SetTableView(DiagramTableView.Standard); await Idle();
        ChooseColumn(plain, "a");
        view.UpdateLayout(); view.SelectAt(view.RowOf(plain, "b").TranslatePoint(new Point(30, 8), view.DiagramSurface), add: true);
        check(view.SelectedColumns.Count == 2 && view.SelectedTables.Single() == plain, "Shift + click on another row adds the column");
        view.TogglePrimaryKey();
        check(plain.PrimaryKey.SequenceEqual(["a", "b"]) && !plain.Column("a")!.Nullable && !plain.Column("b")!.Nullable && Descendants(view.BoxOf(plain)).OfType<Canvas>().Count(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Primary key") == 2, "Set Primary Key with two rows makes a composite key and the columns NOT NULL");
        check(view.BuildChanges()!.Script.Sql.Contains("""ADD CONSTRAINT "e2_plain_pkey" PRIMARY KEY ("a", "b")"""), "The script adds the primary key");
        view.TogglePrimaryKey();
        check(plain.PrimaryKey.Count == 0, "Remove Primary Key takes the key away");
        ChooseColumn(plain, "b");
        view.InsertColumn(plain);
        check(plain.Columns.Select(c => c.Name).SequenceEqual(["a", "Column1", "b"]) && view.Editor is { Text: "Column1" } && view.FeedbackText.Contains("end of the table"), "Insert Column adds a row above the selected one, opens its name and says where PostgreSQL puts it");
        view.Editor!.Text = "c"; view.CommitEdit();
        check(view.BuildChanges()!.Script.Sql.Contains("""ADD COLUMN "c" text NULL"""), "The new column is added by the script");
        ChooseColumn(plain, "c");
        view.DeleteSelectedColumns();
        check(plain.Column("c") is null, "Delete Column removes a new column");
        check(view.BuildChanges()!.Script.Sql.Contains("-- public.e2_child") && !view.BuildChanges()!.Script.Sql.Contains("ADD COLUMN"), "A column added and deleted again leaves nothing in the script");
        ChooseColumn(plain, "a"); ChooseColumn(plain, "a");
        view.UpdateLayout(); view.SelectAt(view.RowOf(plain, "b").TranslatePoint(new Point(30, 8), view.DiagramSurface), add: true);
        view.DeleteSelectedColumns();
        check(view.FeedbackText.Contains("at least one column") && plain.Columns.Count == 2, "The last column cannot be deleted");

        var parent = T("e2_parent");
        Choose(parent);
        view.SetTableView(DiagramTableView.ColumnNames); await Idle();
        ChooseColumn(parent, "id");
        string? asked = null;
        view.ConfirmPrompt = q => { asked = q; return false; };
        view.DeleteSelectedColumns();
        check(parent.Column("id") is not null && asked?.Contains("fk_e2") == true && asked.Contains("e2_parent_pkey"), "Deleting a column in use names the keys and relationships and can be refused");
        view.ConfirmPrompt = _ => true;

        // New Table.
        view.TableNamePromptOverride = suggested => suggested == "Table1" ? null : "x";
        check(view.NewTable(null) is null, "Cancel in Choose Name adds no table");
        view.TableNamePromptOverride = _ => "bad\0name";
        check(view.NewTable(null) is null && view.FeedbackText.Length > 0, "An invalid table name is refused");
        view.TableNamePromptOverride = _ => "e2_parent";
        check(view.NewTable(null) is null && view.FeedbackText.Contains("already in this diagram"), "A name already in the diagram is refused");
        view.TableNamePromptOverride = _ => "e2_new";
        var created = view.NewTable(new Point(900, 40))!;
        await Idle();
        check(created.IsNew && created.View == DiagramTableView.Standard && view.Editor is { Text: "Column1" } && view.TableTitle(created) == "e2_new*" && created.X == 900, "New Table comes where the menu was, in Standard, with its first column open and *");
        view.Editor!.Text = "id"; view.CommitEdit();
        await Idle();
        ChooseColumn(created, "id");
        view.TogglePrimaryKey();
        view.BeginCellEdit(created, "id", DiagramColumnFields.DataType); view.Editor!.Text = "integer"; view.CommitEdit();
        check(view.BuildChanges()!.Script.Sql.Contains("CREATE TABLE \"public\".\"e2_new\"") && created.PrimaryKey.SequenceEqual(["id"]), "The script creates the new table with its key");
        check(view.BuildChanges()!.Script.Sql.IndexOf("CREATE TABLE", StringComparison.Ordinal) < view.BuildChanges()!.Script.Sql.IndexOf("-- public.e2_child", StringComparison.Ordinal), "A new table comes before the changes to existing tables");
        Choose(created);
        view.ShowProperties();
        check(view.FeedbackText.Contains("Save the diagram"), "Properties of an unsaved table say to save first");
        check(!Item(view.FillMenu(new ContextMenu(), new DiagramHit(created, null, DiagramHitPart.Title)), "DiagramDesign").IsEnabled, "Design is grey on a table not saved yet");
        view.RelationshipPrompt = _ => true;
        check(view.CreateRelationship(created, "id", T("e2_plain"), "a") && view.Relations.Any(r => r.Primary == created), "A relationship can point at a new table");
        check(view.BuildChanges()!.Script.Sql.IndexOf("CREATE TABLE", StringComparison.Ordinal) < view.BuildChanges()!.Script.Sql.IndexOf("REFERENCES \"public\".\"e2_new\"", StringComparison.Ordinal), "The new table is created before the foreign key that uses it");
        view.BeginCellEdit(created, "id"); view.Editor!.Text = "key_id"; view.CommitEdit();
        check(T("e2_plain").ForeignKeys.Single(k => k.ReferencedTable == "e2_new").ReferencedColumns.SequenceEqual(["key_id"]) && created.PrimaryKey.SequenceEqual(["key_id"]), "Renaming a column of a new table follows into its key and into foreign keys");

        view.TableNamePromptOverride = _ => "e2_gone";
        var gone = view.NewTable(null)!;
        view.CommitEdit();
        view.CreateRelationship(gone, "Column1", T("e2_plain"), "b");
        Choose(gone);
        view.DeleteSelectedTables();
        check(view.Tables.All(t => t.Name != "e2_gone") && !view.BuildChanges()!.Script.Sql.Contains("e2_gone") && T("e2_plain").ForeignKeys.All(k => k.ReferencedTable != "e2_gone"), "Deleting a table never saved leaves nothing to drop, and no key pointing at it");

        // Annotations.
        var note = view.NewAnnotation(new Point(500, 500));
        await Idle();
        check(view.Editor is not null && view.SelectedAnnotation == note, "New Text Annotation opens for typing");
        view.CancelEdit(); await Idle();
        check(view.Annotations.Count == 0, "A new annotation left empty goes away");
        note = view.NewAnnotation(new Point(500, 500));
        view.Editor!.Text = "Two\nlines"; view.CommitEdit();
        await Idle();
        check(note.Text == "Two\nlines" && view.IsDirty && view.Annotations.Single() == note, "The text of an annotation is kept");
        var inside = new Point(510, 508);
        view.Press(inside); view.DragTo(new Point(610, 558)); view.Release(new Point(610, 558));
        check(note.X == 600 && note.Y == 550 && view.SelectedAnnotation == note, "An annotation moves with the mouse");
        view.Undo();
        check(note.X == 500 && note.Y == 500, "Undo puts the annotation back");
        check(view.DoubleClick(note) && view.Editor is { Text: "Two\nlines" }, "A double click opens the annotation");
        view.Editor!.Text = "Changed"; view.CommitEdit();
        check(note.Text == "Changed", "Editing changes the text");
        var menuOnNote = view.FillMenu(new ContextMenu(), note).Items.OfType<MenuItem>().Select(i => i.Name).ToArray();
        check(menuOnNote.SequenceEqual(["DiagramEditAnnotation", "DiagramDeleteAnnotation"]), "An annotation has Edit Text and Delete Annotation");
        view.UpdateLayout(); view.SelectAt(inside);
        check(view.SelectedAnnotation == note, "Clicking an annotation selects it");

        // Page breaks.
        view.PageSizeOverride = () => new Size(700, 500);
        var surface = view.DiagramSurface;
        view.TogglePageBreaks(); await Idle();
        check(view.ShowPageBreaks && view.PageBreaks.Children.OfType<Line>().Count() == (int)Math.Ceiling(surface.Width / 700) - 1 + (int)Math.Ceiling(surface.Height / 500) - 1, "View Page Breaks draws a line at every page of the diagram");
        check(view.PageBreaks.Children.OfType<Line>().First().X1 == 700 && view.PageBreaks.IsHitTestVisible == false, "The lines are at the page size and cannot be clicked");
        view.PageSizeOverride = () => new Size(300, 400);
        view.RecalculatePageBreaks(); await Idle();
        check(view.PageBreaks.Children.OfType<Line>().First().X1 == 300, "Recalculate Page Breaks uses the page as it is now");
        view.TogglePageBreaks(); await Idle();
        check(view.PageBreaks.Children.Count == 0, "Turning them off takes the lines away");
        view.TogglePageBreaks();

        // Copy Diagram.
        Choose(parent);
        BitmapSource? copied = null;
        view.ClipboardOverride = image => copied = image;
        check(view.CopyDiagram() && copied is not null && view.FeedbackText.Contains("copied"), "Copy Diagram to Clipboard hands over a picture");
        check(view.SelectedTables.Single() == parent, "Copying keeps the selection");
        if (Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") is { Length: > 0 } folder)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(copied!));
            Directory.CreateDirectory(folder); using var file = File.Create(System.IO.Path.Combine(folder, "stage2.png")); encoder.Save(file);
        }
        var pixels = new byte[copied!.PixelWidth * copied.PixelHeight * 4];
        copied.CopyPixels(pixels, copied.PixelWidth * 4, 0);
        int count = copied.PixelWidth * copied.PixelHeight;
        int dark = Enumerable.Range(0, count).Count(i => pixels[i * 4] < 90 && pixels[i * 4 + 1] < 90 && pixels[i * 4 + 2] < 90);
        int blue = Enumerable.Range(0, count).Count(i => pixels[i * 4 + 2] < 30 && pixels[i * 4 + 1] is > 100 and < 140 && pixels[i * 4] > 200);
        int transparent = Enumerable.Range(0, count).Count(i => pixels[i * 4 + 3] != 255);
        var right = view.Tables.Max(t => t.X + view.BoxOf(t).ActualWidth);
        check(dark > 300 && transparent == 0 && Math.Abs(copied.PixelWidth - (right - view.Tables.Min(t => t.X) + 52)) < 60, $"The picture has the tables at 100% on an opaque background (dark {dark}, transparent {transparent}, width {copied.PixelWidth}, tables {right - view.Tables.Min(t => t.X):0})");
        check(blue == 0, "The picture has no selection color");
        double before = view.Zoom; view.SetZoom(0.5);
        BitmapSource? halved = null; view.ClipboardOverride = image => halved = image; view.CopyDiagram();
        check(halved!.PixelWidth == copied.PixelWidth && view.Zoom == 0.5, "The picture does not depend on the zoom");
        view.SetZoom(before);
        // Saving everything.
        view.NamePromptOverride = _ => "Stage2";
        TableDesignChange? reviewed = null;
        view.ReviewPrompt = change => { reviewed = change; return true; };
        check(await view.SaveAsync(), "The diagram is saved: " + view.FeedbackText);
        check(reviewed!.Sql.Contains("CREATE TABLE") && reviewed.Sql.Contains("""RENAME COLUMN "parent_id" TO "owner_id"""), "The script reviewed has every change");
        check(await Scalar("SELECT string_agg(attname || ':' || format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid='public.e2_new'::regclass AND attnum>0") is "key_id:integer", "The new table exists in the database");
        check(await Scalar("SELECT count(*) FROM pg_constraint WHERE conrelid='public.e2_plain'::regclass AND contype='f' AND confrelid='public.e2_new'::regclass") is 1L, "Its foreign key exists");
        check(await Scalar("SELECT is_nullable FROM information_schema.columns WHERE table_name='e2_child' AND column_name='remark'") is "NO" && await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name='e2_child' AND column_name='owner_id'") is "integer", "The column edits reached the database");
        var saved = T("e2_new");
        check(!saved.IsNew && saved.Original.Oid != 0 && saved.View == DiagramTableView.Standard && !view.IsDirty && view.Title == "Stage2", "After Save the table is an ordinary one and the tab is clean");
        check(view.Relations.Any(r => r.Primary == saved), "The relationship to it is still drawn");
        int id = view.Info!.Id;
        window.DiagramClosePrompt = _ => MessageBoxResult.No;
        await window.CloseDiagramAsync(view);
        var reopened = window.OpenDiagram(connection, id, "Stage2");
        for (int i = 0; i < 100 && (reopened.IsBusy || reopened.Tables.Count == 0); i++) await Task.Delay(50);
        await Idle();
        check(reopened.Tables.Single(t => t.Name == "e2_child").View == DiagramTableView.Standard && reopened.Tables.Single(t => t.Name == "e2_parent").View == DiagramTableView.ColumnNames, "Reopening keeps the Table View of each table");
        check(reopened.Annotations.Single() is { Text: "Changed", X: 500, Y: 500 }, "Reopening keeps the annotation");
        check(reopened.ShowPageBreaks && reopened.PageSize == new Size(300, 400) && reopened.PageBreaks.Children.OfType<Line>().First().X1 == 300 && !reopened.IsDirty, "Reopening keeps the page breaks without asking the printer");
        await window.CloseDiagramAsync(reopened);
        await new DiagramService(connection).DeleteAsync(id);
        await Sql("DROP TABLE public.e2_plain, public.e2_new, public.e2_child, public.e2_parent");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

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
