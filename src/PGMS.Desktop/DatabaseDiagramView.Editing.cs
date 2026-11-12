using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Stage 2 of the database diagram (PGMS-028, docs/MARCO-M1043.md): Table View, columns edited in the diagram, New Table, text annotations, page breaks and Copy Diagram to Clipboard.</summary>
public partial class DatabaseDiagramView
{
    private static readonly Brush SelectedRowBrush = Frozen("#CCE4F7"), NoteFrameBrush = Frozen("#B0B0B0"), PageBreakBrush = Frozen("#808080");

    private readonly HashSet<string> selectedColumns = new(StringComparer.Ordinal);
    private DiagramTableModel? selectedColumnTable;
    private readonly List<DiagramAnnotation> annotations = [];
    private readonly Dictionary<DiagramAnnotation, Border> noteBoxes = [];
    private DiagramAnnotation? selectedAnnotation;
    private Point noteOrigin, menuPoint = new(40, 40);
    private bool showPageBreaks;
    private Size? pageSize;
    private TextBox? editor;
    private Func<string, bool>? editorCommit;
    private Action? editorRestore, editorCancelled;
    private DiagramHit? pendingCell;
    private DispatcherTimer? slowClick;
    private int newTables;

    internal Func<string, string?>? TableNamePromptOverride { get; set; }
    internal Func<IReadOnlyList<string>, IReadOnlyList<string>?>? CustomColumnsPrompt { get; set; }
    internal Func<Size>? PageSizeOverride { get; set; }
    internal Action<BitmapSource>? ClipboardOverride { get; set; }

    public IReadOnlyList<DiagramAnnotation> Annotations => annotations;
    public DiagramAnnotation? SelectedAnnotation => selectedAnnotation;
    public IReadOnlyCollection<string> SelectedColumns => selectedColumns;
    public bool ShowPageBreaks => showPageBreaks;
    internal Size? PageSize => pageSize;
    internal Canvas PageBreaks => PageBreaksLayer;
    internal TextBox? Editor => editor;

    private static void ApplyPlacement(DiagramTableModel table, DiagramTablePlacement placement)
    {
        if (Enum.TryParse<DiagramTableView>(placement.View, out var view)) table.View = view;
        if (placement.CustomColumns is { } custom) { table.CustomColumns.Clear(); table.CustomColumns.AddRange(custom.Where(DiagramColumnFields.All.Contains)); }
        table.IndexesExpanded = placement.IndexesExpanded;
    }

    private Rect[] ContentRects() => tables.Where(boxes.ContainsKey).Select(Bounds).Concat(annotations.Where(noteBoxes.ContainsKey).Select(NoteBounds)).ToArray();
    private Rect NoteBounds(DiagramAnnotation note)
    {
        var box = noteBoxes[note];
        if (box.DesiredSize.Width == 0) box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new(note.X, note.Y, box.DesiredSize.Width, box.DesiredSize.Height);
    }

    // ---- Table View ----
    private Grid FieldHeader(DiagramTableModel table, IReadOnlyList<string> fields)
    {
        var header = new Grid { Height = 18, Background = SelectorBrush, Tag = new DiagramHit(table, null, DiagramHitPart.Body) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "DiagramName" });
        var name = new TextBlock { Text = DiagramColumnFields.Name, Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); header.Children.Add(name);
        for (int i = 0; i < fields.Count; i++)
        {
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "DiagramField" + i });
            var text = new TextBlock { Text = fields[i], Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, i + 2); header.Children.Add(text);
        }
        return header;
    }

    private void AddFieldCells(Grid row, DiagramTableModel table, DesignColumn column, IReadOnlyList<string> fields, bool inPrimaryKey)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "DiagramField" + i });
            FrameworkElement cell;
            if (fields[i] == DiagramColumnFields.AllowNulls)
            {
                string name = column.Name;
                // A key column or an identity cannot be null, as in the Table Properties grid.
                var box = new CheckBox { IsChecked = column.Nullable, IsEnabled = CanEditColumns(table) && !inPrimaryKey && column.Identity == ColumnIdentity.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 10, 0) };
                AutomationProperties.SetName(box, "Allow Nulls, " + name);
                box.Click += (_, _) => SetField(table, name, DiagramColumnFields.AllowNulls, box.IsChecked == true ? "true" : "false");
                cell = box;
            }
            else cell = new TextBlock { Text = DiagramColumnFields.Text(column, fields[i]), Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Tag = new DiagramHit(table, column.Name, DiagramHitPart.Column, fields[i]) };
            Grid.SetColumn(cell, i + 2); row.Children.Add(cell);
        }
    }

    private void TableViewClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TableViewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        FillTableViewItems(menu.Items, selected.FirstOrDefault());
        menu.IsOpen = true;
    }

    private void FillTableViewItems(ItemCollection items, DiagramTableModel? table)
    {
        items.Clear();
        void Add(string header, string name, DiagramTableView view)
        {
            var item = new MenuItem { Header = header, Name = name, IsEnabled = table is not null, IsChecked = table is not null && selected.Count > 0 && selected.All(t => t.View == view) };
            item.Click += (_, _) => SetTableView(view);
            items.Add(item);
        }
        Add("Standard", "DiagramViewStandard", DiagramTableView.Standard);
        Add("Column Names", "DiagramViewColumnNames", DiagramTableView.ColumnNames);
        Add("Keys", "DiagramViewKeys", DiagramTableView.Keys);
        Add("Name Only", "DiagramViewNameOnly", DiagramTableView.NameOnly);
        Add("Custom", "DiagramViewCustom", DiagramTableView.Custom);
        items.Add(new Separator());
        var modify = new MenuItem { Header = "Modify Custom …", Name = "DiagramModifyCustom", IsEnabled = table is not null };
        modify.Click += (_, _) => ModifyCustomView();
        items.Add(modify);
    }

    /// <summary>Applies a view to the selected tables; the box and the lines follow its new size.</summary>
    public void SetTableView(DiagramTableView view)
    {
        CommitEdit();
        if (selected.Count == 0) return;
        foreach (var table in selected) table.View = view;
        Rebuild(); Touch();
    }

    /// <summary>Chooses the fields of the Custom view of the selected tables and shows them in it.</summary>
    public void ModifyCustomView()
    {
        if (selected.Count == 0) return;
        var chosen = (CustomColumnsPrompt ?? (current => ColumnSelectionPrompt.Show(OwnerWindow, current)))(selected.First().CustomColumns);
        if (chosen is null) return;
        foreach (var table in selected) { table.CustomColumns.Clear(); table.CustomColumns.AddRange(chosen); }
        SetTableView(DiagramTableView.Custom);
    }

    // ---- Columns in the diagram ----
    private bool CanEditColumns(DiagramTableModel table) => !table.IsReadOnly && !IsBusy;
    private Brush RowBrush(DiagramTableModel table, string column) => selectedColumnTable == table && selectedColumns.Contains(column) ? SelectedRowBrush : Brushes.White;
    private void ClearColumnSelection() { selectedColumns.Clear(); selectedColumnTable = null; }

    /// <summary>A click on a row selects that column; on a title, no column. A right click on one of several selected rows keeps them all, so the menu acts on all of them.</summary>
    private void SelectColumnFromClick(DiagramHit hit)
    {
        if (hit.Column is not { } column) { ClearColumnSelection(); return; }
        if (selectedColumnTable == hit.Table && selectedColumns.Contains(column) && Mouse.RightButton == MouseButtonState.Pressed) return;
        selectedColumns.Clear(); selectedColumns.Add(column); selectedColumnTable = hit.Table;
    }

    private void PaintColumns()
    {
        foreach (var ((table, column), row) in rowVisuals) row.Row.Background = RowBrush(table, column);
        foreach (var (note, box) in noteBoxes) box.BorderBrush = note == selectedAnnotation ? SelectedLineBrush : NoteFrameBrush;
    }

    private string PrimaryKeyHeader(DiagramTableModel table) => selectedColumnTable == table && selectedColumns.Count > 0 && selectedColumns.All(table.PrimaryKey.Contains) ? "Remove Primary Key" : "Set Primary Key";

    /// <summary>Makes the selected columns the primary key; when they already are all in it, removes the key, as the Table Designer does.</summary>
    public void TogglePrimaryKey()
    {
        CommitEdit();
        if (selectedColumnTable is not { } table || selectedColumns.Count == 0 || !CanEditColumns(table)) return;
        var key = table.Indexes.FirstOrDefault(i => i.Kind == DesignIndexKind.PrimaryKey);
        if (key?.FixedDefinition is not null) { Say($"{table.Caption}: the primary key has options the diagram does not edit. Change it in the Table Designer."); return; }
        if (key is not null && selectedColumns.All(table.PrimaryKey.Contains)) table.Indexes.Remove(key);
        else
        {
            if (key is null) table.Indexes.Insert(0, key = new DesignerIndexRow { Name = table.UniqueName(table.Name + "_pkey"), Kind = DesignIndexKind.PrimaryKey, Unique = true });
            key.Columns = table.Columns.Where(c => selectedColumns.Contains(c.Name)).Select(c => new DesignIndexColumn(c.Name)).ToList();
            for (int i = 0; i < table.Columns.Count; i++) if (selectedColumns.Contains(table.Columns[i].Name)) table.Columns[i] = table.Columns[i] with { Nullable = false };
        }
        Rebuild(); Touch();
    }

    /// <summary>Adds a column above the selected one and opens its name for typing. The script adds it at the end of the table, where PostgreSQL puts new columns.</summary>
    public void InsertColumn(DiagramTableModel table)
    {
        CommitEdit();
        if (!CanEditColumns(table)) return;
        int number = 1;
        while (table.Columns.Any(c => c.Name == "Column" + number)) number++;
        string name = "Column" + number;
        int at = selectedColumnTable == table && selectedColumns.Count > 0 ? table.Columns.FindIndex(c => selectedColumns.Contains(c.Name)) : table.Columns.Count;
        table.Columns.Insert(at < 0 ? table.Columns.Count : at, new DesignColumn(null, name, "text", true));
        // The new row has to be visible to take its name.
        if (table.View is DiagramTableView.Keys or DiagramTableView.NameOnly) table.View = DiagramTableView.ColumnNames;
        selected.Clear(); selected.Add(table); selectedRelation = null;
        selectedColumns.Clear(); selectedColumns.Add(name); selectedColumnTable = table;
        Rebuild(); Touch();
        Say("PostgreSQL adds new columns at the end of the table; the row order here does not move existing columns.");
        BeginCellEdit(table, name);
    }

    /// <summary>Removes the selected columns with the keys and indexes that use them (PostgreSQL drops those with the column), and the foreign keys of the diagram that reference them.</summary>
    public void DeleteSelectedColumns()
    {
        CommitEdit();
        if (selectedColumnTable is not { } table || selectedColumns.Count == 0 || !CanEditColumns(table)) return;
        var names = selectedColumns.ToHashSet(StringComparer.Ordinal);
        if (table.Columns.All(c => names.Contains(c.Name))) { Say($"{table.Caption}: a table needs at least one column."); return; }
        var indexes = table.Indexes.Where(i => i.Columns.Any(c => names.Contains(c.Name))).ToArray();
        var own = table.ForeignKeys.Where(k => k.Columns.Any(names.Contains)).ToArray();
        var pointing = tables.SelectMany(o => o.ForeignKeys.Where(k => o.References(table, k) && k.ReferencedColumns.Any(names.Contains)).Select(k => (Owner: o, Key: k))).ToArray();
        var keys = indexes.Select(i => i.Name).Concat(own.Select(k => k.Name)).Concat(pointing.Select(p => p.Key.Name)).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length > 0 && !Confirm($"The following keys and relationships use the selected columns and will be deleted when the diagram is saved:\n\n{string.Join("\n", keys)}\n\nContinue?")) return;
        table.Columns.RemoveAll(c => names.Contains(c.Name));
        foreach (var index in indexes) table.Indexes.Remove(index);
        foreach (var key in own) table.ForeignKeys.Remove(key);
        foreach (var (owner, key) in pointing) owner.ForeignKeys.Remove(key);
        ClearColumnSelection();
        Rebuild(); Touch();
    }

    /// <summary>Changes one field of a column after the same checks as the script. False, with the reason in the feedback line, leaves the column as it was.</summary>
    internal bool SetField(DiagramTableModel table, string column, string field, string value)
    {
        int index = table.Columns.FindIndex(c => c.Name == column);
        if (index < 0 || !CanEditColumns(table)) return false;
        var old = table.Columns[index];
        var changed = field switch
        {
            DiagramColumnFields.Name => old with { Name = value.Trim() },
            DiagramColumnFields.DataType => old with { Type = value.Trim() },
            DiagramColumnFields.AllowNulls => old with { Nullable = value == "true" },
            DiagramColumnFields.DefaultValue => old with { Default = DesignerColumnRow.Optional(value) },
            DiagramColumnFields.Description => old with { Comment = value.Length == 0 ? null : value },
            _ => old
        };
        if (changed == old) return true;
        var columns = table.Columns.ToList();
        columns[index] = changed;
        try { TableDesignScript.Build(null, new TableDesign(0, table.Schema, table.Name, columns)); }
        catch (ArgumentException ex) { Say(ex.Message); Rebuild(); return false; }
        Say("");
        ApplyColumns(table, columns);
        return true;
    }

    // ---- Typing in a cell or an annotation ----
    private void ScheduleCellEdit(DiagramHit cell)
    {
        // Waits for the double click time, so the second click of a double click still opens the properties.
        slowClick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        slowClick.Tick += (_, _) => { CancelSlowClick(); BeginCellEdit(cell.Table, cell.Column!, cell.Field ?? DiagramColumnFields.Name); };
        slowClick.Start();
    }
    private void CancelSlowClick() { slowClick?.Stop(); slowClick = null; }
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    private TextBlock? CellOf(DiagramTableModel table, string column, string field) => !rowVisuals.TryGetValue((table, column), out var row) ? null
        : row.Row.Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag is DiagramHit hit && hit.Field == field);

    /// <summary>Opens a cell of the rows for typing. Enter or leaving it applies, Esc undoes, Tab goes on to the next editable field.</summary>
    internal bool BeginCellEdit(DiagramTableModel table, string column, string field = DiagramColumnFields.Name)
    {
        CommitEdit();
        if (!CanEditColumns(table) || !DiagramColumnFields.IsEditable(field) || CellOf(table, column, field) is not { } cell) return false;
        var parent = (Grid)cell.Parent;
        var box = new TextBox { Text = cell.Text, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(0), BorderThickness = new Thickness(1), MinWidth = 60, VerticalContentAlignment = VerticalAlignment.Center, FontSize = cell.FontSize };
        AutomationProperties.SetName(box, field + ", " + column);
        Grid.SetColumn(box, Grid.GetColumn(cell));
        cell.Visibility = Visibility.Hidden;
        parent.Children.Add(box);
        OpenEditor(box, text => SetField(table, column, field, text), () => { parent.Children.Remove(box); cell.Visibility = Visibility.Visible; }, null);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitEdit(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelEdit(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Tab) { NextCell(table, column, field, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
        };
        return true;
    }

    private void NextCell(DiagramTableModel table, string column, string field, bool back)
    {
        string[] order = [DiagramColumnFields.Name, .. table.Fields.Where(DiagramColumnFields.IsEditable)];
        var rows = table.VisibleColumns.Select(c => c.Name).ToList();
        int cell = rows.IndexOf(column) * order.Length + Array.IndexOf(order, field) + (back ? -1 : 1);
        if (!CommitEdit()) return;
        if (cell < 0 || cell >= rows.Count * order.Length) { Focus(); return; }
        rows = table.VisibleColumns.Select(c => c.Name).ToList();
        string target = rows[cell / order.Length];
        selectedColumns.Clear(); selectedColumns.Add(target); selectedColumnTable = table; PaintColumns();
        BeginCellEdit(table, target, order[cell % order.Length]);
    }

    private void OpenEditor(TextBox box, Func<string, bool> commit, Action restore, Action? cancelled)
    {
        editor = box; editorCommit = commit; editorRestore = restore; editorCancelled = cancelled;
        box.LostKeyboardFocus += (_, _) => { if (editor == box) CommitEdit(); };
        box.Loaded += (_, _) => { if (editor == box) { box.Focus(); box.SelectAll(); } };
        box.Focus(); box.SelectAll();
    }

    /// <summary>Applies what is being typed; false when it was refused.</summary>
    internal bool CommitEdit()
    {
        if (editor is null) return true;
        string text = editor.Text; var commit = editorCommit!; var restore = editorRestore!;
        DropEditor();
        restore();
        return commit(text);
    }

    internal void CancelEdit()
    {
        if (editor is null) return;
        var restore = editorRestore!; var cancelled = editorCancelled;
        DropEditor();
        restore();
        cancelled?.Invoke();
    }

    private void DropEditor() { editor = null; editorCommit = null; editorRestore = null; editorCancelled = null; }

    // ---- New Table ----
    private void NewTableClick(object sender, RoutedEventArgs e) => NewTable(null);

    /// <summary>A table that exists only in the diagram until Save. A name like sales.orders picks the schema; the default is public. It comes in the Standard view with one column ready to be named (inferred).</summary>
    public DiagramTableModel? NewTable(Point? at)
    {
        CommitEdit();
        if (IsBusy) return null;
        int number = newTables + 1;
        while (tables.Any(t => t.Name == "Table" + number)) number++;
        string? typed = (TableNamePromptOverride ?? (s => NamePrompt.Show(OwnerWindow, "Choose Name", "Enter a name for the table:", s)))("Table" + number);
        if (typed is null) return null;
        newTables = number;
        string name = typed.Trim(), schema = "public";
        int dot = name.IndexOf('.');
        if (dot > 0) { schema = name[..dot]; name = name[(dot + 1)..]; }
        try { ObjectSql.Relation(schema, name); }
        catch (ArgumentException ex) { Say(ex.Message); return null; }
        if (tables.Any(t => t.Schema == schema && t.Name == name)) { Say($"The table {schema}.{name} is already in this diagram."); return null; }
        var design = new TableDesign(0, schema, name, [new DesignColumn(null, "Column1", "text", true)]);
        double offset = 20 + 30 * (cascade++ % 10);
        var table = new DiagramTableModel(design, at?.X ?? offset, at?.Y ?? offset) { View = DiagramTableView.Standard };
        tables.Add(table);
        selected.Clear(); selected.Add(table); selectedRelation = null; selectedAnnotation = null;
        selectedColumns.Clear(); selectedColumns.Add("Column1"); selectedColumnTable = table;
        Rebuild(); Touch(); Say("");
        BeginCellEdit(table, "Column1");
        return table;
    }

    // ---- Text annotations ----
    private Border CreateNote(DiagramAnnotation note)
    {
        var text = new TextBlock { Text = note.Text, TextWrapping = TextWrapping.Wrap };
        var box = new Border { Tag = note, Child = text, Background = Brushes.White, BorderBrush = NoteFrameBrush, BorderThickness = new Thickness(1), Padding = new Thickness(4, 2, 4, 2), MinWidth = 60, MinHeight = 22, MaxWidth = 320, Cursor = Cursors.SizeAll };
        AutomationProperties.SetName(box, "Text annotation");
        return box;
    }

    /// <summary>A new annotation at the point, with the text open for typing; one left empty goes away.</summary>
    public DiagramAnnotation NewAnnotation(Point at)
    {
        CommitEdit();
        var note = new DiagramAnnotation("", Math.Max(0, at.X), Math.Max(0, at.Y));
        annotations.Add(note);
        selected.Clear(); selectedRelation = null; ClearColumnSelection(); selectedAnnotation = note;
        Rebuild(); Touch();
        BeginNoteEdit(note);
        return note;
    }

    /// <summary>Opens the text of an annotation. Enter breaks the line, Ctrl+Enter or leaving it applies, Esc undoes.</summary>
    internal void BeginNoteEdit(DiagramAnnotation note)
    {
        CommitEdit();
        if (!noteBoxes.TryGetValue(note, out var border)) return;
        var shown = (TextBlock)border.Child;
        var box = new TextBox { Text = note.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 120, MinHeight = 18, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        AutomationProperties.SetName(box, "Annotation text");
        border.Child = box;
        OpenEditor(box, text => { SetNoteText(note, text); return true; }, () => border.Child = shown, () => { if (note.Text.Length == 0) DeleteNote(note); });
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { CommitEdit(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelEdit(); Focus(); e.Handled = true; }
        };
    }

    internal void SetNoteText(DiagramAnnotation note, string text)
    {
        if (text.Trim().Length == 0) { DeleteNote(note); return; }
        note.Text = text;
        Rebuild(); Touch();
    }

    public void DeleteNote(DiagramAnnotation note)
    {
        annotations.Remove(note);
        if (selectedAnnotation == note) selectedAnnotation = null;
        Rebuild(); Touch();
    }

    private void MoveNote(Vector offset)
    {
        if (selectedAnnotation is not { } note) return;
        if (Math.Abs(offset.X) + Math.Abs(offset.Y) >= 2) moved = true;
        note.X = Math.Max(0, noteOrigin.X + offset.X); note.Y = Math.Max(0, noteOrigin.Y + offset.Y);
        Position();
    }

    private void EndNoteMove()
    {
        if (moved && selectedAnnotation is { } note)
        {
            var before = TakeSnapshot();
            before.Notes[note] = noteOrigin;
            PushUndo(before);
            Touch();
        }
        moved = false;
    }

    // ---- Page breaks ----
    /// <summary>The printable area of the default printer in diagram units (96 per inch), or a Letter page with 0.5 in margins when there is no printer to ask.</summary>
    private Size ReadPageSize()
    {
        if (PageSizeOverride is { } read) return read();
        try
        {
            var printer = new PrintDialog();
            if (printer.PrintableAreaWidth > 0 && printer.PrintableAreaHeight > 0) return new(printer.PrintableAreaWidth, printer.PrintableAreaHeight);
        }
        catch (Exception ex) when (ex is System.Printing.PrintSystemException or InvalidOperationException or NotSupportedException or COMException or System.ComponentModel.Win32Exception) { }
        return new(720, 960);
    }

    public void TogglePageBreaks()
    {
        showPageBreaks = !showPageBreaks;
        if (showPageBreaks && pageSize is null) pageSize = ReadPageSize();
        DrawPageBreaks(); Touch();
    }

    /// <summary>Asks the printer for the page again and redraws the breaks.</summary>
    public void RecalculatePageBreaks()
    {
        pageSize = ReadPageSize();
        DrawPageBreaks(); Touch();
    }

    private void DrawPageBreaks()
    {
        PageBreaksLayer.Children.Clear();
        if (!showPageBreaks || pageSize is not { Width: > 0, Height: > 0 } page) return;
        double width = Surface.Width, height = Surface.Height, thickness = 1 / zoom;
        Line Dashed(double x1, double y1, double x2, double y2) => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = PageBreakBrush, StrokeThickness = thickness, StrokeDashArray = [4, 4] };
        for (double x = page.Width; x < width; x += page.Width) PageBreaksLayer.Children.Add(Dashed(x, 0, x, height));
        for (double y = page.Height; y < height; y += page.Height) PageBreaksLayer.Children.Add(Dashed(0, y, width, y));
    }

    // ---- Copy Diagram to Clipboard ----
    /// <summary>The tables, lines and annotations as a picture at 100%, without selection colors or page breaks. Null with nothing on the diagram.</summary>
    internal BitmapSource? RenderDiagram()
    {
        CommitEdit();
        if (ContentRects().Length == 0) return null;
        var content = ContentRects().Concat(drawnRoutes.Values.SelectMany(r => r.Points.Select(p => new Rect(p, new Size(0, 0))))).ToArray();
        const double margin = 26, limit = 8192; // room for the key and ∞ at the ends of the lines
        double left = Math.Max(0, content.Min(r => r.Left) - margin), top = Math.Max(0, content.Min(r => r.Top) - margin);
        double width = content.Max(r => r.Right) + margin - left, height = content.Max(r => r.Bottom) + margin - top;
        double scale = Math.Min(1, limit / Math.Max(width, height));

        var (tablesBefore, relationBefore, noteBefore, columnsBefore, columnTableBefore) = (selected.ToArray(), selectedRelation, selectedAnnotation, selectedColumns.ToArray(), selectedColumnTable);
        selected.Clear(); selectedRelation = null; selectedAnnotation = null; ClearColumnSelection();
        RefreshTitles(); RefreshLineSelection(); PaintColumns();
        Surface.UpdateLayout();
        try
        {
            var picture = new DrawingVisual();
            // Whole pixels: a fractional edge would leave a half-transparent row at the border of the picture.
            var area = new Rect(0, 0, Math.Max(1, Math.Ceiling(width * scale)), Math.Max(1, Math.Ceiling(height * scale)));
            using (var context = picture.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, area);
                // The layers keep diagram coordinates whatever the zoom of the surface is.
                foreach (var layer in new Visual[] { LinesLayer, TablesLayer })
                    context.DrawRectangle(new VisualBrush(layer) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(left, top, width, height), ViewportUnits = BrushMappingMode.Absolute, Viewport = area, Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
            }
            var bitmap = new RenderTargetBitmap((int)area.Width, (int)area.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(picture);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            selected.UnionWith(tablesBefore); selectedRelation = relationBefore; selectedAnnotation = noteBefore; selectedColumns.UnionWith(columnsBefore); selectedColumnTable = columnTableBefore;
            RefreshTitles(); RefreshLineSelection(); PaintColumns();
        }
    }

    /// <summary>Copy Diagram to Clipboard. False when there is nothing to copy or another program holds the clipboard.</summary>
    public bool CopyDiagram()
    {
        if (RenderDiagram() is not { } image) { Say("There is nothing on the diagram to copy."); return false; }
        try { (ClipboardOverride ?? Clipboard.SetImage)(image); }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException) { Say("The diagram could not be copied: " + ex.Message); return false; }
        Say("Diagram copied to the clipboard.");
        return true;
    }
}
