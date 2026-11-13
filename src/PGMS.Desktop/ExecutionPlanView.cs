using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Places operators as in the SSMS 22.11 graphical plan: root at the left, each input one column to the right, first input on its parent's row.</summary>
internal static class ExecutionPlanLayout
{
    internal const double ColumnGap = 62, RowGap = 26;

    internal static Dictionary<ExecutionPlanOperator, Rect> Place(ExecutionPlanOperator root, Func<ExecutionPlanOperator, Size> measure)
    {
        var columns = new List<double>();
        void Widths(ExecutionPlanOperator node, int depth)
        {
            if (columns.Count == depth) columns.Add(0);
            columns[depth] = Math.Max(columns[depth], measure(node).Width);
            foreach (var child in node.Children) Widths(child, depth + 1);
        }
        Widths(root, 0);
        var lefts = new double[columns.Count];
        for (int i = 1; i < lefts.Length; i++) lefts[i] = lefts[i - 1] + columns[i - 1] + ColumnGap;

        var centers = new Dictionary<ExecutionPlanOperator, double>();
        void Shift(ExecutionPlanOperator node, double delta) { centers[node] += delta; foreach (var child in node.Children) Shift(child, delta); }
        double Lay(ExecutionPlanOperator node, double top) // Returns the bottom of the subtree.
        {
            double half = measure(node).Height / 2;
            if (node.Children.Count == 0) { centers[node] = top + half; return top + half * 2; }
            double bottom = top;
            for (int i = 0; i < node.Children.Count; i++) bottom = Lay(node.Children[i], i == 0 ? top : bottom + RowGap);
            centers[node] = centers[node.Children[0]];
            double overflow = top + half - centers[node];
            if (overflow > 0) { Shift(node, overflow); bottom += overflow; }
            return Math.Max(bottom, centers[node] + half);
        }
        Lay(root, 0);

        var places = new Dictionary<ExecutionPlanOperator, Rect>();
        void Collect(ExecutionPlanOperator node, int depth)
        {
            var size = measure(node);
            places[node] = new Rect(lefts[depth] + (columns[depth] - size.Width) / 2, centers[node] - size.Height / 2, size.Width, size.Height);
            foreach (var child in node.Children) Collect(child, depth + 1);
        }
        Collect(root, 0);
        return places;
    }

    /// <summary>Arrow width grows with the logarithm of the rows.</summary>
    internal static double ArrowThickness(double rows) => Math.Clamp(2 + Math.Log10(Math.Max(rows, 0) + 1) * 1.5, 2, 12);
}

/// <summary>Graphical execution plan: one header and one diagram per statement, in one scroll area, with zoom and operator selection.</summary>
public sealed class ExecutionPlanView : UserControl
{
    private const double MinZoom = 0.1, MaxZoom = 4, ZoomStep = 1.25;
    private static readonly Brush Selection = Frozen(0x00, 0x78, 0xD7), ArrowEdge = Frozen(0xA6, 0xA6, 0xA6), TipBack = Frozen(0xFF, 0xFF, 0xE1), TipLine = Frozen(0xC9, 0xC3, 0x6B), IconStroke = Frozen(0x2B, 0x57, 0x97), IconFill = Frozen(0xDD, 0xE8, 0xF5);
    private static readonly FontFamily DiagramFont = new("Courier New");
    private readonly ScrollViewer scroller;
    private readonly StackPanel sections = new();
    private readonly ScaleTransform scale = new(1, 1);
    private readonly List<(ExecutionPlanStatement Statement, Canvas Canvas)> shown = [];
    private readonly Dictionary<ExecutionPlanOperator, (Border Label, TextBlock Text, ExecutionPlanStatement Statement)> boxes = [];
    private readonly Border findBar = new() { Background = Frozen(0xD5, 0xE5, 0xEF), Padding = new Thickness(4, 3, 4, 3), Visibility = Visibility.Collapsed };
    private readonly ComboBox findProperty = new() { Width = 170, Margin = new Thickness(0, 0, 4, 0) }, findComparison = new() { Width = 80, Margin = new Thickness(0, 0, 4, 0), ItemsSource = new[] { "Contains", "=", ">", "<" }, SelectedIndex = 0 };
    private readonly TextBox findValue = new() { Width = 150, Margin = new Thickness(0, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock findResult = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

    public ExecutionPlanView()
    {
        scroller = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sections, Background = Brushes.White, Focusable = false };
        var layout = new DockPanel();
        DockPanel.SetDock(findBar, Dock.Top);
        findBar.Child = BuildFindBar();
        layout.Children.Add(findBar);
        layout.Children.Add(scroller);
        Content = layout;
        AutomationProperties.SetName(this, "Graphical execution plan");
        scroller.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            Zoom *= e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        };
        ContextMenu = BuildMenu();
    }

    public IReadOnlyList<ExecutionPlanStatement> Statements => shown.Select(s => s.Statement).ToList();
    public ExecutionPlanOperator? SelectedOperator { get; private set; }
    public ExecutionPlanStatement? SelectedStatement => SelectedOperator is { } op && boxes.TryGetValue(op, out var box) ? box.Statement : null;
    public event EventHandler? SelectionChanged;
    /// <summary>Save Execution Plan As… of the context menu.</summary>
    public event EventHandler? SaveRequested;
    /// <summary>Show Execution Plan In New Tab of the context menu.</summary>
    public event EventHandler? ShowInNewTabRequested;
    /// <summary>Missing Index Details… of the context menu, with the suggestions to script.</summary>
    public event Action<IReadOnlyList<PlanIndexSuggestion>>? MissingIndexDetailsRequested;
    /// <summary>Asks the percentage for Custom Zoom: receives the current one, null cancels. Replaced by tests.</summary>
    internal Func<int, int?>? CustomZoomPrompt { get; set; }
    public const int MinZoomPercent = 10, MaxZoomPercent = 400;
    internal int OperatorCount => boxes.Count;

    public double Zoom
    {
        get => scale.ScaleX;
        set { double zoom = Math.Clamp(value, MinZoom, MaxZoom); scale.ScaleX = scale.ScaleY = zoom; }
    }

    public void ZoomToFit()
    {
        if (shown.Count == 0 || scroller.ViewportWidth <= 0) return;
        double width = shown.Max(s => s.Canvas.Width), height = shown.Count == 1 ? shown[0].Canvas.Height : 0;
        double fit = (scroller.ViewportWidth - 24) / width;
        if (height > 0 && scroller.ViewportHeight > 90) fit = Math.Min(fit, (scroller.ViewportHeight - 90) / height);
        Zoom = fit;
    }

    public void Clear() => Show([]);

    private StackPanel BuildFindBar()
    {
        System.Windows.Automation.AutomationProperties.SetName(findProperty, "Find node property");
        System.Windows.Automation.AutomationProperties.SetName(findComparison, "Find node comparison");
        System.Windows.Automation.AutomationProperties.SetName(findValue, "Find node value");
        Button Tool(string text, string name, Action act) { var b = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0) }; System.Windows.Automation.AutomationProperties.SetName(b, name); b.Click += (_, _) => act(); return b; }
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(findProperty); bar.Children.Add(findComparison); bar.Children.Add(findValue);
        bar.Children.Add(Tool("◀", "Find previous node", () => FindNode(false)));
        bar.Children.Add(Tool("▶", "Find next node", () => FindNode(true)));
        bar.Children.Add(Tool("✕", "Close Find Node", CloseFind));
        bar.Children.Add(findResult);
        findBar.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNode(Keyboard.Modifiers != ModifierKeys.Shift); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
        return bar;
    }

    /// <summary>Find Node of the context menu: opens the bar above the plan.</summary>
    public void OpenFind()
    {
        string? selected = findProperty.SelectedItem as string;
        var names = ExecutionPlanSearch.PropertyNames(ExecutionPlanSearch.Operators(Statements));
        findProperty.ItemsSource = names;
        findProperty.SelectedItem = selected is not null && names.Contains(selected) ? selected : names.Contains("Node Type") ? "Node Type" : names.FirstOrDefault();
        findResult.Text = "";
        findBar.Visibility = Visibility.Visible;
        findValue.Focus();
    }

    private void CloseFind() { findBar.Visibility = Visibility.Collapsed; Focus(); }
    public bool IsFindOpen => findBar.Visibility == Visibility.Visible;

    /// <summary>Selects and shows the next operator that matches the bar. Returns false when none does.</summary>
    public bool FindNode(bool forward = true)
    {
        if (findProperty.SelectedItem is not string property) return false;
        var comparison = (ExecutionPlanComparison)Math.Max(0, findComparison.SelectedIndex);
        string value = findValue.Text;
        var found = ExecutionPlanSearch.Next(ExecutionPlanSearch.Operators(Statements), SelectedOperator, op => ExecutionPlanSearch.Matches(op, property, comparison, value), forward);
        findResult.Text = found is null ? "No matching node." : "";
        if (found is null) return false;
        Select(found);
        boxes[found].Label.BringIntoView();
        return true;
    }

    /// <summary>Custom Zoom of the context menu.</summary>
    private void CustomZoom()
    {
        int current = (int)Math.Round(Zoom * 100);
        if ((CustomZoomPrompt is { } prompt ? prompt(current) : AskZoom(current)) is { } percent) Zoom = percent / 100.0;
    }

    private int? AskZoom(int current)
    {
        var box = new TextBox { Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 70, MaxLength = 3, TextAlignment = TextAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(box, "Zoom percentage");
        var problem = new TextBlock { Foreground = Frozen(0xB0, 0x00, 0x20), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(20, 3, 20, 3) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Zoom (%):", VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(line); root.Children.Add(problem); root.Children.Add(buttons);
        var dialog = new Window { Title = "Custom Zoom", Content = root, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 260, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = Frozen(0xF5, 0xF5, 0xF5) };
        int? chosen = null;
        ok.Click += (_, _) =>
        {
            chosen = ParseZoom(box.Text);
            if (chosen is null) { problem.Text = $"Enter a whole number from {MinZoomPercent} to {MaxZoomPercent}."; box.Focus(); box.SelectAll(); return; }
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? chosen : null;
    }

    internal static int? ParseZoom(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) && value is >= MinZoomPercent and <= MaxZoomPercent ? value : null;

    public void Show(IReadOnlyList<ExecutionPlanStatement> statements)
    {
        sections.Children.Clear(); shown.Clear(); boxes.Clear(); SelectedOperator = null; Zoom = 1;
        foreach (var statement in statements)
        {
            sections.Children.Add(Header(statement));
            var canvas = Diagram(statement);
            shown.Add((statement, canvas));
            sections.Children.Add(canvas);
        }
        // SSMS selects the statement box of the first plan.
        if (statements.Count > 0) Select(statements[0].Root); else SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Select(ExecutionPlanOperator op)
    {
        if (SelectedOperator is { } previous && boxes.TryGetValue(previous, out var old)) { old.Label.Background = Brushes.Transparent; old.Text.Foreground = Brushes.Black; }
        SelectedOperator = op;
        if (boxes.TryGetValue(op, out var box)) { box.Label.Background = Selection; box.Text.Foreground = Brushes.White; }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string header, string name, Action act, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Name = name, InputGestureText = gesture ?? "" };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
            return item;
        }
        Add("_Save Execution Plan As…", "PlanSaveAs", () => SaveRequested?.Invoke(this, EventArgs.Empty));
        var newTab = Add("Show Execution Plan In New _Tab", "PlanShowInNewTab", () => ShowInNewTabRequested?.Invoke(this, EventArgs.Empty));
        Add("Find _Node", "PlanFindNode", OpenFind);
        var missing = Add("_Missing Index Details…", "PlanMissingIndex", () => { if (MissingIndexTarget is { Count: > 0 } target) MissingIndexDetailsRequested?.Invoke(target); });
        menu.Items.Add(new Separator());
        Add("Zoom _In", "PlanZoomIn", () => Zoom *= ZoomStep);
        Add("Zoom _Out", "PlanZoomOut", () => Zoom /= ZoomStep);
        Add("_Custom Zoom…", "PlanCustomZoom", CustomZoom);
        Add("Zoom to _Fit", "PlanZoomFit", ZoomToFit);
        // As in SSMS, the item is always listed and gray when the plan has no suggestion.
        menu.Opened += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = shown.Count > 0 && (item != missing || MissingIndexTarget.Count > 0);
            // A plan already in a tab of its own has nowhere newer to go: the item shows only where a host opens the tab (the query tab).
            newTab.Visibility = ShowInNewTabRequested is null ? Visibility.Collapsed : Visibility.Visible;
        };
        return menu;
    }

    internal const string MissingIndexTag = "PlanMissingIndexLine";
    private static readonly Brush MissingIndexText = Frozen(0x00, 0x80, 0x00);

    /// <summary>Statement header. Index suggestions follow the text in green, where SSMS 22.11 shows its Missing Index line (M1057).</summary>
    private static Border Header(ExecutionPlanStatement statement)
    {
        TextBox Line(string text, double maxHeight) => new()
        {
            Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = DiagramFont, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(2),
        };
        var lines = new StackPanel();
        lines.Children.Add(Line(statement.Header + "\n" + statement.QueryText, 62));
        if (statement.MissingIndexes.Count > 0)
        {
            var missing = Line(string.Join("\n", statement.MissingIndexes.Select(s => s.HeaderLine)), 48);
            missing.Foreground = MissingIndexText; missing.Tag = MissingIndexTag; missing.Padding = new Thickness(2, 0, 2, 2);
            AutomationProperties.SetName(missing, "Missing index suggestions");
            lines.Children.Add(missing);
        }
        return new Border { BorderBrush = Frozen(0xAB, 0xAD, 0xB3), BorderThickness = new Thickness(1), Margin = new Thickness(4, 4, 4, 0), Background = Frozen(0xFA, 0xFC, 0xFE), Child = lines };
    }

    /// <summary>What Missing Index Details shows: the suggestions of the selected statement, or of the first statement that has any.</summary>
    internal IReadOnlyList<PlanIndexSuggestion> MissingIndexTarget =>
        SelectedStatement is { MissingIndexes.Count: > 0 } selected ? selected.MissingIndexes : shown.Select(s => s.Statement.MissingIndexes).FirstOrDefault(m => m.Count > 0) ?? [];

    private Canvas Diagram(ExecutionPlanStatement statement)
    {
        var elements = new Dictionary<ExecutionPlanOperator, FrameworkElement>();
        void Create(ExecutionPlanOperator op) { elements[op] = Node(op, statement); foreach (var child in op.Children) Create(child); }
        Create(statement.Root);
        Size Measure(ExecutionPlanOperator op) { var element = elements[op]; element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return element.DesiredSize; }
        var places = ExecutionPlanLayout.Place(statement.Root, Measure);

        const double margin = 10;
        var canvas = new Canvas { Width = places.Values.Max(r => r.Right) + margin * 2, Height = places.Values.Max(r => r.Bottom) + margin * 2, LayoutTransform = scale, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var (parent, rect) in places)
            for (int i = 0; i < parent.Children.Count; i++)
                Arrow(canvas, parent.Children[i], rect, places[parent.Children[i]], i, parent.Children.Count, margin);
        foreach (var (op, rect) in places)
        {
            Canvas.SetLeft(elements[op], rect.X + margin);
            Canvas.SetTop(elements[op], rect.Y + margin);
            canvas.Children.Add(elements[op]);
        }
        return canvas;
    }

    private static void Arrow(Canvas canvas, ExecutionPlanOperator child, Rect parent, Rect source, int index, int count, double margin)
    {
        const double head = 7, spacing = 10, busOffset = 22;
        double thickness = ExecutionPlanLayout.ArrowThickness(child.FlowRows);
        // The first input is on the parent's row and arrives straight; the others enter just below it.
        double endX = parent.Right + margin + 3, endY = parent.Top + parent.Height / 2 + margin + index * spacing;
        double startX = source.Left + margin - 3, startY = source.Top + source.Height / 2 + margin;
        double shaftX = endX + head;
        var points = new PointCollection { new(startX, startY) };
        if (Math.Abs(startY - endY) >= 1)
        {
            double bus = Math.Min(startX - 8, shaftX + busOffset + (count - 1 - index) * 8);
            points.Add(new(bus, startY)); points.Add(new(bus, endY));
        }
        points.Add(new(shaftX, endY));
        canvas.Children.Add(new Polyline { Points = points, Stroke = ArrowEdge, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Miter, IsHitTestVisible = false });
        if (thickness > 2.5) canvas.Children.Add(new Polyline { Points = points, Stroke = Brushes.White, StrokeThickness = thickness - 2, StrokeLineJoin = PenLineJoin.Miter, IsHitTestVisible = false });
        // Thin arrows are hard to point at, so the area that shows the tooltip is wider than the line.
        var hover = new Polyline { Points = points, Stroke = Brushes.Transparent, StrokeThickness = Math.Max(thickness, 10) };
        var tip = new ToolTip { Background = TipBack, BorderBrush = Brushes.Black, Padding = new Thickness(6) };
        tip.Opened += (_, _) => tip.Content ??= ArrowCard(child);
        hover.ToolTip = tip;
        AutomationProperties.SetName(hover, "Rows from " + child.Title);
        canvas.Children.Add(hover);
        double wing = Math.Max(4, thickness / 2 + 3);
        canvas.Children.Add(new Polygon { Points = [new(endX, endY), new(shaftX, endY - wing), new(shaftX, endY + wing)], Fill = Brushes.White, Stroke = ArrowEdge, StrokeThickness = 1, IsHitTestVisible = false });
    }

    private FrameworkElement Node(ExecutionPlanOperator op, ExecutionPlanStatement statement)
    {
        var text = new TextBlock { Text = string.Join("\n", op.Lines), FontFamily = DiagramFont, FontSize = 11, TextAlignment = TextAlignment.Center, Foreground = Brushes.Black, LineHeight = 13.5, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        var label = new Border { Child = text, Padding = new Thickness(4, 1, 4, 1), Background = Brushes.Transparent };
        var panel = new StackPanel { Background = Brushes.Transparent, Focusable = true, Cursor = Cursors.Arrow };
        var icon = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 0, 3), HorizontalAlignment = HorizontalAlignment.Center };
        icon.Children.Add(new Viewbox { Child = Icon(op.Icon) });
        if (op.Warnings.Count > 0) icon.Children.Add(WarningSign());
        panel.Children.Add(icon);
        panel.Children.Add(label);
        AutomationProperties.SetName(panel, op.Title + (op.ObjectName is null ? "" : " " + op.ObjectName));
        if (op.Warnings.Count > 0) AutomationProperties.SetHelpText(panel, "Warning: " + string.Join(" ", op.Warnings));
        panel.MouseLeftButtonDown += (_, e) => { Select(op); panel.Focus(); e.Handled = true; };
        var tip = new ToolTip { Background = TipBack, BorderBrush = Brushes.Black, Padding = new Thickness(6) };
        tip.Opened += (_, _) => tip.Content ??= Card(op);
        panel.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(panel, 500);
        ToolTipService.SetShowDuration(panel, 60000);
        boxes[op] = (label, text, statement);
        return panel;
    }

    /// <summary>Rows that an arrow carries, from the operator it leaves.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ArrowRows(ExecutionPlanOperator source)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var rows = new List<KeyValuePair<string, string>>();
        if (source.ActualRows is { } actual) rows.Add(new("Actual Number of Rows", actual.ToString("0.###", culture)));
        rows.Add(new("Estimated Number of Rows", source.EstimatedRows.ToString("0.###", culture)));
        if (source.Properties.TryGetValue("Plan Width", out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float, culture, out double width))
        {
            rows.Add(new("Estimated Row Size", width.ToString("0", culture) + " B"));
            rows.Add(new("Estimated Data Size", (width * source.EstimatedRows).ToString("0", culture) + " B"));
        }
        return rows;
    }

    private static FrameworkElement ArrowCard(ExecutionPlanOperator source)
    {
        var card = new StackPanel { Width = 300 };
        foreach (var (name, value) in ArrowRows(source))
        {
            var row = new DockPanel();
            var right = new TextBlock { Text = value, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            card.Children.Add(new Border { BorderBrush = TipLine, BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
        }
        return card;
    }

    /// <summary>The yellow summary card of an operator, laid out like the SSMS showplan tooltip.</summary>
    internal static FrameworkElement Card(ExecutionPlanOperator op)
    {
        var card = new StackPanel { Width = 390 };
        card.Children.Add(new TextBlock { Text = op.Title, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
        if (op.Description.Length > 0) card.Children.Add(new TextBlock { Text = op.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) });
        foreach (var (name, value) in op.Summary)
        {
            var row = new DockPanel();
            var right = new TextBlock { Text = value, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            card.Children.Add(new Border { BorderBrush = TipLine, BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
        }
        foreach (var detail in op.Details)
        {
            card.Children.Add(new TextBlock { Text = detail.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            card.Children.Add(new TextBlock { Text = detail.Text.Length <= 600 ? detail.Text : detail.Text[..600] + "…", TextWrapping = TextWrapping.Wrap });
        }
        if (op.Warnings.Count > 0)
        {
            card.Children.Add(new TextBlock { Text = "Warnings", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            foreach (string warning in op.Warnings) card.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap });
        }
        return card;
    }

    internal const string WarningSignTag = "PlanWarning";
    private static readonly Brush WarningFill = Frozen(0xFF, 0xE1, 0x1A), WarningEdge = Frozen(0x8A, 0x6D, 0x00);

    /// <summary>The yellow triangle SSMS puts over the lower right corner of an operator's icon when it has warnings (seen in 22.11, M1057).</summary>
    private static FrameworkElement WarningSign()
    {
        var sign = new Grid { Width = 13, Height = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -3), Tag = WarningSignTag, IsHitTestVisible = false };
        sign.Children.Add(new Path { Data = Geometry.Parse("M6.5,0.5 L12.5,11.5 0.5,11.5 Z"), Fill = WarningFill, Stroke = WarningEdge, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round });
        sign.Children.Add(new Path { Data = Geometry.Parse("M6.5,4 L6.5,8 M6.5,9.3 L6.5,10.3"), Stroke = Brushes.Black, StrokeThickness = 1.4 });
        return sign;
    }

    private static Path Icon(ExecutionPlanIcon icon) => new()
    {
        Width = 26, Height = 26, Stretch = Stretch.Uniform, Stroke = IconStroke, Fill = IconFill, StrokeThickness = 1.2,
        Data = Geometry.Parse(icon switch
        {
            ExecutionPlanIcon.Statement => "M2,3 L24,3 24,23 2,23 Z M2,8 L24,8 M2,13 L24,13 M2,18 L24,18 M9,8 L9,23 M16,8 L16,23",
            ExecutionPlanIcon.TableScan => "M2,4 L18,4 18,22 2,22 Z M2,10 L18,10 M2,16 L18,16 M8,4 L8,22 M13,4 L13,22 M20,8 L25,13 20,18 Z",
            ExecutionPlanIcon.IndexScan => "M13,2 L19,8 13,14 7,8 Z M13,14 L13,18 M4,18 L22,18 M4,18 L4,20 M13,18 L13,20 M22,18 L22,20 M1,20 L7,20 7,25 1,25 Z M10,20 L16,20 16,25 10,25 Z M19,20 L25,20 25,25 19,25 Z",
            ExecutionPlanIcon.BitmapScan => "M2,2 L10,2 10,10 2,10 Z M14,2 L24,2 24,10 14,10 Z M2,14 L10,14 10,24 2,24 Z M14,14 L24,14 24,24 14,24 Z",
            ExecutionPlanIcon.Join => "M24,5 L14,5 8,13 M24,21 L14,21 8,13 M8,9 L2,13 8,17 Z M18,2 L18,8 24,5 Z M18,18 L18,24 24,21 Z",
            ExecutionPlanIcon.Sort => "M2,3 L10,3 10,8 2,8 Z M2,11 L14,11 14,16 2,16 Z M2,19 L18,19 18,24 2,24 Z M21,3 L21,20 M18,18 L21,24 24,18 Z",
            ExecutionPlanIcon.Aggregate => "M21,4 L5,4 14,13 5,22 21,22",
            ExecutionPlanIcon.Limit => "M2,6 L16,6 16,20 2,20 Z M21,3 L21,23 M2,11 L16,11 M2,16 L16,16",
            ExecutionPlanIcon.Materialize => "M3,6 C3,1 23,1 23,6 L23,20 C23,25 3,25 3,20 Z M3,6 C3,11 23,11 23,6",
            ExecutionPlanIcon.Append => "M2,3 L24,3 24,10 2,10 Z M2,14 L24,14 24,21 2,21 Z M13,10 L13,14",
            ExecutionPlanIcon.Gather => "M2,4 L9,13 2,22 M24,4 L17,13 24,22 M6,13 L20,13 M13,7 L13,19",
            ExecutionPlanIcon.Modify => "M3,23 L3,17 17,3 23,9 9,23 Z M14,6 L20,12",
            ExecutionPlanIcon.Subquery => "M2,2 L24,2 24,24 2,24 Z M7,8 L19,8 19,18 7,18 Z",
            _ => "M3,3 L23,3 23,23 3,23 Z M13,8 L13,18 M8,13 L18,13",
        }),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
