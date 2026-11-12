using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Npgsql;
using PGMS.PostgreSql;
using Path = System.Windows.Shapes.Path;

namespace PGMS.Desktop;

public enum DiagramHitPart { Title, Column, Selector, Body, IndexHeader, Index, Warning }
/// <summary>What the mouse is over in a diagram: a part of a table box, with the column when it is a row.</summary>
public sealed record DiagramHit(DiagramTableModel Table, string? Column, DiagramHitPart Part, string? Field = null);
/// <summary>The key (Key = true) or ∞ end of a relationship line, dragged to change the side of the table it meets.</summary>
public sealed record DiagramLineEnd(DiagramRelation Relation, bool Key);

/// <summary>Database diagram as a document tab, like Database Diagrams in SSMS 22.11 (docs/MARCO-M1043.md). Layout and schema changes stay in the tab until Save,
/// which shows the script of any schema change and then writes both in one transaction.</summary>
public partial class DatabaseDiagramView : UserControl
{
    private static readonly double[] ZoomLevels = [4, 2, 1.5, 1, 0.75, 0.5, 0.25, 0.1];
    private const string ToFit = "To Fit";
    private static readonly Brush TitleBrush = Frozen("#DDE3EC"), SelectedTitleBrush = Frozen("#0078D7"), SelectorBrush = Frozen("#F3F3F3"), FrameBrush = Frozen("#7A7A7A"), SelectedLineBrush = Frozen("#0078D7"), LineBrush = Frozen("#505050");

    private readonly string connection;
    private readonly NpgsqlConnectionStringBuilder target;
    private readonly int? openId;
    private readonly List<DiagramTableModel> tables = [];
    private readonly List<TableDesign> dropped = [];
    private readonly Dictionary<DiagramTableModel, (Border Box, Border Title, TextBlock Caption)> boxes = [];
    private readonly HashSet<DiagramTableModel> selected = [];
    private readonly List<(DiagramRelation Relation, Polyline Line)> lineVisuals = [];
    /// <summary>Lines dragged by hand, by foreign key; saved with the layout. The others are routed automatically.</summary>
    private readonly Dictionary<DesignerForeignKeyRow, DiagramManualRoute> manualRoutes = [];
    private readonly Dictionary<DesignerForeignKeyRow, DiagramRouting.Route> drawnRoutes = [];
    private DiagramRelation? dragRelation;
    private DiagramManualRoute? dragRouteStart;
    private int dragSegment;
    private bool? dragEnd;
    private List<(DiagramRelation Relation, DiagramRouting.Route Route)> dragOthers = [];
    private DiagramRelation? selectedRelation;
    private DiagramInfo? info;
    private string name, layoutBaseline = "";
    private bool showLabels, loadedOnce, updatingZoom, showIndexes = true;
    private double zoom = 1;
    private int cascade;
    private CancellationTokenSource? operation;

    private enum DragMode { None, Move, Column, Pan, Line, Marquee, Annotation }
    private static readonly Brush MarqueeFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD7)));
    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
    private Rectangle? marquee;
    private DiagramTableModel[] marqueeBase = [];
    private DiagramTableModel? pressedTable;
    private Point panStart, panOffset;
    private DragMode drag;
    private Point dragStart;
    private bool moved;
    private readonly Dictionary<DiagramTableModel, Point> dragOrigins = [];
    private DiagramHit? dragColumn;
    /// <summary>Rows of the tables by column, for the highlight of the column under the mouse while a relationship is dragged.</summary>
    private readonly Dictionary<(DiagramTableModel Table, string Column), (Grid Row, TextBlock Text)> rowVisuals = [];
    private DiagramHit? dropTarget;
    private object? menuTarget;

    public event Action? TitleChanged;
    /// <summary>Design on a table: the owner opens the Table Designer of that table.</summary>
    public event Action<DiagramTableModel>? DesignRequested;
    /// <summary>After a save; true when tables changed too.</summary>
    public event Action<bool>? Saved;
    internal Func<string, string?>? NamePromptOverride { get; set; }
    internal Func<TableDesignChange, bool>? ReviewPrompt { get; set; }
    internal Func<NewRelationshipDialog, bool>? RelationshipPrompt { get; set; }
    internal Func<string, bool>? ConfirmPrompt { get; set; }
    internal Action<Window>? DialogShown { get; set; }

    public DatabaseDiagramView(string connection, int? id, string name)
    {
        InitializeComponent();
        this.connection = connection; target = new(connection); openId = id; this.name = name;
        AutomationProperties.SetName(this, "Database Diagram");
        TabToolTip = $"{target.Host}:{target.Port} {target.Database}";
        // The zoom can be typed (pedido do usuário, 08/11/2026); no autocomplete, so typing 1 does not jump to 150%.
        ZoomBox.IsEditable = true; ZoomBox.IsReadOnly = false; ZoomBox.IsTextSearchEnabled = false;
        ZoomBox.PreviewKeyDown += ZoomKeyDown; ZoomBox.LostKeyboardFocus += ZoomLostFocus;
        foreach (double level in ZoomLevels) ZoomBox.Items.Add(Percent(level));
        ZoomBox.Items.Add(ToFit);
        PreviewKeyDown += KeyPressed;
        PreviewKeyUp += (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl && drag != DragMode.Pan) ShowPanCursor(false); };
        Surface.MouseLeave += (_, _) => { if (drag != DragMode.Pan) ShowPanCursor(false); };
        Scroller.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            WheelZoom(e.Delta > 0, e.GetPosition(Surface), e.GetPosition(Scroller)); e.Handled = true;
        };
        layoutBaseline = LayoutFingerprint();
        ShowZoom();
    }

    public string TabToolTip { get; }
    public IReadOnlyList<DiagramTableModel> Tables => tables;
    public IReadOnlyCollection<DiagramTableModel> SelectedTables => selected;
    public DiagramRelation? SelectedRelation => selectedRelation;
    public DiagramInfo? Info => info;
    public bool IsNew => info is null && openId is null;
    public bool IsBusy => operation is not null;
    public bool IsDirty { get; private set; }
    public string DiagramName => name;
    public string Title => name + (IsDirty ? "*" : "");
    public string? Database => target.Database;
    public string FeedbackText => Feedback.Text;
    public double Zoom => zoom;
    public bool ShowRelationshipLabels => showLabels;
    public bool HasSchemaChanges => dropped.Count > 0 || tables.Any(t => t.SchemaChanged);
    internal Canvas DiagramSurface => Surface;
    internal Canvas Lines => LinesLayer;
    internal Border BoxOf(DiagramTableModel table) => boxes[table].Box;
    internal Border TitleOf(DiagramTableModel table) => boxes[table].Title;

    /// <summary>Same server, port, user, database and diagram; a new diagram never matches.</summary>
    public bool Targets(string other, int id)
    {
        var candidate = new NpgsqlConnectionStringBuilder(other);
        return (info?.Id ?? openId) == id && string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase) && candidate.Port == target.Port && candidate.Username == target.Username && candidate.Database == target.Database;
    }

    public IReadOnlyList<DiagramRelation> Relations => tables.SelectMany(f => f.ForeignKeys.Select(k => (Foreign: f, Key: k, Primary: tables.FirstOrDefault(p => f.References(p, k)))))
        .Where(r => r.Primary is not null).Select(r => new DiagramRelation(r.Foreign, r.Key, r.Primary!)).ToArray();

    private static Brush Frozen(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static string Percent(double level) => Math.Round(level * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
    private static string Message(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    private Window? OwnerWindow => Window.GetWindow(this);
    private bool Confirm(string text) => ConfirmPrompt?.Invoke(text) ?? MessageBox.Show(OwnerWindow!, text, "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Say(string text, bool cancellable = false)
    {
        Feedback.Text = text;
        FeedbackBar.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CancelOperation(object sender, RoutedEventArgs e) => operation?.Cancel();
    /// <summary>Stops a read or save in progress when the tab closes.</summary>
    public void Shutdown() => operation?.Cancel();

    // ---- Loading ----
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loadedOnce) return;
        loadedOnce = true; Focus();
        if (openId is not null) await LoadAsync();
    }

    /// <summary>Reads the diagram and its tables again; pending changes are discarded (the owner asks first).</summary>
    public async Task LoadAsync()
    {
        if (IsBusy || openId is null && info is null) return;
        using var cancellation = operation = new CancellationTokenSource();
        try
        {
            Say("Reading diagram…", cancellable: true);
            var service = new DiagramService(connection);
            var diagram = await service.ReadAsync(info?.Id ?? openId!.Value, cancellation.Token);
            var read = await service.ReadTablesAndIndexesAsync(diagram.Layout.Tables, [], cancellation.Token);
            var (designs, missing, indexes) = read;
            info = diagram.Info; name = diagram.Info.Name;
            tables.Clear(); dropped.Clear(); selected.Clear(); selectedRelation = null;
            var found = diagram.Layout.Tables.Where(p => !missing.Contains(p.Schema + "." + p.Name)).ToArray();
            for (int i = 0; i < designs.Count && i < found.Length; i++)
            {
                var table = new DiagramTableModel(designs[i], found[i].X, found[i].Y)
                {
                    Catalog = indexes.GetValueOrDefault(designs[i].Oid) ?? DiagramTableIndexes.Empty, CanComment = !read.NotOwned.Contains(designs[i].Oid), SavedDescriptions = found[i].Descriptions
                };
                // A description not applied that the catalog now has was applied by someone else: nothing is pending any more.
                table.Unapplied.AddRange((found[i].Descriptions ?? []).Where(d => !d.Applied && DiagramDescriptions.Text(designs[i], d.Object, d.Name) != (d.Text.Length == 0 ? null : d.Text)));
                ApplyPlacement(table, found[i]); tables.Add(table);
            }
            showIndexes = diagram.Layout.ShowIndexes;
            annotations.Clear(); annotations.AddRange((diagram.Layout.Annotations ?? []).Select(a => new DiagramAnnotation(a.Text, a.X, a.Y)));
            showPageBreaks = diagram.Layout.ShowPageBreaks;
            pageSize = diagram.Layout.PageWidth > 0 && diagram.Layout.PageHeight > 0 ? new Size(diagram.Layout.PageWidth, diagram.Layout.PageHeight) : null;
            ClearColumnSelection(); selectedAnnotation = null;
            zoom = Math.Clamp(diagram.Layout.Zoom, 0.1, 4); showLabels = diagram.Layout.ShowRelationshipLabels;
            ApplyRoutes(diagram.Layout.Relationships ?? []);
            undoStack.Clear(); RefreshUndo();
            Rebuild(); ApplyZoom();
            // Tables dropped elsewhere leave the layout changed, so the tab shows * until the diagram is saved again.
            layoutBaseline = missing.Count == 0 ? LayoutFingerprint() : "";
            string newer = diagram.Layout.Format > DiagramLayout.CurrentFormat ? $"This diagram was saved by a newer version of PGMS (format {diagram.Layout.Format}); saving it here keeps only what this version knows. " : "";
            Say(newer + (missing.Count == 0 ? "" : "Tables no longer in the database were removed from the diagram: " + string.Join(", ", missing) + "."));
        }
        catch (OperationCanceledException) { Say("Reading canceled."); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Say("The diagram could not be read: " + Message(ex)); }
        finally { operation = null; Touch(); }
    }

    // ---- State ----
    /// <param name="unapplied">Descriptions that the save about to run leaves out (not the owner), by table, added to those the tables already keep.</param>
    private DiagramLayout CurrentLayout(IReadOnlyDictionary<uint, IReadOnlyList<DiagramDescription>>? unapplied = null) => new()
    {
        Tables = tables.Select(t => new DiagramTablePlacement(t.Schema, t.Name, Math.Round(t.X), Math.Round(t.Y))
        {
            Oid = t.Original.Oid, View = t.View == DiagramTableView.ColumnNames ? null : t.View.ToString(), CustomColumns = t.CustomColumns.SequenceEqual(DiagramColumnFields.Standard) ? null : t.CustomColumns.ToArray(),
            IndexesExpanded = t.IndexesExpanded,
            // Only the descriptions not applied: the save writes the copy of the others from the catalog, so they never make the tab look changed.
            Descriptions = t.Unapplied.Concat(unapplied?.GetValueOrDefault(t.Original.Oid) ?? []).ToArray() is { Length: > 0 } kept ? DiagramDescriptions.Sorted(kept.Reverse().DistinctBy(d => (d.Object, d.Name))) : null
        }).ToArray(),
        // The indexes are written by DiagramService.SaveAsync from the catalog, so they never make the tab look changed.
        Zoom = zoom, ShowRelationshipLabels = showLabels, ShowIndexes = showIndexes,
        Annotations = annotations.Select(a => new DiagramAnnotationPlacement(a.Text, Math.Round(a.X), Math.Round(a.Y))).ToArray(),
        ShowPageBreaks = showPageBreaks, PageWidth = pageSize?.Width ?? 0, PageHeight = pageSize?.Height ?? 0,
        Relationships = Relations.Where(r => manualRoutes.ContainsKey(r.Key)).Select(r =>
        {
            var m = manualRoutes[r.Key];
            return new DiagramRelationshipRoute(r.Foreign.Schema, r.Foreign.Name, r.Key.Name, m.PrimarySide.ToString(), Math.Round(m.PrimaryOffset), m.ForeignSide.ToString(), Math.Round(m.ForeignOffset), m.Bends.Select(b => Math.Round(b)).ToArray());
        }).ToArray()
    };
    private string LayoutFingerprint() => CurrentLayout().ToJson();

    /// <summary>Lines placed by hand, found by the table that owns the key and the key name; unknown ones are ignored.</summary>
    private void ApplyRoutes(IEnumerable<DiagramRelationshipRoute> saved)
    {
        manualRoutes.Clear();
        foreach (var route in saved)
        {
            if (tables.FirstOrDefault(t => t.Schema == route.Schema && t.Name == route.Table)?.ForeignKeys.FirstOrDefault(k => k.Name == route.Name) is not { } key
                || !Enum.TryParse<DiagramSide>(route.PrimarySide, out var primary) || !Enum.TryParse<DiagramSide>(route.ForeignSide, out var foreign)) continue;
            manualRoutes[key] = new(primary, route.PrimaryOffset, foreign, route.ForeignOffset, route.Bends ?? []);
        }
    }

    // ---- Undo of the layout (pedido do usuário, 08/11/2026): Arrange Tables, moving tables and moving lines record where things were ----
    /// <summary>Positions by table key and hand-placed lines by (table key, foreign key name), so a refresh of the tables does not break it.</summary>
    private sealed record LayoutSnapshot(Dictionary<string, Point> Positions, Dictionary<(string Table, string Key), DiagramManualRoute> Routes, Dictionary<DiagramAnnotation, Point> Notes);
    private readonly List<LayoutSnapshot> undoStack = [];
    private const int UndoLimit = 100;
    private LayoutSnapshot? pendingUndo;
    public bool CanUndo => undoStack.Count > 0;
    internal int UndoDepth => undoStack.Count;

    private LayoutSnapshot TakeSnapshot() => new(
        tables.ToDictionary(t => t.Key, t => new Point(t.X, t.Y)),
        Relations.Where(r => manualRoutes.ContainsKey(r.Key)).ToDictionary(r => (r.Foreign.Key, r.Key.Name), r => manualRoutes[r.Key]),
        annotations.ToDictionary(a => a, a => new Point(a.X, a.Y)));

    private void PushUndo(LayoutSnapshot snapshot)
    {
        undoStack.Add(snapshot);
        if (undoStack.Count > UndoLimit) undoStack.RemoveAt(0);
        RefreshUndo();
    }

    private void RefreshUndo() { if (UndoButton is not null) UndoButton.IsEnabled = CanUndo && !IsBusy; }

    /// <summary>Ctrl+Z and the Undo button: puts tables and hand-placed lines back where they were before the last Arrange Tables or move.
    /// Tables added since keep their place; schema changes (keys, deleted tables) are not undone here.</summary>
    public void Undo()
    {
        if (undoStack.Count == 0 || IsBusy || drag != DragMode.None) { if (undoStack.Count == 0) Say("Nothing to undo."); return; }
        var snapshot = undoStack[^1]; undoStack.RemoveAt(undoStack.Count - 1);
        foreach (var table in tables) if (snapshot.Positions.TryGetValue(table.Key, out var at)) { table.X = at.X; table.Y = at.Y; }
        foreach (var note in annotations) if (snapshot.Notes.TryGetValue(note, out var was)) { note.X = was.X; note.Y = was.Y; }
        manualRoutes.Clear();
        foreach (var relation in Relations)
            if (snapshot.Routes.TryGetValue((relation.Foreign.Key, relation.Key.Name), out var route)) manualRoutes[relation.Key] = route;
        Position(); Touch(); RefreshUndo();
    }

    private void Touch()
    {
        RefreshUndo();
        IsDirty = LayoutFingerprint() != layoutBaseline || HasSchemaChanges;
        RefreshTitles();
        TitleChanged?.Invoke(); CommandManager.InvalidateRequerySuggested();
    }
    private void RefreshState() { CommandManager.InvalidateRequerySuggested(); TitleChanged?.Invoke(); }
    /// <summary>Renamed from Object Explorer while open: the new name and version, so the next Save keeps both; unsaved changes stay.</summary>
    internal void ApplyRename(DiagramInfo renamed)
    {
        if (renamed.Id != (info?.Id ?? openId)) return;
        info = renamed; name = renamed.Name;
        RefreshState();
    }

    /// <summary>A table shows * when its keys, indexes or checks changed, or when a new relationship of another table points at it (both tables get * in SSMS).</summary>
    public bool TableChanged(DiagramTableModel table) => table.SchemaChanged || tables.Any(o => o != table && o.ForeignKeys.Any(k => k.OriginalName is null && o.References(table, k)));
    public string TableTitle(DiagramTableModel table) => table.Caption + (TableChanged(table) ? "*" : "");

    // ---- Drawing ----
    private void Rebuild()
    {
        DropEditor();
        TablesLayer.Children.Clear(); boxes.Clear(); rowVisuals.Clear(); noteBoxes.Clear();
        selected.RemoveWhere(t => !tables.Contains(t));
        if (selectedColumnTable is { } columnTable && !tables.Contains(columnTable)) ClearColumnSelection();
        if (selectedAnnotation is { } note && !annotations.Contains(note)) selectedAnnotation = null;
        if (selectedRelation is { } relation && !Relations.Contains(relation)) selectedRelation = null;
        foreach (var table in tables)
        {
            var parts = CreateBox(table);
            boxes[table] = parts; TablesLayer.Children.Add(parts.Box);
        }
        foreach (var annotation in annotations) { var box = CreateNote(annotation); noteBoxes[annotation] = box; TablesLayer.Children.Add(box); }
        Position();
        PaintColumns();
    }

    private (Border Box, Border Title, TextBlock Caption) CreateBox(DiagramTableModel table)
    {
        var stack = new StackPanel();
        var caption = new TextBlock { Text = TableTitle(table), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var title = new Border { Background = TitleBrush, Padding = new Thickness(5, 2, 8, 3), Child = TitleContent(table, caption), Tag = new DiagramHit(table, null, DiagramHitPart.Title), Cursor = Cursors.SizeAll };
        stack.Children.Add(title);
        var key = table.PrimaryKey.ToHashSet(StringComparer.Ordinal);
        var fields = table.Fields;
        Grid.SetIsSharedSizeScope(stack, true);
        if (fields.Count > 0) stack.Children.Add(FieldHeader(table, fields));
        foreach (var column in table.VisibleColumns)
        {
            var row = new Grid { Height = 18, Background = Brushes.White, Tag = new DiagramHit(table, column.Name, DiagramHitPart.Column) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); row.ColumnDefinitions.Add(fields.Count > 0 ? new() { Width = GridLength.Auto, SharedSizeGroup = "DiagramName" } : new());
            var selector = new Border
            {
                Background = SelectorBrush, BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(0, 0, 1, 0), Tag = new DiagramHit(table, column.Name, DiagramHitPart.Selector),
                ToolTip = "Drag to a column of another table to create a relationship."
            };
            // The whole row starts a relationship (pedido do usuário, 08/11/2026); before, only the selector did.
            row.ToolTip = column.Comment is { } described ? described + "\n\n" + selector.ToolTip : selector.ToolTip;
            if (key.Contains(column.Name)) selector.Child = KeyGlyph();
            var text = new TextBlock { Text = column.Name, Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Tag = new DiagramHit(table, column.Name, DiagramHitPart.Column, DiagramColumnFields.Name) };
            Grid.SetColumn(text, 1);
            row.Children.Add(selector); row.Children.Add(text);
            AddFieldCells(row, table, column, fields, key.Contains(column.Name));
            // The mark of a described column, in a last column shared by the rows, so the marks line up (PGMS-045).
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "DiagramNote" });
            if (MarkFor(table, DiagramDescription.ColumnObject, column.Name) is { } note) { note.Margin = new Thickness(2, 0, 4, 0); Grid.SetColumn(note, row.ColumnDefinitions.Count - 1); row.Children.Add(note); }
            rowVisuals[(table, column.Name)] = (row, text);
            AutomationProperties.SetName(row, column.Name + (key.Contains(column.Name) ? ", primary key" : ""));
            stack.Children.Add(row);
        }
        if (ShowsIndexSection(table)) AddIndexSection(stack, table);
        var box = new Border { BorderBrush = FrameBrush, BorderThickness = new Thickness(FrameWidth), Background = Brushes.White, Child = stack, MinWidth = 130, MaxWidth = fields.Count > 0 ? 760 : 360, SnapsToDevicePixels = true, Tag = new DiagramHit(table, null, DiagramHitPart.Body) };
        AutomationProperties.SetName(box, "Table " + table.Caption);
        return (box, title, caption);
    }

    // Key and ∞ of proposal A, approved by the user on 08/11/2026 (artifacts/acceptance/M1043-icons/propostas.png):
    // a gold key with a gradient, a bow with a hole, a shaft with two teeth and a dark outline; a real ∞ instead of two circles.
    private static readonly Brush KeyGold = FrozenGradient("#FFE68A", "#D9A400"), KeyShine = Frozen("#FFF6CC");
    private static readonly Pen KeyOutline = FrozenPen("#6E4B00", 0.9);
    private static Brush FrozenGradient(string from, string to)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), 90);
        brush.Freeze(); return brush;
    }
    private static Pen FrozenPen(string color, double width) { var pen = new Pen(Frozen(color), width); pen.Freeze(); return pen; }
    private static Geometry Union(params Geometry[] parts) => parts.Aggregate((a, b) => Geometry.Combine(a, b, GeometryCombineMode.Union, null));
    private static Geometry Rounded(double x, double y, double w, double h, double r) => new RectangleGeometry(new Rect(x, y, w, h), r, r);
    private static Path KeyPath(Geometry shape) => new() { Data = shape, Fill = KeyGold, Stroke = KeyOutline.Brush, StrokeThickness = KeyOutline.Thickness };

    /// <summary>The primary key mark in the row selector: an upright key, 10 × 15.</summary>
    internal static Canvas KeyGlyph()
    {
        var canvas = new Canvas { Width = 10, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var bow = Geometry.Combine(new EllipseGeometry(new Point(5, 4.5), 4.2, 4.2), new EllipseGeometry(new Point(5, 3.9), 1.3, 1.3), GeometryCombineMode.Exclude, null);
        canvas.Children.Add(KeyPath(Union(bow, Rounded(4, 8, 2.2, 6.2, 0.4), Rounded(6, 10.6, 2.4, 1.4, 0.3), Rounded(6, 12.6, 1.8, 1.4, 0.3))));
        canvas.Children.Add(new Path { Data = new EllipseGeometry(new Point(3.6, 2.9), 0.9, 0.9), Fill = KeyShine });
        AutomationProperties.SetName(canvas, "Primary key");
        return canvas;
    }

    /// <summary>The ∞ sign as one curve (a lemniscate), centered at x with half width a.</summary>
    private static Geometry Lemniscate(double a, double x)
    {
        var figure = new StreamGeometry();
        using (var context = figure.Open())
            for (int i = 0; i <= 120; i++)
            {
                double t = 2 * Math.PI * i / 120, d = 1 + Math.Pow(Math.Sin(t), 2);
                var p = new Point(x + a * Math.Cos(t) / d, a * Math.Sin(t) * Math.Cos(t) / d);
                if (i == 0) context.BeginFigure(p, false, true); else context.LineTo(p, true, true);
            }
        figure.Freeze();
        return figure;
    }

    private void RefreshTitles()
    {
        foreach (var (table, (_, title, caption)) in boxes)
        {
            bool on = selected.Contains(table);
            title.Background = on ? SelectedTitleBrush : TitleBrush;
            caption.Foreground = on ? Brushes.White : Brushes.Black;
            caption.Text = TableTitle(table);
        }
    }

    /// <summary>The end of a relationship, drawn along +x from the table border and turned to the direction the line leaves the table,
    /// as SSMS 22.11 turns the key and ∞ with the line (user's capture of 07/11/2026).</summary>
    internal static Canvas EndGlyph(Point at, Vector outward, bool key, DiagramRelation? relation)
    {
        // The key and ∞ can be dragged to any side of their table (pedido do usuário, 08/11/2026). Without a relation it is the preview of a drag.
        var glyph = new Canvas { Tag = relation is null ? null : new DiagramLineEnd(relation, key), Cursor = Cursors.SizeAll, ToolTip = "Drag to another side of the table to change where the line meets it.", RenderTransform = new RotateTransform(Math.Atan2(outward.Y, outward.X) * 180 / Math.PI) };
        var grip = new Rectangle { Width = 20, Height = 12, Fill = Brushes.Transparent };
        Canvas.SetTop(grip, -6); glyph.Children.Add(grip);
        if (key)
        {
            // Bow next to the table, shaft and two teeth along the line.
            var bow = Geometry.Combine(new EllipseGeometry(new Point(5.2, 0), 4.4, 4.4), new EllipseGeometry(new Point(4.6, -0.4), 1.3, 1.3), GeometryCombineMode.Exclude, null);
            glyph.Children.Add(KeyPath(Union(bow, Rounded(9, -1.2, 10, 2.4, 0.5), Rounded(14, 1, 1.6, 2.8, 0.3), Rounded(17, 1, 1.6, 2.2, 0.3))));
            glyph.Children.Add(new Path { Data = new EllipseGeometry(new Point(3.6, -1.9), 0.9, 0.9), Fill = KeyShine });
        }
        else
        {
            // A white patch hides the double line under the ∞.
            var patch = new Rectangle { Width = 13.4, Height = 8.4, Fill = Brushes.White };
            Canvas.SetLeft(patch, 0.8); Canvas.SetTop(patch, -4.2); glyph.Children.Add(patch);
            glyph.Children.Add(new Path { Data = Lemniscate(6.2, 7.5), Stroke = Brushes.Black, StrokeThickness = 1.5 });
        }
        AutomationProperties.SetName(glyph, key ? "Primary key end" : "Foreign key end");
        Canvas.SetLeft(glyph, at.X); Canvas.SetTop(glyph, at.Y);
        return glyph;
    }

    private void RefreshLineSelection()
    {
        foreach (var (relation, line) in lineVisuals)
        {
            bool on = relation == selectedRelation;
            line.Stroke = on ? SelectedLineBrush : LineBrush;
        }
    }

    private Rect Bounds(DiagramTableModel table)
    {
        var box = boxes[table].Box;
        if (box.DesiredSize.Width == 0) box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new(table.X, table.Y, box.DesiredSize.Width, box.DesiredSize.Height);
    }

    private void Position()
    {
        foreach (var (table, (box, _, _)) in boxes) { Canvas.SetLeft(box, table.X); Canvas.SetTop(box, table.Y); box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); }
        foreach (var (annotation, box) in noteBoxes) { Canvas.SetLeft(box, annotation.X); Canvas.SetTop(box, annotation.Y); box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); }
        DrawLines();
        var content = ContentRects();
        double right = content.Length == 0 ? 0 : content.Max(r => r.Right), bottom = content.Length == 0 ? 0 : content.Max(r => r.Bottom);
        Surface.Width = Math.Max(1600, right + 300); Surface.Height = Math.Max(1200, bottom + 300);
        DrawPageBreaks();
        RefreshTitles();
    }

    private void DrawLines()
    {
        LinesLayer.Children.Clear(); lineVisuals.Clear(); drawnRoutes.Clear();
        var relations = Relations;
        // Lines placed by hand keep their shape; the others are laid out together as before.
        var automatic = relations.Where(r => !manualRoutes.ContainsKey(r.Key)).ToArray();
        var laid = DiagramRouting.Layout(automatic.Select(r => new DiagramRouting.Request(r.Primary, Bounds(r.Primary), r.Foreign, Bounds(r.Foreign))).ToArray(), tables.Select(Bounds).ToArray());
        for (int i = 0; i < automatic.Length; i++) drawnRoutes[automatic[i].Key] = laid[i];
        foreach (var relation in relations)
            if (manualRoutes.TryGetValue(relation.Key, out var manual)) drawnRoutes[relation.Key] = DiagramRouting.Build(manual, Bounds(relation.Primary), Bounds(relation.Foreign)).Route;
        {
            for (int i = 0; i < relations.Count; i++)
            {
                var relation = relations[i];
                var route = drawnRoutes[relation.Key];
                bool on = relation == selectedRelation;
                var points = new PointCollection(route.Points);
                // SSMS draws relationships as a double line: a dark 4 px stroke with a white 2 px core.
                var visible = new Polyline { Points = points, Stroke = on ? SelectedLineBrush : LineBrush, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Miter, IsHitTestVisible = false };
                var core = new Polyline { Points = points, Stroke = Brushes.White, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Miter, IsHitTestVisible = false };
                LinesLayer.Children.Add(visible); LinesLayer.Children.Add(core); lineVisuals.Add((relation, visible));
                var hit = new Polyline { Points = points, Stroke = Brushes.Transparent, StrokeThickness = 9, Tag = relation, ToolTip = RelationToolTip(relation) };
                AutomationProperties.SetName(hit, "Relationship " + relation.Key.Name);
                LinesLayer.Children.Add(hit);
                // Each segment can be dragged sideways; the cursor shows the direction (inferred: the SSMS cursor was not captured).
                for (int k = 0; k + 1 < route.Points.Count; k++)
                {
                    Point p = route.Points[k], q = route.Points[k + 1];
                    if (p == q) continue;
                    LinesLayer.Children.Add(new Line
                    {
                        X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y, Stroke = Brushes.Transparent, StrokeThickness = 9, Tag = relation, ToolTip = hit.ToolTip,
                        Cursor = Math.Abs(p.Y - q.Y) < 0.5 ? Cursors.SizeNS : Cursors.SizeWE
                    });
                }
                // Key at the primary key table, ∞ at the foreign key table.
                LinesLayer.Children.Add(EndGlyph(route.Points[0], route.PrimaryOut, key: true, relation));
                LinesLayer.Children.Add(EndGlyph(route.Points[^1], route.ForeignOut, key: false, relation));
                DrawRelationMark(relation, route);
                if (showLabels)
                {
                    int middle = route.Points.Count / 2;
                    var at = new Point((route.Points[middle - 1].X + route.Points[middle].X) / 2, (route.Points[middle - 1].Y + route.Points[middle].Y) / 2);
                    var label = new TextBlock { Text = relation.Key.Name, Background = Brushes.White, FontSize = 11, Padding = new Thickness(2, 0, 2, 0), IsHitTestVisible = false };
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(label, at.X - label.DesiredSize.Width / 2); Canvas.SetTop(label, at.Y - label.DesiredSize.Height - 2); LinesLayer.Children.Add(label);
                }
            }
        }
    }

    // ---- Zoom ----
    private void ShowZoom() { updatingZoom = true; ZoomBox.Text = Percent(zoom); ZoomBox.SelectedItem = ZoomBox.Items.Contains(Percent(zoom)) ? Percent(zoom) : null; ZoomBox.Text = Percent(zoom); updatingZoom = false; }
    private void ApplyZoom()
    {
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = zoom;
        foreach (var (box, _, _) in boxes.Values) box.BorderThickness = new Thickness(FrameWidth);
        DrawPageBreaks();
        ShowZoom();
    }
    /// <summary>The table frame is at least one screen pixel: below 100% a 1 unit border is thinner than a pixel and vanished at some positions
    /// (pedido do usuário, 08/11/2026, table Sorteios at 92%).</summary>
    private double FrameWidth => Math.Max(1, 1 / zoom);
    private void ZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingZoom || ZoomBox.SelectedItem is not string choice) return;
        if (choice == ToFit) FitZoom();
        else SetZoom(double.Parse(choice.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100);
        // The combo box finishes its own selection after this handler: the percentage is shown afterwards, or To Fit left the box empty (pedido do usuário, 08/11/2026).
        Dispatcher.BeginInvoke(ShowZoom, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A zoom typed in the box (pedido do usuário, 08/11/2026): "125", "125%" or "62,5%", from 10% to 400%. Anything else puts the current zoom back.</summary>
    internal bool ApplyTypedZoom(string text)
    {
        string number = text.Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (string.Equals(text.Trim(), ToFit, StringComparison.OrdinalIgnoreCase)) { FitZoom(); return true; }
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent) || double.IsNaN(percent) || percent <= 0)
        { ShowZoom(); return false; }
        SetZoom(percent / 100);
        return true;
    }
    private void ZoomKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyTypedZoom(ZoomBox.Text); Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ShowZoom(); Focus(); e.Handled = true; }
    }
    private void ZoomLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ZoomBox.IsKeyboardFocusWithin && ZoomBox.Text != Percent(zoom)) ApplyTypedZoom(ZoomBox.Text);
    }

    public void SetZoom(double level) { zoom = Math.Clamp(level, 0.1, 4); ApplyZoom(); Touch(); }
    /// <summary>To Fit: the tables, from the leftmost and topmost one, fill the visible area (up to 400%), and the view scrolls to them.
    /// It used to count from the corner of the surface and stop at 100%, which left much empty space (pedido do usuário, 08/11/2026).</summary>
    public void FitZoom()
    {
        var content = ContentRects();
        if (content.Length == 0) { SetZoom(1); return; }
        const double margin = 20;
        double left = Math.Max(0, content.Min(r => r.Left) - margin), top = Math.Max(0, content.Min(r => r.Top) - margin);
        double right = content.Max(r => r.Right) + margin, bottom = content.Max(r => r.Bottom) + margin;
        // The whole area of the tab: scroll bars may go away once the diagram fits.
        double width = Scroller.ActualWidth > 0 ? Scroller.ActualWidth : ActualWidth, height = Scroller.ActualHeight > 0 ? Scroller.ActualHeight : ActualHeight;
        SetZoom(width <= 0 || height <= 0 ? 1 : Math.Min(width / (right - left), height / (bottom - top)));
        Scroller.UpdateLayout();
        // Still needs scroll bars after rounding: fit inside them.
        if (Scroller.ScrollableWidth > 0.5 || Scroller.ScrollableHeight > 0.5)
        {
            SetZoom(Math.Min(Scroller.ViewportWidth / (right - left), Scroller.ViewportHeight / (bottom - top)));
            Scroller.UpdateLayout();
        }
        Scroller.ScrollToHorizontalOffset(left * zoom); Scroller.ScrollToVerticalOffset(top * zoom);
    }

    // ---- Mouse ----
    private object? HitAt(Point point)
    {
        DependencyObject? found = null;
        VisualTreeHelper.HitTest(Surface, null, result => { found = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        for (var element = found; element is not null && element != Surface; element = VisualTreeHelper.GetParent(element))
            if (element is FrameworkElement { Tag: DiagramHit or DiagramRelation or DiagramLineEnd or DiagramAnnotation or DiagramDescriptionHit } tagged) return tagged.Tag;
        return null;
    }

    /// <summary>Selects what is under the point, as a left or right click does.</summary>
    internal object? SelectAt(Point point, bool add = false)
    {
        var hit = HitAt(point);
        // An end of a line selects the line, like the line itself.
        if (hit is DiagramLineEnd end) hit = end.Relation;
        // The mark of a description selects what it describes.
        if (hit is DiagramDescriptionHit described)
            hit = described.Object == DiagramDescription.ForeignKeyObject ? Relations.FirstOrDefault(r => r.Foreign == described.Table && r.Key.Name == described.Name)
                : described.Object == DiagramDescription.ColumnObject ? new DiagramHit(described.Table, described.Name, DiagramHitPart.Column) : new DiagramHit(described.Table, null, DiagramHitPart.Title);
        if (!add || hit is DiagramAnnotation) selectedAnnotation = null;
        switch (hit)
        {
            case DiagramHit table:
                selectedRelation = null;
                // Shift on another row of the table whose rows are selected adds that row, so a primary key can have several columns.
                if (add && table.Column is { } extra && selectedColumnTable == table.Table && selected.Count == 1 && selected.Contains(table.Table)) { if (!selectedColumns.Remove(extra)) selectedColumns.Add(extra); break; }
                if (add) { if (!selected.Remove(table.Table)) selected.Add(table.Table); ClearColumnSelection(); }
                else
                {
                    if (!selected.Contains(table.Table)) { selected.Clear(); selected.Add(table.Table); }
                    SelectColumnFromClick(table);
                }
                break;
            case DiagramRelation relation: selected.Clear(); selectedRelation = relation; ClearColumnSelection(); break;
            case DiagramAnnotation note: selected.Clear(); selectedRelation = null; selectedAnnotation = note; ClearColumnSelection(); break;
            default: if (!add) { selected.Clear(); selectedRelation = null; ClearColumnSelection(); } break;
        }
        // Only colors change: redrawing would remove the line under the mouse and the context menu would not open on it.
        RefreshTitles(); RefreshLineSelection(); PaintColumns();
        return hit;
    }

    private void SurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var point = e.GetPosition(Surface);
        // Ctrl held: the hand drags the visible area (pedido do usuário, 07/11/2026); a Ctrl+click that does not move still adds or removes a table from the selection.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            pressedTable = null;
            drag = DragMode.Pan; dragStart = point; moved = false; panStart = e.GetPosition(Scroller); panOffset = new(Scroller.HorizontalOffset, Scroller.VerticalOffset);
            ShowPanCursor(true, dragging: true); Surface.CaptureMouse(); e.Handled = true; return;
        }
        Press(point, e.ClickCount, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    /// <summary>The left button goes down. Shift adds to the selection: a table toggles, and a drag from the empty area adds what it touches (pedido do usuário, 08/11/2026).</summary>
    internal void Press(Point point, int clicks = 1, bool extend = false)
    {
        pressedTable = null;
        CancelSlowClick();
        var end = HitAt(point) as DiagramLineEnd;
        var selectedBefore = selectedColumnTable is { } before && selectedColumns.Count == 1 ? (before, selectedColumns.First()) : default((DiagramTableModel, string)?);
        // A click on the mark of a description opens it after the click is over (PGMS-045).
        if (!extend && HitAt(point) is DiagramDescriptionHit described) { SelectAt(point); drag = DragMode.None; if (clicks == 1) Dispatcher.BeginInvoke(() => EditDescription(described)); return; }
        var hit = SelectAt(point, extend);
        pendingCell = clicks == 1 && !extend && hit is DiagramHit { Column: { } clicked, Part: DiagramHitPart.Column } cell && selectedBefore == (cell.Table, clicked) ? cell : null;
        // The Indexes header opens and closes on a click (a second click of a double click does nothing); the warning opens Find Unindexed Foreign Keys
        // after the click is over, so the window never opens under a pressed button.
        if (!extend && hit is DiagramHit { Part: DiagramHitPart.IndexHeader } header) { drag = DragMode.None; if (clicks == 1) ToggleIndexSection(header.Table); return; }
        if (!extend && hit is DiagramHit { Part: DiagramHitPart.Warning } warned) { drag = DragMode.None; if (clicks == 1) Dispatcher.BeginInvoke(() => RequestUnindexedForeignKeys(warned.Table)); return; }
        // Double click on a line opens its properties (pedido do usuário, 08/11/2026), as Alt+Enter does.
        if (clicks == 2 && DoubleClick(hit)) { drag = DragMode.None; return; }
        dragStart = point; moved = false; dragOrigins.Clear(); drag = DragMode.None;
        if (extend && hit is not null) return;
        pressedTable = (hit as DiagramHit)?.Table;
        if (hit is DiagramHit { Column: not null, Part: DiagramHitPart.Selector or DiagramHitPart.Column } column) { drag = DragMode.Column; StartColumnDrag(column, point); }
        // An index row moves the table like its title: it is read only, so there is nothing else to drag.
        else if (hit is DiagramHit { Part: DiagramHitPart.Title or DiagramHitPart.Index } && selected.Count > 0) { drag = DragMode.Move; foreach (var table in selected) dragOrigins[table] = new(table.X, table.Y); }
        else if (hit is DiagramRelation relation && StartLineDrag(relation, point, end?.Key)) drag = DragMode.Line;
        else if (hit is DiagramAnnotation note) { drag = DragMode.Annotation; noteOrigin = new(note.X, note.Y); }
        else if (hit is null) { drag = DragMode.Marquee; marqueeBase = selected.ToArray(); }
        if (drag != DragMode.None) Surface.CaptureMouse();
    }

    /// <summary>A double click on a line or its key/∞ end opens the relationship properties; on a table, Table Properties (pedidos do usuário, 08/11/2026).
    /// False leaves the click to the drag code.</summary>
    internal bool DoubleClick(object? hit)
    {
        if (hit is DiagramLineEnd end) hit = end.Relation;
        switch (hit)
        {
            case DiagramRelation relation: selected.Clear(); selectedRelation = relation; break;
            case DiagramHit { Part: DiagramHitPart.Index, Field: { } index } row:
                // A double click on an index opens Indexes/Keys on it, the dialog where indexes are changed.
                selectedRelation = null; selected.Clear(); selected.Add(row.Table); RefreshTitles(); RefreshLineSelection();
                ShowIndexes(row.Table, index);
                return true;
            case DiagramHit table: selectedRelation = null; selected.Clear(); selected.Add(table.Table); break;
            case DiagramAnnotation note: BeginNoteEdit(note); return true;
            default: return false;
        }
        RefreshTitles(); RefreshLineSelection();
        ShowProperties();
        return true;
    }

    private void SurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (drag is not (DragMode.Pan or DragMode.Column or DragMode.Marquee)) ShowPanCursor(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (drag == DragMode.None || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(Surface);
        if (drag == DragMode.Pan) { Pan(e.GetPosition(Scroller) - panStart); return; }
        DragTo(point);
    }

    /// <summary>The mouse moves with the left button down.</summary>
    internal void DragTo(Point point)
    {
        if (drag == DragMode.Move) MoveSelection(point - dragStart);
        else if (drag == DragMode.Line) DragLineTo(point);
        else if (drag == DragMode.Column) DragColumnTo(point);
        else if (drag == DragMode.Marquee) MarqueeTo(point);
        else if (drag == DragMode.Annotation) MoveNote(point - dragStart);
    }

    private void SurfaceMouseUp(object sender, MouseButtonEventArgs e) => Release(e.GetPosition(Surface));

    /// <summary>Another window took the mouse in the middle of the drag: the rectangle must not stay on the diagram.</summary>
    private void SurfaceLostCapture(object sender, MouseEventArgs e)
    {
        if (drag == DragMode.Marquee) { drag = DragMode.None; EndMarquee(); }
    }

    /// <summary>The left button goes up.</summary>
    internal void Release(Point point)
    {
        var mode = drag; drag = DragMode.None;
        bool dragged = moved; var pressed = pressedTable; pressedTable = null;
        Surface.ReleaseMouseCapture();
        if (mode == DragMode.Move) EndMove();
        if (mode == DragMode.Line) EndLineDrag();
        if (mode == DragMode.Pan && !moved) SelectAt(point, add: true);
        if (mode == DragMode.Pan) ShowPanCursor(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (mode == DragMode.Column) EndColumnDrag(point);
        if (mode == DragMode.Annotation) EndNoteMove();
        if (mode == DragMode.Column && !dragged && pendingCell is { } slow) ScheduleCellEdit(slow);
        pendingCell = null;
        if (mode == DragMode.Marquee) EndMarquee();
        // A click that did not drag, on one of several selected tables, leaves only that table selected; pressing keeps them all so they can be moved together.
        if (pressed is not null && !dragged && selected.Count > 1 && selected.Contains(pressed)) { selected.Clear(); selected.Add(pressed); RefreshTitles(); }
    }

    /// <summary>Dragging from the empty area selects every table the rectangle touches, as it grows (pedido do usuário, 08/11/2026). Touching rather than enclosing is inferred: SSMS was not checked.</summary>
    private void MarqueeTo(Point point)
    {
        if (!moved && Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) < 3) return;
        moved = true;
        var area = new Rect(dragStart, new Point(Math.Clamp(point.X, 0, Surface.Width), Math.Clamp(point.Y, 0, Surface.Height)));
        if (marquee is null)
        {
            marquee = new Rectangle { Stroke = SelectedLineBrush, Fill = MarqueeFill };
            DragLayer.Children.Add(marquee);
            selectedRelation = null; RefreshLineSelection();
        }
        marquee.StrokeThickness = 1 / zoom;
        Canvas.SetLeft(marquee, area.X); Canvas.SetTop(marquee, area.Y); marquee.Width = area.Width; marquee.Height = area.Height;
        selected.Clear(); selected.UnionWith(marqueeBase); selected.UnionWith(tables.Where(t => Bounds(t).IntersectsWith(area)));
        RefreshTitles();
    }

    /// <summary>Takes the rectangle away. Cancelled (Esc), the selection goes back to what it was when the drag started.</summary>
    internal void EndMarquee(bool cancel = false)
    {
        if (marquee is not null) { DragLayer.Children.Remove(marquee); marquee = null; }
        if (cancel) { selected.Clear(); selected.UnionWith(marqueeBase); RefreshTitles(); }
    }
    internal bool IsMarqueeShown => marquee is not null;

    /// <summary>Ctrl + wheel (pedido do usuário, 07/11/2026): the next zoom level of the list, keeping the point under the mouse where it is.</summary>
    internal void WheelZoom(bool zoomIn, Point onSurface, Point inViewport)
    {
        var levels = ZoomLevels.OrderBy(l => l).ToArray();
        double next = zoomIn ? levels.FirstOrDefault(l => l > zoom + 0.001, levels[^1]) : levels.LastOrDefault(l => l < zoom - 0.001, levels[0]);
        if (Math.Abs(next - zoom) < 0.001) return;
        SetZoom(next);
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(Math.Max(0, onSurface.X * zoom - inViewport.X));
        Scroller.ScrollToVerticalOffset(Math.Max(0, onSurface.Y * zoom - inViewport.Y));
    }

    /// <summary>Scrolls the visible area with the hand: the diagram follows the mouse.</summary>
    internal void Pan(Vector offset)
    {
        if (Math.Abs(offset.X) + Math.Abs(offset.Y) >= 2) moved = true;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, panOffset.X - offset.X));
        Scroller.ScrollToVerticalOffset(Math.Max(0, panOffset.Y - offset.Y));
    }
    /// <summary>The open hand over the whole surface, above the move and drag cursors of the tables, while Ctrl is held; the closed hand while it drags.</summary>
    internal void ShowPanCursor(bool on, bool dragging = false)
    {
        var cursor = !on ? null : dragging ? HandCursors.Closed : HandCursors.Open;
        if (Surface.Cursor == cursor && Surface.ForceCursor == on) return;
        Surface.Cursor = cursor; Surface.ForceCursor = on;
    }
    internal bool IsPanCursorShown => Surface.ForceCursor && Surface.Cursor == HandCursors.Open;
    internal bool IsGrabCursorShown => Surface.ForceCursor && Surface.Cursor == HandCursors.Closed;

    /// <summary>Moves the selected tables from where the drag started; never above or left of the surface.</summary>
    internal void MoveSelection(Vector offset)
    {
        if (dragOrigins.Count == 0) foreach (var table in selected) dragOrigins[table] = new(table.X, table.Y);
        if (Math.Abs(offset.X) + Math.Abs(offset.Y) >= 2) moved = true;
        foreach (var (table, origin) in dragOrigins) { table.X = Math.Max(0, origin.X + offset.X); table.Y = Math.Max(0, origin.Y + offset.Y); }
        Position();
    }
    internal void EndMove()
    {
        if (moved && dragOrigins.Count > 0)
        {
            // Where the moved tables were before the drag; the others did not move.
            var before = TakeSnapshot();
            foreach (var (table, origin) in dragOrigins) before.Positions[table.Key] = origin;
            PushUndo(before);
        }
        dragOrigins.Clear(); if (moved) Touch(); moved = false;
    }

    // ---- Lines placed by hand (SSMS 22.11, pilotagem de 07/11/2026) ----
    /// <summary>The line as drawn now: points between the tables where they are.</summary>
    internal IReadOnlyList<Point> RoutePoints(DiagramRelation relation) => drawnRoutes.TryGetValue(relation.Key, out var route) ? route.Points : [];
    internal bool IsPlacedByHand(DiagramRelation relation) => manualRoutes.ContainsKey(relation.Key);

    /// <summary>Starts dragging the segment of the line nearest to the point, or the key (endKey true) or ∞ end. The line keeps its automatic shape until it really moves.</summary>
    internal bool StartLineDrag(DiagramRelation relation, Point point, bool? endKey = null)
    {
        if (!drawnRoutes.TryGetValue(relation.Key, out var route) || route.Points.Count < 2) return false;
        dragRelation = relation; dragStart = point; moved = false; dragEnd = endKey;
        dragOthers = Relations.Where(r => r != relation && !manualRoutes.ContainsKey(r.Key) && drawnRoutes.ContainsKey(r.Key)).Select(r => (r, drawnRoutes[r.Key])).ToList();
        dragSegment = DiagramRouting.SegmentAt(route.Points, point);
        dragRouteStart = manualRoutes.TryGetValue(relation.Key, out var manual) ? manual : DiagramRouting.FromRoute(route, Bounds(relation.Primary), Bounds(relation.Foreign));
        pendingUndo = TakeSnapshot();
        return true;
    }
    internal void DragLineTo(Point point)
    {
        if (dragRelation is not { } relation || dragRouteStart is null) return;
        if (!moved && Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) < 2) return;
        moved = true;
        Rect primary = Bounds(relation.Primary), foreign = Bounds(relation.Foreign);
        // Lines up with a parallel segment within 6 screen pixels, so the step between them goes away (pedido do usuário, 08/11/2026).
        manualRoutes[relation.Key] = dragEnd is { } key
            ? DiagramRouting.MoveEnd(dragRouteStart, primary, foreign, key, point)
            : DiagramRouting.DragSegment(dragRouteStart, primary, foreign, dragSegment, point, snap: 6 / zoom);
        DrawLines();
        // The other lines stay as they were (pedido do usuário, 08/11/2026): an automatic line that the new layout would move keeps its shape from before the drag.
        bool froze = false;
        foreach (var (other, before) in dragOthers)
        {
            if (manualRoutes.ContainsKey(other.Key) || !drawnRoutes.TryGetValue(other.Key, out var now) || now.Points.SequenceEqual(before.Points)) continue;
            manualRoutes[other.Key] = DiagramRouting.FromRoute(before, Bounds(other.Primary), Bounds(other.Foreign));
            froze = true;
        }
        if (froze) DrawLines();
    }
    internal void EndLineDrag()
    {
        dragRelation = null; dragRouteStart = null;
        if (moved && pendingUndo is { } before) PushUndo(before);
        pendingUndo = null;
        if (moved) Touch();
        moved = false;
    }

    private void SurfaceRightDown(object sender, MouseButtonEventArgs e) { Focus(); menuPoint = e.GetPosition(Surface); menuTarget = SelectAt(menuPoint); }
    private void SurfaceMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (drag != DragMode.None) { e.Handled = true; return; }
        FillMenu(SurfaceMenu, menuTarget);
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (editor is not null) return; // the keys belong to the text being typed
        if (ZoomBox.IsKeyboardFocusWithin) return; // Ctrl+A and the rest belong to the zoom text while typing
        if (key == Key.Escape && drag == DragMode.Column) { drag = DragMode.None; Surface.ReleaseMouseCapture(); CancelColumnDrag(); e.Handled = true; return; }
        if (key == Key.Escape && drag == DragMode.Marquee) { drag = DragMode.None; Surface.ReleaseMouseCapture(); EndMarquee(cancel: true); e.Handled = true; return; }
        if (drag is DragMode.Column or DragMode.Marquee) return;
        if (key is Key.LeftCtrl or Key.RightCtrl && Surface.IsMouseOver) ShowPanCursor(true);
        if (key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SelectAll(); e.Handled = true; }
        else if (key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
        else if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt) { ShowProperties(); e.Handled = true; }
        else if (key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None) { if (selectedAnnotation is { } note) BeginNoteEdit(note); else if (selectedColumnTable is { } table && selectedColumns.Count == 1) BeginCellEdit(table, selectedColumns.First()); e.Handled = true; }
        else if (key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && selectedAnnotation is { } deleted) { DeleteNote(deleted); e.Handled = true; }
    }

    // ---- Menus (SSMS 22.11 captures m1–m4 of M1043) ----
    /// <summary>Fills the menu for a table, a relationship, an annotation or the empty area.</summary>
    internal ContextMenu FillMenu(ContextMenu menu, object? target)
    {
        menu.Items.Clear();
        MenuItem Add(string header, Action click, string name, string? icon = null, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Name = name, IsEnabled = enabled, InputGestureText = gesture ?? "" };
            if (icon is not null) item.Icon = FindResource(icon);
            item.Click += (_, _) => click();
            menu.Items.Add(item); return item;
        }
        void Separator() => menu.Items.Add(new Separator());
        switch (target)
        {
            case DiagramHit { Table: var table }:
                bool single = selected.Count == 1;
                var viewMenu = new MenuItem { Header = "Table View", Name = "DiagramTableView" };
                FillTableViewItems(viewMenu.Items, table);
                menu.Items.Add(viewMenu);
                Separator();
                bool columns = selectedColumnTable == table && selectedColumns.Count > 0 && CanEditColumns(table);
                // The column items are grey on the title, as in SSMS; a table with no columns still needs a way to get one.
                Add(PrimaryKeyHeader(table), TogglePrimaryKey, "DiagramSetPrimaryKey", enabled: columns);
                Add("Insert Column", () => InsertColumn(table), "DiagramInsertColumn", enabled: columns || CanEditColumns(table) && table.Columns.Count == 0);
                Add("Delete Column", DeleteSelectedColumns, "DiagramDeleteColumn", enabled: columns);
                Separator();
                Add("Delete Tables from Database", DeleteSelectedTables, "DiagramDeleteTables", "DesignerIconDelete", !IsBusy);
                Add("Remove from Diagram", RemoveSelectedTables, "DiagramRemoveTables");
                Separator();
                Add("Add Related Tables", () => _ = AddRelatedTablesAsync(), "DiagramAddRelated", enabled: !IsBusy);
                Separator();
                Add("Relationships…", () => ShowRelationships(table), "DiagramRelationships", "DesignerIconRelationships", single);
                string? onIndex = target is DiagramHit { Part: DiagramHitPart.Index, Field: var clickedIndex } ? clickedIndex : null;
                Add("Indexes/Keys…", () => ShowIndexes(table, onIndex), "DiagramIndexes", "DesignerIconIndexes", single);
                Add("Check Constraints…", () => ShowChecks(table), "DiagramChecks", "DesignerIconChecks", single);
                // Only on a table with the warning (PGMS-044); not in the SSMS menu.
                if (ShowsWarning(table)) Add("Find Unindexed Foreign Keys…", () => RequestUnindexedForeignKeys(table), "DiagramUnindexedForeignKeys");
                Separator();
                // SSMS edits descriptions in the Properties window; the diagram of PGMS edits them here (PGMS-045).
                Add("Table Description…", () => EditDescription(table, DiagramDescription.TableObject, null), "DiagramTableDescription", enabled: single && !IsBusy);
                if (target is DiagramHit { Column: { } describedColumn, Part: DiagramHitPart.Column or DiagramHitPart.Selector } && table.Column(describedColumn) is not null)
                    Add("Column Description…", () => EditDescription(table, DiagramDescription.ColumnObject, describedColumn), "DiagramColumnDescription", enabled: !IsBusy);
                if (table.Unapplied.Count > 0) Add("Script Unapplied Descriptions…", () => ShowUnappliedScript(table), "DiagramUnappliedDescriptions");
                Separator();
                Add("Generate Change Script…", ShowChangeScript, "DiagramScript", "DesignerIconScript", HasSchemaChanges);
                Separator();
                Add("Properties", ShowProperties, "DiagramProperties", "DesignerIconProperties", single && !table.IsNew, "Alt+Enter");
                // Design opens the Table Designer of the table (pedido do usuário, 08/11/2026; not in the SSMS diagram menu).
                Add("Design", () => DesignRequested?.Invoke(table), "DiagramDesign", enabled: single && !table.IsNew);
                break;
            case DiagramRelation relation:
                Add("Delete Relationships from Database", () => DeleteRelationship(relation), "DiagramDeleteRelationship", "DesignerIconDelete", !relation.Foreign.IsReadOnly);
                Separator();
                Add("Relationship Description…", () => EditDescription(relation.Foreign, DiagramDescription.ForeignKeyObject, relation.Key.Name), "DiagramRelationshipDescription", enabled: !IsBusy);
                Add("Properties", ShowProperties, "DiagramRelationshipProperties", "DesignerIconProperties", gesture: "Alt+Enter");
                break;
            case DiagramAnnotation note:
                Add("Edit Text", () => BeginNoteEdit(note), "DiagramEditAnnotation");
                Add("Delete Annotation", () => DeleteNote(note), "DiagramDeleteAnnotation", "DesignerIconDelete");
                break;
            default:
                var at = menuPoint;
                Add("New Table…", () => NewTable(at), "DiagramNewTable", enabled: !IsBusy);
                Add("Add Table…", ShowAddTable, "DiagramAddTable", enabled: !IsBusy);
                Add("New Text Annotation", () => NewAnnotation(at), "DiagramNewAnnotation");
                Separator();
                Add("Select All", SelectAll, "DiagramSelectAll", enabled: tables.Count > 0, gesture: "Ctrl+A");
                Add("Show Relationship Labels", ToggleLabels, "DiagramShowLabels").IsChecked = showLabels;
                // Show Indexes and Refresh are PGMS additions (PGMS-044): SSMS shows no index in a diagram and has no Refresh in this menu.
                Add("Show Indexes", ToggleIndexes, "DiagramShowIndexes").IsChecked = showIndexes;
                Separator();
                Add("Arrange Tables", ArrangeTables, "DiagramArrange", enabled: tables.Count > 0);
                var zoomMenu = new MenuItem { Header = "Zoom", Name = "DiagramZoom" };
                foreach (double level in ZoomLevels)
                {
                    var item = new MenuItem { Header = Percent(level), IsChecked = Math.Abs(level - zoom) < 0.001 };
                    item.Click += (_, _) => SetZoom(level); zoomMenu.Items.Add(item);
                }
                var fit = new MenuItem { Header = ToFit }; fit.Click += (_, _) => FitZoom(); zoomMenu.Items.Add(fit);
                menu.Items.Add(zoomMenu);
                Separator();
                Add("View Page Breaks", TogglePageBreaks, "DiagramViewPageBreaks").IsChecked = showPageBreaks;
                Add("Recalculate Page Breaks", RecalculatePageBreaks, "DiagramRecalculatePageBreaks");
                Separator();
                Add("Copy Diagram to Clipboard", () => CopyDiagram(), "DiagramCopy", enabled: ContentRects().Length > 0);
                // Export for AI and the optional function are PGMS additions (PGMS-045).
                Add("Export for AI…", () => _ = ExportForAiAsync(), "DiagramExportForAi", enabled: !IsBusy && tables.Any(t => !t.IsNew));
                Add("AI Export Function…", () => _ = ManageMarkdownFunctionAsync(), "DiagramMarkdownFunction", enabled: !IsBusy);
                Separator();
                Add("Refresh", () => _ = RefreshAsync(), "DiagramRefresh", enabled: !IsBusy && tables.Any(t => !t.IsNew));
                break;
        }
        return menu;
    }

    // ---- Tables ----
    private void AddTableClick(object sender, RoutedEventArgs e) => ShowAddTable();
    private void AddRelatedClick(object sender, RoutedEventArgs e) => _ = AddRelatedTablesAsync();
    private void ScriptClick(object sender, RoutedEventArgs e) => ShowChangeScript();
    private void UndoClick(object sender, RoutedEventArgs e) { Undo(); Focus(); }

    /// <summary>Tables the Add Table dialog offers: not in the diagram and not waiting to be deleted.</summary>
    public async Task<IReadOnlyList<DiagramTableRef>> CandidateTablesAsync(CancellationToken token = default)
    {
        var shown = tables.Select(t => t.Original.Oid).Concat(dropped.Select(d => d.Oid)).ToHashSet();
        return (await new DiagramService(connection).ListTablesAsync(token)).Where(t => !shown.Contains(t.Oid)).ToArray();
    }

    public void ShowAddTable()
    {
        if (IsBusy) return;
        var dialog = new AddTableDialog(CandidateTablesAsync, AddTablesAsync) { Owner = OwnerWindow };
        DialogShown?.Invoke(dialog);
        dialog.ShowDialog();
    }

    /// <summary>Adds tables as read now. One table goes in cascade, overlapping the previous one as SSMS does; several at once are laid out without overlap.</summary>
    public async Task AddTablesAsync(IReadOnlyList<DiagramTableRef> chosen)
    {
        if (IsBusy || chosen.Count == 0) return;
        using var cancellation = operation = new CancellationTokenSource();
        try
        {
            Say("Reading tables…", cancellable: true);
            var known = tables.Select(t => t.Original.Oid).ToHashSet();
            var read = await new DiagramService(connection).ReadTablesAndIndexesAsync(chosen.Where(c => !known.Contains(c.Oid)).Select(c => new DiagramTablePlacement(c.Schema, c.Name, 0, 0) { Oid = c.Oid }).ToArray(), [], cancellation.Token);
            var (designs, missing, indexes) = read;
            var added = designs.Where(d => !known.Contains(d.Oid)).Select(d => new DiagramTableModel(d, 0, 0) { Catalog = indexes.GetValueOrDefault(d.Oid) ?? DiagramTableIndexes.Empty, CanComment = !read.NotOwned.Contains(d.Oid) }).ToList();
            tables.AddRange(added);
            Rebuild();
            if (added.Count == 1)
            {
                added[0].X = 20 + 30 * (cascade % 10); added[0].Y = 20 + 30 * (cascade % 10); cascade++;
            }
            else Lay(added, tables.Except(added).Select(t => Bounds(t).Bottom).DefaultIfEmpty(-40).Max() + 60);
            selected.Clear(); selected.UnionWith(added); selectedRelation = null;
            Position();
            Say(missing.Count == 0 ? "" : "Tables no longer in the database were not added: " + string.Join(", ", missing) + ".");
        }
        catch (OperationCanceledException) { Say("Adding tables canceled."); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Say("The tables could not be read: " + Message(ex)); }
        finally { operation = null; Touch(); }
    }

    /// <summary>Rows of up to four tables, 60 px apart (room for the relationship ends), starting at top.</summary>
    private void Lay(IReadOnlyList<DiagramTableModel> laid, double top)
    {
        int perRow = Math.Max(1, Math.Min(4, (int)Math.Ceiling(Math.Sqrt(laid.Count))));
        double y = top;
        for (int start = 0; start < laid.Count; start += perRow)
        {
            double x = 20, height = 0;
            foreach (var table in laid.Skip(start).Take(perRow))
            {
                var size = Bounds(table).Size;
                table.X = x; table.Y = y; x += size.Width + 60; height = Math.Max(height, size.Height);
            }
            y += height + 60;
        }
    }

    /// <summary>Arrange Tables: rows in the order with the fewest crossing lines (pedido do usuário, 08/11/2026). Lines placed by hand are routed
    /// again (inferred: not checked in SSMS).</summary>
    public void ArrangeTables()
    {
        if (tables.Count == 0) return;
        PushUndo(TakeSnapshot()); // Ctrl+Z puts everything back (pedido do usuário)
        manualRoutes.Clear();
        var ordered = tables.OrderBy(t => t.Key, StringComparer.Ordinal).ToArray();
        var index = ordered.Select((t, i) => (t, i)).ToDictionary(p => p.t, p => p.i);
        var links = Relations.Select(r => (index[r.Primary], index[r.Foreign])).ToArray();
        var at = DiagramArrange.Arrange(ordered.Select(t => Bounds(t).Size).ToArray(), links, 20, 20);
        for (int i = 0; i < ordered.Length; i++) { ordered[i].X = at[i].X; ordered[i].Y = at[i].Y; }
        Position(); Touch();
    }
    public void SelectAll() { selected.Clear(); selected.UnionWith(tables); selectedRelation = null; RefreshTitles(); RefreshLineSelection(); }
    public void ToggleLabels() { showLabels = !showLabels; DrawLines(); Touch(); }

    /// <summary>Add Related Tables: tables linked by a foreign key to the selected tables (to all when none is selected).</summary>
    public async Task AddRelatedTablesAsync()
    {
        if (IsBusy || tables.Count == 0) return;
        var from = (selected.Count > 0 ? selected : tables.AsEnumerable()).Select(t => t.Original.Oid).ToArray();
        IReadOnlyList<DiagramTableRef> related;
        try { related = await new DiagramService(connection).RelatedTablesAsync(from); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException) { Say("Related tables could not be read: " + Message(ex)); return; }
        var shown = tables.Select(t => t.Original.Oid).Concat(dropped.Select(d => d.Oid)).ToHashSet();
        var missing = related.Where(r => !shown.Contains(r.Oid) && !(r.Schema == DiagramService.SchemaName && r.Name == DiagramService.TableName)).ToArray();
        if (missing.Length == 0) { Say("Every related table is already in the diagram."); return; }
        await AddTablesAsync(missing);
    }

    public void RemoveSelectedTables()
    {
        var removed = selected.ToArray();
        if (removed.Length == 0) return;
        if (removed.Any(t => t.SchemaChanged) && !Confirm("The selected tables have unsaved changes to keys, indexes or checks. Removing them from the diagram discards those changes. Continue?")) return;
        foreach (var table in removed)
        {
            tables.Remove(table);
            // A table never saved no longer exists anywhere, so nothing can reference it.
            if (table.IsNew) foreach (var other in tables) other.ForeignKeys.RemoveAll(k => other.References(table, k));
        }
        selected.Clear(); Rebuild(); Touch();
    }

    /// <summary>Delete Tables from Database: the tables leave the diagram now and are dropped on Save, after the script review. Relationships of other diagram tables that point at them go too.</summary>
    public void DeleteSelectedTables()
    {
        var removed = selected.ToArray();
        if (removed.Length == 0 || !Confirm($"The following tables will be deleted from the database when the diagram is saved, with all their data:\n\n{string.Join("\n", removed.Select(t => t.Key))}\n\nContinue?")) return;
        foreach (var table in removed)
        {
            tables.Remove(table);
            if (!table.IsNew) dropped.Add(table.Original); // a table never saved has nothing to drop
            foreach (var other in tables) other.ForeignKeys.RemoveAll(k => other.References(table, k));
        }
        selected.Clear(); Rebuild(); Touch();
    }

    public void DeleteRelationship(DiagramRelation relation)
    {
        if (relation.Foreign.IsReadOnly) { Say(relation.Foreign.Original.ReadOnlyReason!); return; }
        relation.Foreign.ForeignKeys.Remove(relation.Key);
        if (selectedRelation == relation) selectedRelation = null;
        Rebuild(); Touch();
    }

    // ---- Dialogs of the Table Designer ----
    private DesignerObjectKind RelationshipsKind(DiagramTableModel table) => DesignerObjectKinds.Relationships(table.ForeignKeys, table.Schema, () => table.UniqueName($"fk_{table.Name}".ToLowerInvariant()), Touch);
    private void ShowObjects(DiagramTableModel table, DesignerObjectKind kind, object? select = null)
    {
        var dialog = new DesignerObjectsDialog(kind, table.IsReadOnly || IsBusy, select) { Owner = OwnerWindow };
        DialogShown?.Invoke(dialog);
        dialog.ShowDialog();
        Rebuild(); Touch();
    }
    public void ShowRelationships(DiagramTableModel table, DesignerForeignKeyRow? select = null) => ShowObjects(table, RelationshipsKind(table), select);
    /// <summary>Indexes/Keys of the table; select names the index to show first (an index row of the Indexes section), when the dialog still has it.</summary>
    public void ShowIndexes(DiagramTableModel table, string? select = null) => ShowObjects(table, DesignerObjectKinds.Indexes(table.Indexes, () => table.UniqueName($"ix_{table.Name}"), () => table.Columns.FirstOrDefault()?.Name, Touch, () => { }),
        select is null ? null : table.Indexes.FirstOrDefault(i => i.Name == select));
    public void ShowChecks(DiagramTableModel table) => ShowObjects(table, DesignerObjectKinds.Checks(table.Checks, () => table.UniqueName($"ck_{table.Name}"), Touch));

    /// <summary>Properties (Alt+Enter): Table Properties of the selected table, or the selected relationship in Foreign Key Relationships.</summary>
    public void ShowProperties()
    {
        if (selectedRelation is { } relation) { ShowRelationships(relation.Foreign, relation.Key); return; }
        if (selected.Count != 1) return;
        var table = selected.First();
        if (table.IsNew) { Say("Save the diagram to create the table before opening its properties. Use the Standard view to edit its columns."); return; }
        // The Columns tab edits the diagram's table (pedido do usuário, 08/11/2026): the box changes at once and Save writes the change.
        var dialog = new TablePropertiesDialog(connection, table.Schema, table.Name)
        {
            Owner = OwnerWindow, InspectOnOpen = true,
            ColumnTarget = new(() => IsBusy ? table.Current() with { ReadOnlyReason = "the diagram is busy." } : table.Current(), columns => ApplyColumns(table, columns))
        };
        DialogShown?.Invoke(dialog);
        dialog.ShowDialog();
    }

    /// <summary>New names, types and nulls of a table's columns. A rename follows into the table's keys and indexes and into the foreign keys of the
    /// diagram that use the column, as RENAME COLUMN does in PostgreSQL, so Save renames instead of rebuilding them.</summary>
    public void ApplyColumns(DiagramTableModel table, IReadOnlyList<DesignColumn> columns)
    {
        if (table.IsReadOnly) return;
        var renames = TableDesignColumns.Renames(table.Current(), columns).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        // New columns have no position to follow: a rename is the same row with another name.
        if (columns.Count == table.Columns.Count)
            for (int i = 0; i < columns.Count; i++)
                if (columns[i].Position is null && table.Columns[i].Position is null && columns[i].Name != table.Columns[i].Name) renames[table.Columns[i].Name] = columns[i].Name;
        string Map(string column) => renames.GetValueOrDefault(column, column);
        table.Columns.Clear(); table.Columns.AddRange(columns);
        if (renames.Count > 0)
        {
            foreach (var index in table.Indexes.Where(i => i.FixedDefinition is null)) index.Columns = index.Columns.Select(c => c with { Name = Map(c.Name) }).ToList();
            foreach (var key in table.ForeignKeys.Where(k => k.FixedDefinition is null)) key.Columns = key.Columns.Select(Map).ToList();
            foreach (var key in tables.SelectMany(t => t.ForeignKeys.Where(k => t.References(table, k))))
                key.ReferencedColumns = key.ReferencedColumns.Select(Map).ToList();
            if (selectedColumnTable == table) { var renamedSelection = selectedColumns.Select(Map).ToArray(); selectedColumns.Clear(); selectedColumns.UnionWith(renamedSelection); }
        }
        Rebuild(); Touch();
    }

    // ---- Relationship by dragging a column (pedido do usuário, 08/11/2026) ----
    // Better than SSMS on purpose: the cursor becomes the key, a line like the relationship follows the mouse from the primary key table,
    // the column under the mouse is highlighted, and one dialog (New Relationship) replaces Tables and Columns plus Foreign Key Relationship.
    private static Cursor? keyCursor;
    /// <summary>The cursor while a relationship is dragged: the key of the primary key rows, enlarged, with the hot spot at the tip of the shaft.</summary>
    internal static Cursor KeyCursor => keyCursor ??= new(new MemoryStream(HandCursors.CursorFile(RenderKeyCursor(), 16, 27)), scaleWithDpi: true);
    internal static BitmapSource RenderKeyCursor()
    {
        var glyph = KeyGlyph();
        glyph.RenderTransform = new ScaleTransform(1.7, 1.7);
        Canvas.SetLeft(glyph, 16 - 5 * 1.7); Canvas.SetTop(glyph, 2.5);
        var host = new Canvas { Width = 32, Height = 32 };
        host.Children.Add(glyph);
        host.Measure(new Size(32, 32)); host.Arrange(new Rect(0, 0, 32, 32)); host.UpdateLayout();
        var frame = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); frame.Render(host);
        var image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); image.Freeze();
        return image;
    }

    internal DiagramHit? DropTarget => dropTarget;
    internal bool IsKeyCursorShown => Surface.ForceCursor && Surface.Cursor == KeyCursor;
    internal bool IsNoDropCursorShown => Surface.ForceCursor && Surface.Cursor == Cursors.No;
    internal Canvas DragPreview => DragLayer;
    internal Grid RowOf(DiagramTableModel table, string column) => rowVisuals[(table, column)].Row;

    /// <summary>Starts a relationship from a column; nothing shows until the mouse really moves, so a click still only selects.</summary>
    internal void StartColumnDrag(DiagramHit column, Point point) { dragColumn = column; dragStart = point; moved = false; dropTarget = null; }

    /// <summary>What a drop at the point would use: a column of a table (not the dragged column itself), or the title or body of another table,
    /// whose column is then chosen in the dialog. Null where nothing can be dropped; refused is true over a table that cannot take it.</summary>
    private DiagramHit? DropTargetAt(Point point, out bool refused)
    {
        refused = false;
        if (dragColumn is not { Column: { } from } source || HitAt(point) is not DiagramHit hit) return null;
        DiagramHit? target = hit.Column is { } to ? (hit.Table != source.Table || to != from ? hit with { Part = DiagramHitPart.Column, Field = null } : null)
            : hit.Table != source.Table ? hit with { Part = DiagramHitPart.Title } : null;
        if (target is { Table.IsReadOnly: true }) target = null;
        refused = target is null;
        return target;
    }

    internal void DragColumnTo(Point point)
    {
        if (dragColumn is not { Column: { } from } source) return;
        if (!moved && Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) < 3) return;
        moved = true;
        var target = DropTargetAt(point, out bool refused);
        Highlight(target);
        Surface.Cursor = refused ? Cursors.No : KeyCursor; Surface.ForceCursor = true;
        DrawDragPreview(source, from, point, target);
    }

    internal void EndColumnDrag(Point point)
    {
        var source = dragColumn;
        var target = moved ? DropTargetAt(point, out _) : null;
        CancelColumnDrag();
        if (source is { Column: { } from } && target is not null) CreateRelationship(source.Table, from, target.Table, target.Column);
    }

    private void CancelColumnDrag()
    {
        Highlight(null); DragLayer.Children.Clear();
        dragColumn = null; moved = false;
        Surface.Cursor = null; Surface.ForceCursor = false;
    }

    /// <summary>The column under the mouse as selected (blue, white text); another table's title or body as a blue frame.</summary>
    private void Highlight(DiagramHit? target)
    {
        if (dropTarget == target) return;
        if (dropTarget is { } old)
        {
            if (old.Column is { } c && rowVisuals.TryGetValue((old.Table, c), out var was)) { was.Row.Background = RowBrush(old.Table, c); was.Text.Foreground = Brushes.Black; }
            else if (boxes.TryGetValue(old.Table, out var box)) { box.Box.BorderBrush = FrameBrush; box.Box.BorderThickness = new Thickness(FrameWidth); }
        }
        dropTarget = target;
        if (target is null) return;
        if (target.Column is { } column && rowVisuals.TryGetValue((target.Table, column), out var row)) { row.Row.Background = SelectedTitleBrush; row.Text.Foreground = Brushes.White; }
        else if (boxes.TryGetValue(target.Table, out var frame)) { frame.Box.BorderBrush = SelectedLineBrush; frame.Box.BorderThickness = new Thickness(2 * FrameWidth); }
    }

    /// <summary>The middle of a row on the surface, at the left or right border of its table.</summary>
    private Point RowPoint(DiagramTableModel table, string column, bool right)
    {
        var bounds = Bounds(table);
        double y = rowVisuals.TryGetValue((table, column), out var row) && row.Row.IsArrangeValid && row.Row.ActualHeight > 0
            ? row.Row.TranslatePoint(new Point(0, row.Row.ActualHeight / 2), Surface).Y : bounds.Top + bounds.Height / 2;
        return new(right ? bounds.Right : bounds.Left, y);
    }

    /// <summary>The line of the drag, in right angles like a relationship: out of the side of the dragged row and to the end. With endRight it ends at
    /// a side of the other table (both sides on the right go around to the right of the two); without, at the mouse.</summary>
    internal static IReadOnlyList<Point> PreviewRoute(Point start, bool startRight, Point end, bool? endRight)
    {
        const double lead = 20;
        double sign = startRight ? 1 : -1;
        double x = endRight is { } toRight
            ? startRight == toRight ? (startRight ? Math.Max(start.X, end.X) + lead : Math.Min(start.X, end.X) - lead) : (start.X + end.X) / 2
            : (end.X - start.X) * sign > 2 * lead ? (start.X + end.X) / 2 : start.X + sign * lead;
        return [start, new(x, start.Y), new(x, end.Y), end];
    }

    private void DrawDragPreview(DiagramHit source, string from, Point point, DiagramHit? target)
    {
        DragLayer.Children.Clear();
        var primary = Bounds(source.Table);
        Point start, end; bool startRight; bool? endRight = null;
        if (target is { Column: { } to })
        {
            var other = Bounds(target.Table);
            // Facing sides when the tables are apart; both on the right when one is above the other (or for the same table).
            if (other.Left >= primary.Right + 10) { startRight = true; endRight = false; }
            else if (other.Right <= primary.Left - 10) { startRight = false; endRight = true; }
            else { startRight = true; endRight = true; }
            start = RowPoint(source.Table, from, startRight); end = RowPoint(target.Table, to, endRight.Value);
        }
        else
        {
            startRight = point.X >= primary.Left + primary.Width / 2;
            start = RowPoint(source.Table, from, startRight); end = point;
        }
        var points = new PointCollection(PreviewRoute(start, startRight, end, endRight));
        DragLayer.Children.Add(new Polyline { Points = points, Stroke = LineBrush, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Miter });
        DragLayer.Children.Add(new Polyline { Points = points, Stroke = Brushes.White, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Miter });
        DragLayer.Children.Add(EndGlyph(start, new Vector(startRight ? 1 : -1, 0), key: true, null));
        if (endRight is { } right) DragLayer.Children.Add(EndGlyph(end, new Vector(right ? 1 : -1, 0), key: false, null));
    }

    /// <summary>Drop of a column of the primary key table on a column of the foreign key table, or on its title with the column left to choose:
    /// New Relationship, with the pair already filled in. The line shows while it is open; Cancel removes it.</summary>
    public bool CreateRelationship(DiagramTableModel primary, string primaryColumn, DiagramTableModel foreign, string? foreignColumn)
    {
        if (foreign.IsReadOnly) { Say($"{foreign.Caption}: {foreign.Original.ReadOnlyReason}"); return false; }
        string target = foreignColumn ?? "";
        var key = primary.PrimaryKey;
        (string, string)[] pairs = key.Contains(primaryColumn) ? key.Select(c => (c, c == primaryColumn ? target : "")).ToArray() : [(primaryColumn, target)];
        var row = new DesignerForeignKeyRow
        {
            Name = foreign.UniqueName($"fk_{foreign.Name}_{primary.Name}".ToLowerInvariant()), Columns = target.Length > 0 ? [target] : [],
            ReferencedSchema = primary.Schema, ReferencedTable = primary.Name, ReferencedColumns = [primaryColumn]
        };
        foreign.ForeignKeys.Add(row);
        selected.Clear(); selectedRelation = null; Rebuild(); Touch();

        var dialog = new NewRelationshipDialog(tables, primary, foreign, row.Name, pairs) { Owner = OwnerWindow };
        DialogShown?.Invoke(dialog);
        if (!(RelationshipPrompt ?? (d => d.ShowDialog() == true))(dialog) || dialog.Primary is not { } chosen || dialog.Columns.Count == 0)
        {
            foreign.ForeignKeys.Remove(row); Rebuild(); Touch();
            return false;
        }
        row.Name = dialog.NameBox.Text.Trim(); row.ReferencedSchema = chosen.Schema; row.ReferencedTable = chosen.Name;
        row.Columns = dialog.Columns.Select(c => c.Foreign).ToList(); row.ReferencedColumns = dialog.Columns.Select(c => c.Primary).ToList();
        row.OnDelete = dialog.OnDelete; row.OnUpdate = dialog.OnUpdate; row.CheckExisting = dialog.CheckExisting.IsChecked == true; row.Description = dialog.DescriptionBox.Text.Trim();
        Rebuild(); Touch(); Say("");
        return true;
    }

    /// <summary>Same server, port, user and database as another connection.</summary>
    public bool OnDatabase(string other)
    {
        var candidate = new NpgsqlConnectionStringBuilder(other);
        return string.Equals(candidate.Host, target.Host, StringComparison.OrdinalIgnoreCase) && candidate.Port == target.Port && candidate.Username == target.Username && candidate.Database == target.Database;
    }

    /// <summary>A table was saved elsewhere (the Table Designer opened by Design): the tables without changes in this diagram are read again, so the
    /// diagram shows them as they are and its next Save is not refused. Tables with changes here are kept and named in the feedback line.</summary>
    public async Task RefreshTablesAsync()
    {
        if (IsBusy || tables.Count == 0) return;
        var clean = tables.Where(t => !TableChanged(t)).ToArray();
        var kept = tables.Except(clean).Select(t => t.Caption).ToArray();
        using var cancellation = operation = new CancellationTokenSource();
        try
        {
            var routes = CurrentLayout().Relationships ?? [];
            bool wasDirty = IsDirty;
            // The indexes of every table are read again, also of tables with pending changes: they come from the catalog and are not edited here.
            var reread = await new DiagramService(connection).ReadTablesAndIndexesAsync(clean.Select(t => new DiagramTablePlacement(t.Schema, t.Name, t.X, t.Y) { Oid = t.Original.Oid }).ToArray(),
                tables.Except(clean).Where(t => !t.IsNew).Select(t => t.Original.Oid).ToArray(), cancellation.Token);
            var (designs, _, indexes) = reread;
            var byOid = designs.ToDictionary(d => d.Oid);
            foreach (var table in clean) if (byOid.TryGetValue(table.Original.Oid, out var read)) table.Reset(read);
            foreach (var table in tables.Where(t => !t.IsNew)) { table.Catalog = indexes.GetValueOrDefault(table.Original.Oid) ?? DiagramTableIndexes.Empty; table.CanComment = !reread.NotOwned.Contains(table.Original.Oid); }
            // Reset makes new key rows: the lines placed by hand are found again by name.
            ApplyRoutes(routes);
            if (!wasDirty) layoutBaseline = LayoutFingerprint();
            Rebuild();
            Say(kept.Length == 0 ? "" : "Tables with unsaved changes in this diagram were not read again: " + string.Join(", ", kept) + ". Saving them is refused if the Table Designer changed them too.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException) { Say("The tables could not be read again: " + Message(ex)); }
        finally { operation = null; Touch(); }
    }

    // ---- Script and save ----
    /// <summary>The ordered schema changes, or null with the reason in the feedback line.</summary>
    public DiagramSchemaChanges.Result? BuildChanges()
    {
        CommitEdit();
        try { return DiagramSchemaChanges.Build(tables.Select(t => (t.Original, t.Current())).ToArray(), dropped, NotOwned()); }
        catch (ArgumentException ex) { Say(ex.Message); return null; }
    }
    public void ShowChangeScript()
    {
        if (BuildChanges() is not { } changes) return;
        if (changes.Edits.Count == 0) { Say("There are no changes to tables to script. The layout is saved with the diagram."); return; }
        var dialog = new ChangeScriptDialog(changes.Script, false) { Owner = OwnerWindow };
        DialogShown?.Invoke(dialog);
        dialog.ShowDialog();
    }

    /// <summary>Asks for a name on a new diagram, shows the script of any schema change, then writes both in one transaction. False leaves the tab as it was.</summary>
    public async Task<bool> SaveAsync()
    {
        if (IsBusy) return false;
        string chosen = name;
        if (IsNew)
        {
            string? typed = (NamePromptOverride ?? (s => NamePrompt.Show(OwnerWindow, "Choose Name", "Enter a name for the diagram:", s)))(name);
            if (typed is null) return false;
            try { chosen = DiagramService.ValidName(typed); }
            catch (ArgumentException ex) { Say(ex.Message); return false; }
        }
        if (BuildChanges() is not { } changes) return false;
        if (changes.Edits.Count > 0 && !(ReviewPrompt ?? Review)(changes.Script)) return false;

        using var cancellation = operation = new CancellationTokenSource();
        Say("Saving…", cancellable: true); RefreshState();
        try
        {
            var service = new DiagramService(connection);
            // Descriptions the user cannot apply go into the layout as not applied (PGMS-045); the save writes the copy of the others from the catalog.
            var written = await service.SaveWithLayoutAsync(info, chosen, CurrentLayout(changes.Unapplied.ToDictionary(u => u.Oid, u => u.Items)), changes.Edits, cancellation.Token);
            var saved = written.Info;
            info = saved; name = saved.Name;
            var savedLayout = CurrentLayout();
            var reread = await service.ReadTablesAndIndexesAsync(savedLayout.Tables, [], CancellationToken.None);
            var (designs, _, indexes) = reread;
            var copies = written.Layout.Tables.ToDictionary(t => t.Schema + "." + t.Name, t => t.Descriptions, StringComparer.Ordinal);
            var byOid = designs.ToDictionary(d => d.Oid);
            var byName = designs.ToDictionary(d => d.Schema + "." + d.Name, StringComparer.Ordinal);
            // A table created by this save had no OID when the layout was read: it is found by name.
            var read = tables.ToDictionary(t => t, t => t.IsNew ? byName.GetValueOrDefault(t.Key) : byOid.GetValueOrDefault(t.Original.Oid));
            tables.RemoveAll(t => read[t] is null);
            foreach (var table in tables)
            {
                table.Reset(read[table]!); table.Catalog = indexes.GetValueOrDefault(table.Original.Oid) ?? DiagramTableIndexes.Empty; table.CanComment = !reread.NotOwned.Contains(table.Original.Oid);
                table.SavedDescriptions = copies.GetValueOrDefault(table.Key);
                table.Unapplied.Clear(); table.Unapplied.AddRange((table.SavedDescriptions ?? []).Where(d => !d.Applied));
            }
            // Reset makes new key rows: the lines placed by hand are found again by name.
            ApplyRoutes(savedLayout.Relationships);
            dropped.Clear();
            layoutBaseline = LayoutFingerprint();
            Rebuild();
            Say(changes.Unapplied.Count == 0 ? "Diagram saved." : "Diagram saved. Descriptions of tables you do not own were kept only in the diagram, marked as not applied.");
            Saved?.Invoke(changes.Edits.Count > 0);
            if (changes.Unapplied.Count > 0)
                ShowUnappliedScript("These descriptions were not applied: only the owner of a table can change its descriptions. They are kept in the diagram, marked as not applied. Send this script to the owner, or to someone who can run it.",
                    changes.Unapplied.SelectMany(u => u.Statements).ToArray());
            return true;
        }
        catch (OperationCanceledException) { Say("Save canceled. The transaction was rolled back."); return false; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException) { Say("The diagram was not saved: " + Message(ex)); return false; }
        finally { operation = null; Touch(); }
    }
    private bool Review(TableDesignChange change) => new ChangeScriptDialog(change, true) { Owner = OwnerWindow }.ShowDialog() == true;
}
