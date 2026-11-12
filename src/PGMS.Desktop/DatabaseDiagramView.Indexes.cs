using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PGMS.PostgreSql;
using Path = System.Windows.Shapes.Path;

namespace PGMS.Desktop;

/// <summary>Indexes in the diagram (PGMS-044, docs/MARCO-M1061.md). SSMS 22.11 shows no index in a diagram table (pilotagem de 11/11/2026): the section,
/// the method label and the warning for foreign keys without an index are PGMS additions; the glyphs follow the Object Explorer of SSMS (a key for the
/// primary key, another key for unique, a tree for an index). Read only: indexes are created and changed in Indexes/Keys and the other dialogs.</summary>
public partial class DatabaseDiagramView
{
    private static readonly Brush IndexHeaderBrush = Frozen("#F3F3F3"), IndexDetailBrush = Frozen("#6A6A6A"), InvalidBrush = Frozen("#A1260D"), WarningFill = Frozen("#F2C200"), WarningInk = Frozen("#5C4500"),
        SilverKey = FrozenGradient("#F4F4F4", "#9A9A9A"), TreeInk = Frozen("#424242");

    /// <summary>Show Indexes of the diagram menu: the Indexes section and the warning of foreign keys without an index, on by default, kept by diagram.</summary>
    public bool IndexesShown => showIndexes;

    /// <summary>Find Unindexed Foreign Keys… from the warning of a table or the table menu; the main window opens the window on this database.</summary>
    public event Action<DiagramTableModel?>? UnindexedForeignKeysRequested;

    public void ToggleIndexes() { showIndexes = !showIndexes; Rebuild(); Touch(); }

    /// <summary>Opens or closes the Indexes section of a table; the table keeps its place, so the tables below may be covered until moved or arranged.</summary>
    public void ToggleIndexSection(DiagramTableModel table) { table.IndexesExpanded = !table.IndexesExpanded; Rebuild(); Touch(); }

    public void RequestUnindexedForeignKeys(DiagramTableModel? table = null) => UnindexedForeignKeysRequested?.Invoke(table);

    /// <summary>Refresh of the diagram menu: reads the tables without pending changes and the indexes of every table again.</summary>
    public Task RefreshAsync() => RefreshTablesAsync();

    private bool ShowsIndexSection(DiagramTableModel table) => showIndexes && !table.IsNew && table.View != DiagramTableView.NameOnly;
    private bool ShowsWarning(DiagramTableModel table) => showIndexes && !table.IsNew && table.Catalog.UnindexedForeignKeys.Count > 0;

    /// <summary>The title of a table: the caption, the mark of its description (PGMS-045) and, when a foreign key of the table has no index, the warning on the right.</summary>
    private UIElement TitleContent(DiagramTableModel table, TextBlock caption)
    {
        var note = MarkFor(table, DiagramDescription.TableObject, null);
        if (!ShowsWarning(table) && note is null) return caption;
        var panel = new DockPanel();
        if (ShowsWarning(table)) { var warning = WarningGlyph(table); DockPanel.SetDock(warning, Dock.Right); panel.Children.Add(warning); }
        if (note is not null) { note.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(note, Dock.Right); panel.Children.Add(note); }
        panel.Children.Add(caption);
        return panel;
    }

    internal static string UnindexedText(DiagramTableIndexes catalog)
    {
        var lines = new List<string> { catalog.UnindexedForeignKeys.Count == 1 ? "A foreign key of this table has no index:" : "Foreign keys of this table have no index:" };
        foreach (var key in catalog.UnindexedForeignKeys)
            lines.Add($"  {key.Name} ({string.Join(", ", key.Columns)})" + (key.NearIndexes.Count > 0 ? " — " + string.Join("; ", key.NearIndexes) : ""));
        lines.Add("");
        lines.Add("Deleting or updating a row of the referenced table reads this table to check the key.");
        lines.Add("Click to open Find Unindexed Foreign Keys.");
        return string.Join("\n", lines);
    }

    private FrameworkElement WarningGlyph(DiagramTableModel table)
    {
        var canvas = new Canvas { Width = 14, Height = 13, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        canvas.Children.Add(new Path { Data = Geometry.Parse("M7,0.5 L13.5,12.5 L0.5,12.5 Z"), Fill = WarningFill, Stroke = WarningInk, StrokeThickness = 0.9, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M6.2,4 L7.8,4 L7.5,8.6 L6.5,8.6 Z M6.2,9.6 L7.8,9.6 L7.8,11.1 L6.2,11.1 Z"), Fill = WarningInk, IsHitTestVisible = false });
        canvas.Tag = new DiagramHit(table, null, DiagramHitPart.Warning);
        canvas.ToolTip = UnindexedText(table.Catalog);
        ToolTipService.SetShowDuration(canvas, 60000);
        AutomationProperties.SetName(canvas, "Foreign keys without an index");
        AutomationProperties.SetHelpText(canvas, UnindexedText(table.Catalog));
        return canvas;
    }

    private static string MethodsText(IEnumerable<DiagramIndex> indexes) => string.Join(", ", indexes.Select(i => i.MethodLabel).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal));

    internal static string IndexToolTip(DiagramIndex index)
    {
        var lines = new List<string> { index.Name, $"{index.Kind}, {(index.Method == "btree" ? "B-tree" : index.MethodLabel)}" };
        lines.Add("Keys: " + string.Join(", ", index.Keys.Select(k => k.Text)));
        if (index.Keys.Any(k => k.OperatorClass is not null)) lines.Add("Operator class: " + string.Join(", ", index.Keys.Where(k => k.OperatorClass is not null).Select(k => $"{k.Expression} {k.OperatorClass}")));
        if (index.Include.Count > 0) lines.Add("Include: " + string.Join(", ", index.Include));
        if (index.Predicate is { } predicate) lines.Add("Partial: WHERE " + predicate);
        if (!index.Valid) lines.Add("Invalid: a failed CREATE INDEX CONCURRENTLY left it; the planner does not use it.");
        lines.Add("");
        lines.Add(index.Definition);
        return string.Join("\n", lines);
    }

    /// <summary>The Indexes section below the columns: a header that opens and closes it, then one row per index when open.</summary>
    private void AddIndexSection(StackPanel stack, DiagramTableModel table)
    {
        var indexes = table.Catalog.Indexes;
        string methods = MethodsText(indexes);
        var header = new Grid { Height = 18, Background = IndexHeaderBrush, Cursor = Cursors.Hand, Tag = new DiagramHit(table, null, DiagramHitPart.IndexHeader) };
        var text = new TextBlock { Margin = new Thickness(5, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run((table.IndexesExpanded ? "▾ " : "▸ ") + $"Indexes ({indexes.Count})"));
        if (methods.Length > 0) text.Inlines.Add(new Run(" · " + methods) { Foreground = IndexDetailBrush });
        header.Children.Add(text);
        header.ToolTip = indexes.Count == 0 ? "This table has no index." : string.Join("\n", indexes.Select(i => $"{i.Name}  {i.MethodLabel}{(i.MethodLabel.Length > 0 ? " " : "")}{i.KeysText}{(i.Flags.Count > 0 ? "  " + string.Join(", ", i.Flags) : "")}"))
            + (table.IndexesExpanded ? "" : "\n\nClick to show the indexes in the table.");
        ToolTipService.SetShowDuration(header, 60000);
        AutomationProperties.SetName(header, $"Indexes, {indexes.Count}, {(table.IndexesExpanded ? "expanded" : "collapsed")}");
        // Closed, the header never widens the table: the diagram keeps the size it was laid out with.
        stack.Children.Add(table.IndexesExpanded ? header : new WidthNeutral { Child = header });
        if (!table.IndexesExpanded) return;
        foreach (var index in indexes)
        {
            var row = new Grid { Height = 18, Background = Brushes.White, Tag = new DiagramHit(table, null, DiagramHitPart.Index, index.Name), ToolTip = IndexToolTip(index) };
            ToolTipService.SetShowDuration(row, 60000);
            row.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); row.ColumnDefinitions.Add(new());
            row.Children.Add(new Border { Background = SelectorBrush, BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(0, 0, 1, 0), Child = IndexGlyph(index) });
            var line = new TextBlock { Margin = new Thickness(5, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            line.Inlines.Add(new Run(index.Name) { Foreground = index.Valid ? Brushes.Black : InvalidBrush });
            if (index.MethodLabel.Length > 0) line.Inlines.Add(new Run("  " + index.MethodLabel) { FontWeight = FontWeights.SemiBold, Foreground = IndexDetailBrush });
            line.Inlines.Add(new Run("  " + index.KeysText) { Foreground = IndexDetailBrush });
            var flags = index.Flags.Where(f => f != "PRIMARY KEY").ToArray(); // the key glyph already says it
            if (flags.Length > 0) line.Inlines.Add(new Run("  " + string.Join(", ", flags)) { Foreground = index.Valid ? IndexDetailBrush : InvalidBrush, FontStyle = FontStyles.Italic });
            Grid.SetColumn(line, 1); row.Children.Add(line);
            AutomationProperties.SetName(row, $"Index {index.Name}, {index.Kind}{(index.MethodLabel.Length > 0 ? ", " + index.MethodLabel : "")}, {index.KeysText}{(flags.Length > 0 ? ", " + string.Join(", ", flags) : "")}");
            stack.Children.Add(row);
        }
    }

    /// <summary>The glyph of an index row: the gold key of the primary key (as on its columns), a silver key lying down for unique and exclusion, a tree for an index.</summary>
    internal static Canvas IndexGlyph(DiagramIndex index)
    {
        Canvas canvas;
        if (index.Primary) canvas = KeyGlyph();
        else if (index.Unique || index.Exclusion)
        {
            canvas = new Canvas { Width = 15, Height = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var bow = Geometry.Combine(new EllipseGeometry(new Point(4.5, 5), 4.2, 4.2), new EllipseGeometry(new Point(3.9, 5), 1.3, 1.3), GeometryCombineMode.Exclude, null);
            canvas.Children.Add(new Path { Data = Union(bow, Rounded(8, 4, 6.2, 2.2, 0.4), Rounded(10.6, 6, 1.4, 2.4, 0.3), Rounded(12.6, 6, 1.4, 1.8, 0.3)), Fill = SilverKey, Stroke = Brushes.DimGray, StrokeThickness = 0.9 });
        }
        else
        {
            canvas = new Canvas { Width = 13, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            canvas.Children.Add(new Path { Data = Geometry.Parse("M4.5,0.5 H8.5 V3.5 H4.5 Z M0.5,8.5 H4.5 V11.5 H0.5 Z M8.5,8.5 H12.5 V11.5 H8.5 Z M6.5,3.5 V6 M2.5,8.5 V6 H10.5 V8.5"), Stroke = TreeInk, StrokeThickness = 1, Fill = Brushes.White });
        }
        AutomationProperties.SetName(canvas, index.Primary ? "Primary key index" : index.Unique || index.Exclusion ? "Unique index" : "Index");
        return canvas;
    }

    /// <summary>Measures its child in the space it is given but asks for no width, so a long line is cut instead of widening the table.</summary>
    private sealed class WidthNeutral : Decorator
    {
        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null) return default;
            Child.Measure(new Size(double.IsInfinity(constraint.Width) ? 0 : constraint.Width, constraint.Height));
            return new Size(0, Child.DesiredSize.Height);
        }
        protected override Size ArrangeOverride(Size arrangeSize) { Child?.Arrange(new Rect(arrangeSize)); return arrangeSize; }
    }
}
