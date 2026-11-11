using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Npgsql;
using PGMS.PostgreSql;
using Path = System.Windows.Shapes.Path;

namespace PGMS.Desktop;

/// <summary>A row of the Criteria pane.</summary>
public sealed class CriterionRow
{
    public string Column { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Table { get; set; } = "";
    public bool Output { get; set; } = true;
    public string SortType { get; set; } = "";
    public string SortOrder { get; set; } = "";
    public string GroupBy { get; set; } = "";
    public string Filter { get; set; } = "";
    public string Or1 { get; set; } = "";
    public string Or2 { get; set; } = "";
    public string Or3 { get; set; } = "";
    public string NewValue { get; set; } = "";
    public string Append { get; set; } = "";
}

/// <summary>A table in the Diagram pane: the source of the query, what the catalog says about it, and where it is.</summary>
public sealed class DesignerSourceBox(DesignSource source, DesignSourceInfo info)
{
    public DesignSource Source { get; } = source;
    public DesignSourceInfo Info { get; } = info;
    public double X { get; set; }
    public double Y { get; set; }
    public bool NameOnly { get; set; }
    public string Reference => Source.Reference;
}

public sealed record DesignerHit(DesignerSourceBox Box, string? Column);
public sealed record DesignerJoinLine(string Left, string Right);

/// <summary>The panes of the View Designer and of the Query Designer dialog of SSMS 22.11 (docs/MARCO-M1046.md, MARCO-M1047.md): Diagram, Criteria, SQL and Results, kept in step.
/// The SQL pane holds what runs, what is saved and what goes to the editor; a query the panes cannot show stays there untouched.</summary>
public sealed class QueryDesignerControl : UserControl
{
    private static readonly string[] GroupChoices = [QueryDesign.GroupBy, "Sum", "Avg", "Min", "Max", "Count", QueryDesign.Expression, QueryDesign.Where];
    private static readonly string[] SortChoices = ["", "Ascending", "Descending"];
    private static readonly Brush TitleBrush = Frozen("#DDE3EC"), FrameBrush = Frozen("#7A7A7A"), LineBrush = Frozen("#505050"), BannerBrush = Frozen("#FFF4CE"), SelectedFrameBrush = Frozen("#0078D7"), SelectedTitleBrush = Frozen("#CCE4F7");
    private static Brush Frozen(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }

    private readonly QueryDesignService service;
    private QueryDesign design = new();
    private readonly List<DesignerSourceBox> boxes = [];
    private readonly Dictionary<DesignerSourceBox, Border> visuals = [];
    private const double SurfaceWidth = 1400, SurfaceHeight = 700;
    private readonly Canvas surface = new() { Background = Brushes.White, Width = SurfaceWidth, Height = SurfaceHeight, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Canvas linesLayer = new(), tablesLayer = new();
    private readonly ContextMenu surfaceMenu = new();
    private readonly DataGrid criteria = new() { AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.All, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader, RowHeaderWidth = 24, GridLinesVisibility = DataGridGridLinesVisibility.All, HorizontalGridLinesBrush = Frozen("#CCCEDB"), VerticalGridLinesBrush = Frozen("#CCCEDB"), Background = Brushes.White, RowBackground = Brushes.White };
    private readonly TextEditor sqlEditor = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13, ShowLineNumbers = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DataGrid results = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader };
    private readonly TextBlock resultStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 6, 2) }, changedMark = new() { Text = "Query Changed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 6, 2), Visibility = Visibility.Collapsed };
    private readonly TextBlock banner = new() { Background = BannerBrush, Padding = new Thickness(6, 3, 6, 3), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Text = "The query cannot be represented graphically in the Diagram and Criteria panes. Edit it in the SQL pane." };
    private readonly Grid panes = new();
    private readonly ToggleButton[] paneButtons = new ToggleButton[4];
    private readonly ToggleButton groupButton = new() { Content = "Add Group By", Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button cancelButton = new() { Content = "Cancel", Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly UIElement[] paneElements;
    private readonly DataGridColumn groupColumn, aliasColumn, tableColumn, sortTypeColumn, sortOrderColumn, newValueColumn, appendColumn, filterColumn, or1Column, or2Column, or3Column;
    private readonly DataGridCheckBoxColumn outputColumn;
    private readonly GridSplitter[] splitters = new GridSplitter[3];
    private readonly double[] paneWeights;
    private readonly bool queryDialog;
    private readonly Stack<string> undoStack = new(), redoStack = new();
    private bool restoring;
    private ObservableCollection<CriterionRow> rows = [];
    private CancellationTokenSource? operation;
    private bool representable = true, syncing, gridPending;
    private string lastGoodSql = "";
    private DesignerHit? dragColumn, moveBox;
    private DesignerSourceBox? selectedBox;
    private Point dragStart, moveOrigin;
    private object? menuTarget;

    /// <summary>The SQL or the panes changed.</summary>
    public event Action? Changed;
    public event Action<string>? Feedback;
    internal Func<string, bool>? DifferPrompt { get; set; }
    internal Action<string>? NoticePrompt { get; set; }
    internal Action<Window>? DialogShown { get; set; }
    internal Func<IReadOnlyList<DiagramTableRef>, DiagramTableRef?>? TargetPrompt { get; set; }
    internal Func<string?>? MakeTablePrompt { get; set; }
    /// <summary>The last SQL left in the pane was refused and put back (Undo in Query Definitions Differ).</summary>
    internal bool SqlPutBack { get; private set; }

    /// <param name="queryDialog">The Query Designer dialog: three panes, no toolbar, no Results, Change Type in the menus and INSERT, UPDATE and DELETE allowed.</param>
    public QueryDesignerControl(string connection, bool queryDialog = false)
    {
        this.queryDialog = queryDialog;
        paneWeights = queryDialog ? [3, 1.1, 1.5, 1] : [1, 1, 1, 1];
        service = new(connection);
        Focusable = true;
        sqlEditor.SyntaxHighlighting = QueryView.SqlDefinition;
        AutomationProperties.SetName(sqlEditor, "SQL pane"); AutomationProperties.SetName(criteria, "Criteria pane"); AutomationProperties.SetName(surface, "Diagram pane"); AutomationProperties.SetName(results, "Results pane");
        surface.Children.Add(linesLayer); surface.Children.Add(tablesLayer);
        surface.ContextMenu = surfaceMenu;
        surface.MouseLeftButtonDown += SurfaceDown; surface.MouseMove += SurfaceMove; surface.MouseLeftButtonUp += SurfaceUp;
        surface.Focusable = true; surface.FocusVisualStyle = null;
        surface.KeyDown += (_, e) => { if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && DeleteSelectedTable()) e.Handled = true; };
        surface.PreviewMouseRightButtonDown += (_, e) => { menuTarget = Tagged(e.OriginalSource as DependencyObject); surface.Focus(); SelectBox((menuTarget as DesignerHit)?.Box); };
        surface.ContextMenuOpening += (_, _) => FillMenu(surfaceMenu, menuTarget);
        sqlEditor.LostKeyboardFocus += (_, _) => _ = SqlLeftAsync();
        sqlEditor.TextChanged += (_, _) => { if (!syncing) MarkChanged(); };
        ResultGridStyle.Apply(results);

        var bindOutput = new DataGridCheckBoxColumn { Header = "Output", Binding = new Binding(nameof(CriterionRow.Output)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 52 };
        DataGridColumn Text(string header, string path, double width) => new DataGridTextColumn { Header = header, Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = width };
        DataGridComboBoxColumn Combo(string header, string path, string[] items) => new() { Header = header, TextBinding = new Binding(path), ItemsSource = items, Width = 92 };
        outputColumn = bindOutput;
        groupColumn = Combo("Group By", nameof(CriterionRow.GroupBy), GroupChoices);
        groupColumn.Visibility = Visibility.Collapsed;
        aliasColumn = Text("Alias", nameof(CriterionRow.Alias), 70); tableColumn = Text("Table", nameof(CriterionRow.Table), 80);
        newValueColumn = Text("New Value", nameof(CriterionRow.NewValue), 100); newValueColumn.Visibility = Visibility.Collapsed;
        appendColumn = Text("Append", nameof(CriterionRow.Append), 90); appendColumn.Visibility = Visibility.Collapsed;
        sortTypeColumn = Combo("Sort Type", nameof(CriterionRow.SortType), SortChoices); sortOrderColumn = Text("Sort Order", nameof(CriterionRow.SortOrder), 70);
        filterColumn = Text("Filter", nameof(CriterionRow.Filter), 100); or1Column = Text("Or...", nameof(CriterionRow.Or1), 90); or2Column = Text("Or...", nameof(CriterionRow.Or2), 90); or3Column = Text("Or...", nameof(CriterionRow.Or3), 90);
        foreach (var column in new[] { Text("Column", nameof(CriterionRow.Column), 120), aliasColumn, tableColumn, bindOutput, newValueColumn, appendColumn, sortTypeColumn, sortOrderColumn, groupColumn, filterColumn, or1Column, or2Column, or3Column })
            criteria.Columns.Add(column);
        criteria.CellEditEnding += (_, _) => ScheduleGridRead();
        criteria.RowEditEnding += (_, _) => ScheduleGridRead();
        criteria.CurrentCellChanged += (_, _) => ScheduleGridRead();
        // A checkbox commits only when the row ends; the SQL should follow the click.
        bindOutput.ElementStyle = new Style(typeof(CheckBox)) { Setters = { new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center), new EventSetter(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => ScheduleGridRead())) } };

        var resultsPanel = new DockPanel();
        var resultBar = new DockPanel { Background = Frozen("#F5F5F5") };
        DockPanel.SetDock(changedMark, Dock.Right); resultBar.Children.Add(changedMark); resultBar.Children.Add(resultStatus);
        DockPanel.SetDock(resultBar, Dock.Bottom); resultsPanel.Children.Add(resultBar); resultsPanel.Children.Add(results);
        var diagram = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = surface, Background = Brushes.White };
        paneElements = [diagram, criteria, sqlEditor, resultsPanel];
        for (int i = 0; i < 4; i++)
        {
            panes.RowDefinitions.Add(new() { Height = new GridLength(paneWeights[i], GridUnitType.Star), MinHeight = 0 });
            Grid.SetRow(paneElements[i], i * 2); panes.Children.Add(paneElements[i]);
            if (i < 3)
            {
                panes.RowDefinitions.Add(new() { Height = new GridLength(5) });
                var splitter = splitters[i] = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Frozen("#E6E7E8"), ResizeBehavior = GridResizeBehavior.PreviousAndNext };
                Grid.SetRow(splitter, i * 2 + 1); panes.Children.Add(splitter);
            }
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        string[] names = ["Diagram", "Criteria", "SQL", "Results"];
        for (int i = 0; i < 4; i++)
        {
            int pane = i;
            var button = paneButtons[i] = new ToggleButton { Content = names[i], IsChecked = true, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0), ToolTip = "Show " + names[i] + " Pane" };
            AutomationProperties.SetName(button, "Show " + names[i] + " Pane");
            button.Click += (_, _) => SetPane(pane, button.IsChecked == true);
            bar.Children.Add(button);
        }
        Button Tool(string text, string tip, Action click, double left = 6) { var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(left, 0, 0, 0) }; AutomationProperties.SetName(b, tip); b.Click += (_, _) => click(); bar.Children.Add(b); return b; }
        Tool("Execute SQL", "Execute SQL (Ctrl+R)", () => _ = ExecuteAsync(), 12);
        Tool("Verify SQL", "Verify SQL Syntax", () => _ = VerifyAsync());
        groupButton.ToolTip = "Add Group By"; groupButton.Click += (_, _) => SetGrouped(groupButton.IsChecked == true); bar.Children.Add(groupButton);
        Tool("Add Table", "Add Table", ShowAddTable);
        cancelButton.Click += (_, _) => operation?.Cancel(); bar.Children.Add(cancelButton);

        var root = new DockPanel { Background = Brushes.White };
        var barHost = new Border { Background = Frozen("#EEF0F5"), BorderBrush = Frozen("#CCCEDB"), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost);
        DockPanel.SetDock(banner, Dock.Top); root.Children.Add(banner);
        root.Children.Add(panes);
        Content = root;
        PreviewKeyDown += (_, e) => { if (!queryDialog && e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = ExecuteAsync(); } };
        if (queryDialog)
        {
            barHost.Visibility = Visibility.Collapsed;
            SetPane(3, false);
            criteria.ContextMenu = new ContextMenu(); criteria.ContextMenuOpening += (_, _) => FillGridMenu(criteria.ContextMenu);
            sqlEditor.ContextMenu = new ContextMenu(); sqlEditor.ContextMenuOpening += (_, _) => FillSqlMenu(sqlEditor.ContextMenu);
        }
        ShowRows();
    }

    public string Sql { get => sqlEditor.Text; set => SetSqlText(value); }
    public bool IsRepresentable => representable;
    public bool IsBusy => operation is not null;
    public QueryDesign Design => design;
    public IReadOnlyList<DesignerSourceBox> Sources => boxes;
    public IReadOnlyList<CriterionRow> Rows => rows;
    public bool IsQueryChangedShown => changedMark.Visibility == Visibility.Visible;
    public string ResultStatus => resultStatus.Text;
    internal DataGrid ResultsGrid => results;
    internal DataGrid CriteriaGrid => criteria;
    internal Canvas Surface => surface;
    internal Canvas Lines => linesLayer;
    internal Border BoxOf(DesignerSourceBox box) => visuals[box];
    public bool IsPaneVisible(int pane) => paneElements[pane].Visibility == Visibility.Visible;
    public DesignerSourceBox? SelectedBox => selectedBox;
    public DesignStatement Statement => design.Statement;
    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;
    internal bool IsColumnShown(string header) => criteria.Columns.Any(c => (string)c.Header == header && c.Visibility == Visibility.Visible);

    private void Say(string text) => Feedback?.Invoke(text);
    private static string Message(Exception ex) => ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
    public void Shutdown() => operation?.Cancel();
    private void SetSqlText(string text) { syncing = true; sqlEditor.Text = text; syncing = false; }
    private void MarkChanged() { if (results.ItemsSource is not null) changedMark.Visibility = Visibility.Visible; Changed?.Invoke(); }

    // ---- Panes ----
    public void SetPane(int pane, bool visible)
    {
        paneButtons[pane].IsChecked = visible;
        paneElements[pane].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        panes.RowDefinitions[pane * 2].Height = visible ? new GridLength(paneWeights[pane], GridUnitType.Star) : new GridLength(0);
        // A divider stands only between two panes that are showing.
        for (int i = 0; i < splitters.Length; i++)
        {
            if (splitters[i] is not { } splitter) continue;
            bool between = paneElements[i].Visibility == Visibility.Visible && Enumerable.Range(i + 1, paneElements.Length - i - 1).Any(j => paneElements[j].Visibility == Visibility.Visible);
            splitter.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
            panes.RowDefinitions[i * 2 + 1].Height = new GridLength(between ? 5 : 0);
        }
    }

    public void ClearResults() { results.ItemsSource = null; results.Columns.Clear(); resultStatus.Text = ""; changedMark.Visibility = Visibility.Collapsed; }

    // ---- Reading SQL into the panes ----
    /// <summary>Shows a statement as the designer starts. One the panes cannot show opens in the SQL pane only, with no prompt.</summary>
    public async Task LoadAsync(string sql)
    {
        lastGoodSql = sql; SetSqlText(sql);
        if (sql.Trim().Length == 0) { ShowEmpty(); return; }
        if (await ApplyAsync(sql) is { } problem) { SetRepresentable(false); Say(problem); }
        else if (representable) { lastGoodSql = QueryDesignSql.Generate(design); SetSqlText(lastGoodSql); }
    }
    private void ShowEmpty() { design = new(); boxes.Clear(); selectedBox = null; SetRepresentable(true); lastGoodSql = QueryDesignSql.Generate(design); SetSqlText(lastGoodSql); DrawAll(); ShowRows(); }

    // ---- Undo and Redo of the Query Designer: each step is the SQL before it, read back into the panes ----
    public async Task UndoAsync() { if (undoStack.Count > 0) await RestoreAsync(undoStack.Pop(), redoStack); }
    public async Task RedoAsync() { if (redoStack.Count > 0) await RestoreAsync(redoStack.Pop(), undoStack); }

    private async Task RestoreAsync(string sql, Stack<string> other)
    {
        other.Push(lastGoodSql);
        restoring = true;
        try
        {
            if (sql == QueryDesignSql.Generate(new QueryDesign())) ShowEmpty();
            else
            {
                if (await ApplyAsync(sql) is { } problem) { Say(problem); return; }
                lastGoodSql = sql; SetSqlText(sql);
            }
            MarkChanged();
        }
        finally { restoring = false; }
    }

    private void Remember(string previous)
    {
        if (!queryDialog || restoring || previous == lastGoodSql) return;
        undoStack.Push(previous); redoStack.Clear();
    }

    private void SetRepresentable(bool value)
    {
        representable = value;
        banner.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        paneElements[0].IsEnabled = paneElements[1].IsEnabled = groupButton.IsEnabled = value;
        // The dialog shows only what still applies: a statement the panes cannot read is the SQL pane alone.
        if (queryDialog) { SetPane(0, value); SetPane(1, value); }
    }

    /// <summary>Takes the panes from the SQL. Null when they now show it; otherwise why they cannot, and the panes stay as they were.</summary>
    private async Task<string?> ApplyAsync(string sql)
    {
        var parsed = QueryDesignSql.Parse(sql, out string? error);
        if (parsed is null) return error ?? "Unable to parse query text.";
        if (!queryDialog && parsed.Statement != DesignStatement.Select) return "A view is a single SELECT statement.";
        if (parsed.Criteria.Any(c => c.Filters.Count > 4)) return "This query has more alternatives than the Or... columns can show.";
        var read = new List<DesignerSourceBox>();
        try
        {
            foreach (var source in parsed.Sources)
            {
                if (await service.ReadSourceAsync(source.Schema, source.Name) is not { } info) return $"The table {source.Name} was not found.";
                var old = boxes.FirstOrDefault(b => b.Reference == source.Reference && b.Info.Oid == info.Oid);
                read.Add(new(source, info) { X = old?.X ?? 0, Y = old?.Y ?? 0, NameOnly = old?.NameOnly ?? false });
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or ArgumentException) { return Message(ex); }
        // Columns written without a table belong to the one source that has them, so their boxes show them ticked.
        foreach (var c in parsed.Criteria.Where(c => c.Source.Length == 0))
            if (read.Where(b => b.Info.Columns.Any(x => x.Name == c.Expression)).ToArray() is [var only]) c.Source = only.Reference;
        string? selectedReference = selectedBox?.Reference;
        design = parsed; boxes.Clear(); boxes.AddRange(read);
        selectedBox = boxes.FirstOrDefault(b => b.Reference == selectedReference);
        SetRepresentable(true);
        DrawAll();
        PlaceNewBoxes();
        ShowRows();
        return null;
    }

    private async Task SqlLeftAsync()
    {
        if (sqlEditor.Text == lastGoodSql) return;
        await ApplySqlAsync(sqlEditor.Text);
    }

    /// <summary>The SQL pane is left with other text: the panes follow, or the choice of SSMS when they cannot.</summary>
    public async Task ApplySqlAsync(string text)
    {
        if (sqlEditor.Text != text) SetSqlText(text);
        string previous = lastGoodSql;
        if (await ApplyAsync(text) is not { } problem) { lastGoodSql = text; Remember(previous); return; }
        bool ignore = DifferPrompt?.Invoke(problem) ?? ShowDiffer(problem);
        if (ignore) { lastGoodSql = text; SetRepresentable(false); }
        else { SetSqlText(lastGoodSql); SqlPutBack = true; Changed?.Invoke(); }
    }

    private bool ShowDiffer(string problem)
    {
        var ignore = new Button { Content = "_Ignore", Width = 86, Margin = new Thickness(0, 0, 8, 0) };
        var undo = new Button { Content = "_Undo", Width = 86, IsDefault = true, IsCancel = true };
        var dialog = new Window
        {
            Title = "Query Definitions Differ", Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Owner = Window.GetWindow(this),
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock { Text = "The following errors were encountered while parsing the contents of the SQL pane:", TextWrapping = TextWrapping.Wrap },
                    new TextBox { Text = problem, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 70, Margin = new Thickness(0, 8, 0, 8) },
                    new TextBlock { Text = "The query cannot be represented graphically in the Diagram and Criteria Pane.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ignore, undo } }
                }
            }
        };
        ignore.Click += (_, _) => dialog.DialogResult = true;
        DialogShown?.Invoke(dialog);
        return dialog.ShowDialog() == true;
    }

    /// <summary>The panes changed: the SQL is written again.</summary>
    private void Regenerate()
    {
        if (!representable) return;
        string previous = lastGoodSql;
        lastGoodSql = QueryDesignSql.Generate(design);
        Remember(previous);
        SetSqlText(lastGoodSql);
        MarkChanged();
    }

    // ---- Criteria ----
    private void ShowRows()
    {
        syncing = true;
        if (rows is not null) rows.CollectionChanged -= RowsChanged;
        rows = new(design.Criteria.Select(c => new CriterionRow
        {
            Column = c.Expression, Alias = c.Alias, Table = c.Source, Output = c.Output, SortType = c.Sort == DesignSort.None ? "" : c.Sort.ToString(), SortOrder = c.Sort == DesignSort.None || c.SortOrder == 0 ? "" : c.SortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GroupBy = c.Grouping, Filter = c.Filters.ElementAtOrDefault(0) ?? "", Or1 = c.Filters.ElementAtOrDefault(1) ?? "", Or2 = c.Filters.ElementAtOrDefault(2) ?? "", Or3 = c.Filters.ElementAtOrDefault(3) ?? "", NewValue = c.NewValue, Append = c.Append
        }));
        rows.CollectionChanged += RowsChanged;
        criteria.ItemsSource = rows;
        ShowColumns();
        groupButton.IsChecked = design.Grouped;
        syncing = false;
    }

    /// <summary>The Criteria columns of each statement, as measured in the Query Designer of SSMS 22.11.</summary>
    private void ShowColumns()
    {
        static Visibility On(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        var kind = design.Statement;
        bool selects = kind is DesignStatement.Select or DesignStatement.InsertResults or DesignStatement.MakeTable;
        aliasColumn.Visibility = sortTypeColumn.Visibility = sortOrderColumn.Visibility = On(selects);
        tableColumn.Visibility = filterColumn.Visibility = or1Column.Visibility = or2Column.Visibility = or3Column.Visibility = On(kind != DesignStatement.InsertValues);
        outputColumn.Visibility = On(selects || kind == DesignStatement.Update);
        outputColumn.Header = kind == DesignStatement.Update ? "Set" : "Output";
        newValueColumn.Visibility = On(kind is DesignStatement.Update or DesignStatement.InsertValues);
        appendColumn.Visibility = On(kind == DesignStatement.InsertResults);
        groupColumn.Visibility = On(selects && design.Grouped);
    }

    /// <summary>Change Type: the rows that still mean something are kept, and the statement is written again. Nothing runs.</summary>
    public async Task ChangeTypeAsync(DesignStatement kind)
    {
        if (!representable || design.Statement == kind) return;
        if (kind is DesignStatement.Update or DesignStatement.Delete or DesignStatement.InsertValues && design.Sources.Count != 1) { Notice("Update, Delete and Insert Values work on one table. Remove the other tables from the Diagram pane first."); return; }
        DesignSource? target = design.Target;
        if (kind == DesignStatement.InsertResults)
        {
            IReadOnlyList<DiagramTableRef> relations;
            try { relations = await service.ListRelationsAsync(); }
            catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException) { Say("The tables could not be read: " + Message(ex)); return; }
            if ((TargetPrompt ?? (list => ChooseTargetDialog.Show(Window.GetWindow(this), list)))(relations) is not { } chosen) return;
            target = new(chosen.Schema, chosen.Name, null);
        }
        else if (kind == DesignStatement.MakeTable)
        {
            string? typed = (MakeTablePrompt ?? (() => NamePrompt.Show(Window.GetWindow(this), "Make Table", "Table name:", "")))()?.Trim();
            if (string.IsNullOrEmpty(typed)) return;
            int dot = typed.IndexOf('.');
            target = dot > 0 ? new(typed[..dot], typed[(dot + 1)..], null) : new(null, typed, null);
        }
        else target = null;
        bool wasSelect = design.Statement is DesignStatement.Select or DesignStatement.InsertResults or DesignStatement.MakeTable;
        bool willSelect = kind is DesignStatement.Select or DesignStatement.InsertResults or DesignStatement.MakeTable;
        if (!willSelect)
        {
            design.Grouped = false; design.Distinct = false;
            design.Criteria.RemoveAll(c => c.Expression == "*" || c.Source.Length == 0 && kind != DesignStatement.Delete);
            design.Criteria.RemoveAll(c => kind switch
            {
                DesignStatement.Delete or DesignStatement.Update => !c.Output && c.Filters.All(f => f.Trim().Length == 0) || kind == DesignStatement.Delete && c.Filters.All(f => f.Trim().Length == 0),
                _ => !c.Output
            });
            foreach (var c in design.Criteria)
            {
                c.Sort = DesignSort.None; c.SortOrder = 0; c.Grouping = ""; c.Alias = ""; c.Append = "";
                if (kind == DesignStatement.Delete) { c.Output = false; c.NewValue = ""; }
                if (kind == DesignStatement.InsertValues) c.Filters.Clear();
                if (wasSelect) c.NewValue = "";
            }
        }
        else if (!wasSelect) foreach (var c in design.Criteria) c.NewValue = "";
        if (kind != DesignStatement.InsertResults) foreach (var c in design.Criteria) c.Append = "";
        design.Statement = kind; design.Target = target;
        Regenerate(); DrawTables(); ShowRows();
    }

    private void Notice(string text) { if (NoticePrompt is { } prompt) prompt(text); else MessageBox.Show(Window.GetWindow(this)!, text, "PGMS"); }
    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e) { if (!syncing) ScheduleGridRead(); }

    private void ScheduleGridRead()
    {
        if (syncing || gridPending) return;
        gridPending = true;
        Dispatcher.BeginInvoke(() => { gridPending = false; ReadGrid(); }, DispatcherPriority.Background);
    }

    /// <summary>The grid is read back into the design. The rows are not rebuilt here, so a cell being typed in stays open.</summary>
    internal void ReadGrid()
    {
        if (syncing || !representable) return;
        var read = new List<DesignCriterion>();
        int next = rows.Select(r => int.TryParse(r.SortOrder, out int n) ? n : 0).DefaultIfEmpty(0).Max();
        foreach (var row in rows.Where(r => r.Column.Trim().Length > 0))
        {
            bool known = design.Sources.Any(s => s.Reference == row.Table.Trim());
            var sort = row.SortType == "Ascending" ? DesignSort.Ascending : row.SortType == "Descending" ? DesignSort.Descending : DesignSort.None;
            int order = int.TryParse(row.SortOrder, out int parsedOrder) ? parsedOrder : 0;
            if (sort != DesignSort.None && order == 0) { order = ++next; row.SortOrder = order.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            var filters = new List<string> { row.Filter, row.Or1, row.Or2, row.Or3 };
            while (filters.Count > 0 && filters[^1].Trim().Length == 0) filters.RemoveAt(filters.Count - 1);
            read.Add(new() { Expression = row.Column.Trim(), Source = known ? row.Table.Trim() : "", Alias = row.Alias.Trim(), Output = design.Statement == DesignStatement.InsertValues || row.Output && design.Statement != DesignStatement.Delete, Sort = sort, SortOrder = sort == DesignSort.None ? 0 : order, Grouping = design.Grouped ? row.GroupBy : "", Filters = filters, NewValue = row.NewValue, Append = row.Append });
        }
        if (Signature(read) == Signature(design.Criteria)) return;
        design.Criteria.Clear(); design.Criteria.AddRange(read);
        Regenerate();
        DrawTables();
    }
    private static string Signature(IEnumerable<DesignCriterion> list) => string.Join("\u0001", list.Select(c => string.Join("\u0002", c.Expression, c.Source, c.Alias, c.Output, c.Sort, c.SortOrder, c.Grouping, string.Join("\u0003", c.Filters), c.NewValue, c.Append)));

    public void SetGrouped(bool grouped)
    {
        if (!representable) return;
        design.Grouped = grouped;
        foreach (var c in design.Criteria) c.Grouping = grouped ? QueryDesign.GroupBy : "";
        Regenerate(); DrawTables(); ShowRows();
    }

    // ---- Diagram ----
    private void DrawAll() { DrawTables(); }

    private void DrawTables()
    {
        tablesLayer.Children.Clear(); visuals.Clear();
        foreach (var box in boxes)
        {
            var visual = CreateBox(box);
            visuals[box] = visual; tablesLayer.Children.Add(visual);
            Canvas.SetLeft(visual, box.X); Canvas.SetTop(visual, box.Y);
            visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        ShowSelection();
        FitSurface();
        DrawLines();
    }

    /// <summary>The Diagram pane grows to hold every table, so none is cut off; the pane scrolls.</summary>
    private void FitSurface()
    {
        var all = boxes.Where(visuals.ContainsKey).Select(Bounds).ToArray();
        surface.Width = Math.Max(SurfaceWidth, all.Length == 0 ? 0 : all.Max(r => r.Right) + 40);
        surface.Height = Math.Max(SurfaceHeight, all.Length == 0 ? 0 : all.Max(r => r.Bottom) + 40);
    }

    /// <summary>Arrange Tables, with the grid of the Database Diagrams: the order with the fewest crossing lines, and lines clear of the tables.</summary>
    public void ArrangeTables()
    {
        if (boxes.Count == 0) return;
        DrawTables();
        var links = design.Joins.Select(j => (boxes.FindIndex(b => b.Reference == j.Left), boxes.FindIndex(b => b.Reference == j.Right))).Where(l => l.Item1 >= 0 && l.Item2 >= 0 && l.Item1 != l.Item2).Distinct().ToArray();
        var at = DiagramArrange.Arrange(boxes.Select(b => Bounds(b).Size).ToArray(), links, 20, 20);
        for (int i = 0; i < boxes.Count; i++) { boxes[i].X = at[i].X; boxes[i].Y = at[i].Y; }
        DrawTables();
        (surface.Parent as ScrollViewer)?.ScrollToHome();
    }

    /// <summary>Marks the selected table. The boxes are not rebuilt, so the element under the mouse stays in place.</summary>
    internal void SelectBox(DesignerSourceBox? box) { selectedBox = box; ShowSelection(); }

    private void ShowSelection()
    {
        foreach (var (box, visual) in visuals)
        {
            bool selected = box == selectedBox;
            visual.BorderBrush = selected ? SelectedFrameBrush : FrameBrush;
            visual.BorderThickness = new Thickness(selected ? 2 : 1);
            visual.Margin = new Thickness(selected ? -1 : 0);
            if (visual.Child is StackPanel { Children: [Border title, ..] }) title.Background = selected ? SelectedTitleBrush : TitleBrush;
        }
    }

    /// <summary>Delete: the selected table leaves the query, with its joins and rows. The table in the database is not touched.</summary>
    internal bool DeleteSelectedTable()
    {
        if (selectedBox is not { } box || !representable) return false;
        RemoveSource(box);
        return true;
    }

    private Rect Bounds(DesignerSourceBox box) => new(box.X, box.Y, visuals[box].DesiredSize.Width, visuals[box].DesiredSize.Height);

    /// <summary>Tables that have no place yet go to the right of the ones that do; those keep theirs.</summary>
    private void PlaceNewBoxes()
    {
        var fresh = boxes.Where(b => b.X == 0 && b.Y == 0).ToArray();
        if (fresh.Length == 0) return;
        // A statement read into the Query Designer has no positions at all: it opens arranged.
        if (queryDialog && fresh.Length == boxes.Count && boxes.Count > 1) { ArrangeTables(); return; }
        var placed = boxes.Except(fresh).Select(Bounds).ToArray();
        double x = placed.Length == 0 ? 20 : placed.Max(r => r.Right) + 40;
        foreach (var box in fresh) { box.X = x; box.Y = 10; x += Bounds(box).Width + 40; }
        DrawAll();
    }

    private Border CreateBox(DesignerSourceBox box)
    {
        var stack = new StackPanel();
        var title = new Border { Background = TitleBrush, Padding = new Thickness(6, 3, 8, 3), Tag = new DesignerHit(box, null), Cursor = Cursors.SizeAll, Child = new TextBlock { Text = box.Reference, FontWeight = FontWeights.SemiBold } };
        stack.Children.Add(title);
        if (!box.NameOnly)
        {
            stack.Children.Add(ColumnRow(box, "*", "* (All Columns)", false));
            foreach (var column in box.Info.Columns) stack.Children.Add(ColumnRow(box, column.Name, column.Name, column.IsPrimaryKey));
        }
        var visual = new Border { BorderBrush = FrameBrush, BorderThickness = new Thickness(1), Background = Brushes.White, Child = stack, MinWidth = 130, MaxWidth = 280, Tag = new DesignerHit(box, null) };
        AutomationProperties.SetName(visual, "Table " + box.Reference);
        return visual;
    }

    private FrameworkElement ColumnRow(DesignerSourceBox box, string column, string caption, bool primaryKey)
    {
        var row = new DockPanel { Height = 18, Background = Brushes.White, Tag = new DesignerHit(box, column) };
        var found = design.Criteria.FirstOrDefault(c => c.Source == box.Reference && c.Expression == column);
        // Delete marks no columns; Update and Insert Values mark real columns only, with their own glyphs (measured in SSMS 22.11).
        bool hasCheck = design.Statement switch { DesignStatement.Delete => false, DesignStatement.Update or DesignStatement.InsertValues => column != "*", _ => true };
        if (hasCheck)
        {
            var check = new CheckBox { IsChecked = found is { Output: true }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            AutomationProperties.SetName(check, "Output " + box.Reference + "." + column);
            check.Click += (_, _) => SetOutput(box, column, check.IsChecked == true);
            if (column != "*" && design.Statement switch { DesignStatement.Update => "\u270E", DesignStatement.InsertValues or DesignStatement.InsertResults => "+", _ => null } is { } glyph) check.Template = GlyphCheck(glyph);
            row.Children.Add(check);
        }
        else row.Children.Add(new FrameworkElement { Width = 21 });
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(marks, Dock.Right);
        foreach (var c in design.Criteria.Where(c => c.Source == box.Reference && c.Expression == column))
        {
            if (c.Filters.Any(f => f.Trim().Length > 0) && marks.Children.Count == 0) marks.Children.Add(new Path { Data = Geometry.Parse("M0,0 H10 L6,5 V10 L4,9 V5 Z"), Stroke = Brushes.Black, Fill = Brushes.Transparent, ToolTip = "Filtered", Margin = new Thickness(0, 0, 3, 0) });
            if (c.Grouping is QueryDesign.GroupBy) marks.Children.Add(new TextBlock { Text = "{=", FontWeight = FontWeights.Bold, FontSize = 10, ToolTip = "Grouped" });
            if (c.Sort != DesignSort.None) marks.Children.Add(new TextBlock { Text = c.Sort == DesignSort.Ascending ? "↑" : "↓", FontSize = 10, ToolTip = c.Sort == DesignSort.Ascending ? "Sorted ascending" : "Sorted descending", Margin = new Thickness(2, 0, 0, 0) });
        }
        row.Children.Add(marks);
        row.Children.Add(new TextBlock { Text = caption, FontWeight = primaryKey ? FontWeights.Bold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    /// <summary>A check box that shows a glyph instead of the tick: the pencil of Update and the plus of the INSERT statements.</summary>
    private static ControlTemplate GlyphCheck(string glyph)
    {
        var mark = new FrameworkElementFactory(typeof(TextBlock), "Mark");
        mark.SetValue(TextBlock.TextProperty, glyph); mark.SetValue(TextBlock.FontSizeProperty, 11d); mark.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        mark.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); mark.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(VisibilityProperty, Visibility.Hidden);
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(WidthProperty, 13d); frame.SetValue(HeightProperty, 13d);
        frame.SetValue(Border.BorderBrushProperty, FrameBrush); frame.SetValue(Border.BorderThicknessProperty, new Thickness(1)); frame.SetValue(Border.BackgroundProperty, Brushes.White);
        frame.AppendChild(mark);
        var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = frame };
        template.Triggers.Add(new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true, Setters = { new Setter(VisibilityProperty, Visibility.Visible, "Mark") } });
        return template;
    }

    private void DrawLines()
    {
        linesLayer.Children.Clear();
        var pairs = design.Joins.Select(j => new DesignerJoinLine(j.Left, j.Right)).Distinct().Where(p => Box(p.Left) is not null && Box(p.Right) is not null).ToArray();
        if (pairs.Length == 0) return;
        var keyed = pairs.Select(pair =>
        {
            var join = design.Joins.First(j => j.Left == pair.Left && j.Right == pair.Right);
            bool leftKey = Box(pair.Left)!.Info.Columns.Any(c => c.Name == join.LeftColumn && c.IsPrimaryKey), rightKey = Box(pair.Right)!.Info.Columns.Any(c => c.Name == join.RightColumn && c.IsPrimaryKey);
            // The key end goes on the side of the primary key; a join with no primary key has no ends.
            bool swap = rightKey && !leftKey;
            return (Pair: pair, Primary: swap ? Box(pair.Right)! : Box(pair.Left)!, Foreign: swap ? Box(pair.Left)! : Box(pair.Right)!, Ends: leftKey || rightKey, Join: join);
        }).ToArray();
        var routes = DiagramRouting.Layout(keyed.Select(k => new DiagramRouting.Request(k.Primary, Bounds(k.Primary), k.Foreign, Bounds(k.Foreign))).ToArray(), boxes.Select(Bounds).ToArray());
        for (int i = 0; i < keyed.Length; i++)
        {
            var points = new PointCollection(routes[i].Points);
            linesLayer.Children.Add(new Polyline { Points = points, Stroke = LineBrush, StrokeThickness = 4, IsHitTestVisible = false });
            linesLayer.Children.Add(new Polyline { Points = points, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false });
            var hit = new Polyline { Points = points, Stroke = Brushes.Transparent, StrokeThickness = 9, Tag = keyed[i].Pair, ToolTip = $"{keyed[i].Join.Kind} join between {keyed[i].Pair.Left} and {keyed[i].Pair.Right}" };
            AutomationProperties.SetName(hit, "Join " + keyed[i].Pair.Left + " " + keyed[i].Pair.Right);
            linesLayer.Children.Add(hit);
            if (keyed[i].Ends)
            {
                var key = DatabaseDiagramView.EndGlyph(routes[i].Points[0], routes[i].PrimaryOut, key: true, null); key.IsHitTestVisible = false; linesLayer.Children.Add(key);
                var many = DatabaseDiagramView.EndGlyph(routes[i].Points[^1], routes[i].ForeignOut, key: false, null); many.IsHitTestVisible = false; linesLayer.Children.Add(many);
            }
        }
    }

    private DesignerSourceBox? Box(string reference) => boxes.FirstOrDefault(b => b.Reference == reference);

    /// <summary>Ticks or clears a column. Cleared, a row that also filters or sorts stays, without output.</summary>
    public void SetOutput(DesignerSourceBox box, string column, bool on)
    {
        if (!representable) return;
        var found = design.Criteria.FirstOrDefault(c => c.Source == box.Reference && c.Expression == column);
        if (on)
        {
            if (found is not null) found.Output = true;
            else design.Criteria.Add(new() { Expression = column, Source = box.Reference, Grouping = design.Grouped ? QueryDesign.GroupBy : "" });
        }
        else if (found is not null)
        {
            if (found.Filters.Any(f => f.Trim().Length > 0) || found.Sort != DesignSort.None) found.Output = false;
            else design.Criteria.Remove(found);
        }
        Regenerate(); DrawTables(); ShowRows();
    }

    public void SelectAllColumns(DesignerSourceBox box) { foreach (var column in box.Info.Columns) if (!design.Criteria.Any(c => c.Source == box.Reference && c.Expression == column.Name && c.Output)) SetOutput(box, column.Name, true); }

    public void RemoveSource(DesignerSourceBox box)
    {
        if (!representable) return;
        design.Sources.Remove(box.Source); boxes.Remove(box);
        if (selectedBox == box) selectedBox = null;
        design.Joins.RemoveAll(j => j.Left == box.Reference || j.Right == box.Reference);
        design.Criteria.RemoveAll(c => c.Source == box.Reference);
        Regenerate(); DrawTables(); ShowRows();
    }

    public void AddJoin(DesignerSourceBox left, string leftColumn, DesignerSourceBox right, string rightColumn)
    {
        if (!representable || left == right || design.Joins.Any(j => j.Left == left.Reference && j.Right == right.Reference && j.LeftColumn == leftColumn && j.RightColumn == rightColumn)) return;
        design.Joins.Add(new(left.Reference, leftColumn, right.Reference, rightColumn, DesignJoinKind.Inner));
        Regenerate(); DrawTables();
    }

    public void RemoveJoin(DesignerJoinLine line)
    {
        design.Joins.RemoveAll(j => j.Left == line.Left && j.Right == line.Right);
        Regenerate(); DrawTables();
    }

    /// <summary>Select All Rows from one side of a join: turns the outer join on that side on or off.</summary>
    public void ToggleAllRows(DesignerJoinLine line, bool leftSide)
    {
        var current = design.Joins.First(j => j.Left == line.Left && j.Right == line.Right).Kind;
        bool left = current is DesignJoinKind.Left or DesignJoinKind.Full, right = current is DesignJoinKind.Right or DesignJoinKind.Full;
        if (leftSide) left = !left; else right = !right;
        var kind = left && right ? DesignJoinKind.Full : left ? DesignJoinKind.Left : right ? DesignJoinKind.Right : DesignJoinKind.Inner;
        for (int i = 0; i < design.Joins.Count; i++) if (design.Joins[i].Left == line.Left && design.Joins[i].Right == line.Right) design.Joins[i] = design.Joins[i] with { Kind = kind };
        Regenerate(); DrawTables();
    }

    public void ShowAddTable()
    {
        if (!representable) { Say("The query cannot be represented in the Diagram pane."); return; }
        var dialog = new AddTableDialog(token => service.ListRelationsAsync(token), AddSourcesAsync) { Owner = Window.GetWindow(this) };
        DialogShown?.Invoke(dialog);
        dialog.ShowDialog();
    }

    /// <summary>Adds tables to the query. Foreign keys between them and the tables already there become joins, as in SSMS. A table added twice gets an alias.</summary>
    public async Task AddSourcesAsync(IReadOnlyList<DiagramTableRef> chosen)
    {
        if (!representable) return;
        try
        {
            var added = new List<DesignerSourceBox>();
            foreach (var table in chosen)
            {
                if (await service.ReadSourceAsync(table.Schema, table.Name) is not { } info) { Say($"The table {table.Name} was not found."); continue; }
                string? alias = null;
                if (boxes.Any(b => b.Reference == table.Name)) { int n = 1; while (boxes.Any(b => b.Reference == $"{table.Name}_{n}")) n++; alias = $"{table.Name}_{n}"; }
                var box = new DesignerSourceBox(new(table.Schema, table.Name, alias), info);
                design.Sources.Add(box.Source);
                boxes.Add(box); added.Add(box);
                DrawTables();
                var at = boxes.Except([box]).Select(Bounds).ToArray();
                double width = Bounds(box).Width;
                box.X = at.Length == 0 ? 20 : at.Max(r => r.Right) + 40; box.Y = 10;
                if (box.X + width > SurfaceWidth)
                {
                    // Past the right edge: next to the last table, or on a new row below them all.
                    box.X = at[^1].Right + 40; box.Y = at[^1].Y;
                    if (box.X + width > SurfaceWidth) { box.X = 20; box.Y = at.Max(r => r.Bottom) + 40; }
                }
                DrawTables();
            }
            var links = await service.ForeignKeysAsync(boxes.Select(b => b.Info.Oid).Distinct().ToArray());
            foreach (var box in added)
                foreach (var other in boxes.Where(b => b != box && b.Info.Oid != box.Info.Oid && boxes.IndexOf(b) < boxes.IndexOf(box)))
                    foreach (var link in links.Where(l => l.From == box.Info.Oid && l.To == other.Info.Oid || l.From == other.Info.Oid && l.To == box.Info.Oid))
                    {
                        var (mine, theirs) = link.From == other.Info.Oid ? (link.ReferencedColumns, link.Columns) : (link.Columns, link.ReferencedColumns);
                        for (int i = 0; i < mine.Count; i++) design.Joins.Add(new(other.Reference, theirs[i], box.Reference, mine[i], DesignJoinKind.Inner));
                    }
            Regenerate(); DrawTables();
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException) { Say("The table could not be read: " + Message(ex)); }
    }

    /// <summary>Add Related Tables: every table linked to this one by a foreign key, in either direction, that is not in the Diagram pane yet, with its joins.</summary>
    public async Task AddRelatedTablesAsync(DesignerSourceBox box)
    {
        if (!representable) return;
        IReadOnlyList<DiagramTableRef> related;
        try { related = await service.RelatedTablesAsync(box.Info.Oid); }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException) { Say("The related tables could not be read: " + Message(ex)); return; }
        var missing = related.Where(r => boxes.All(b => b.Info.Oid != r.Oid)).ToArray();
        if (missing.Length == 0) { Say(box.Reference + " has no related tables that are not already in the diagram."); return; }
        Say("");
        await AddSourcesAsync(missing);
    }

    // ---- Mouse in the Diagram pane ----
    private static object? Tagged(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is FrameworkElement { Tag: DesignerHit or DesignerJoinLine } tagged) return tagged.Tag;
        return null;
    }

    private void SurfaceDown(object sender, MouseButtonEventArgs e)
    {
        var pressed = Tagged(e.OriginalSource as DependencyObject) as DesignerHit;
        surface.Focus();
        SelectBox(pressed?.Box);
        if (pressed is not { } hit) return;
        dragStart = e.GetPosition(surface);
        if (hit.Column is null) { moveBox = hit; moveOrigin = new(hit.Box.X, hit.Box.Y); }
        else dragColumn = hit;
        surface.CaptureMouse(); e.Handled = true;
    }
    private void SurfaceMove(object sender, MouseEventArgs e)
    {
        if (moveBox is null || e.LeftButton != MouseButtonState.Pressed) return;
        MoveBox(moveBox.Box, moveOrigin + (e.GetPosition(surface) - dragStart));
    }
    internal void MoveBox(DesignerSourceBox box, Point to)
    {
        box.X = Math.Max(0, to.X); box.Y = Math.Max(0, to.Y);
        Canvas.SetLeft(visuals[box], box.X); Canvas.SetTop(visuals[box], box.Y);
        FitSurface();
        DrawLines();
    }
    private void SurfaceUp(object sender, MouseButtonEventArgs e)
    {
        surface.ReleaseMouseCapture();
        var column = dragColumn; moveBox = null; dragColumn = null;
        if (column is not { Column: { } from and not "*" }) return;
        // Capture sends the release here; what is under the mouse is found by position.
        DependencyObject? found = null;
        VisualTreeHelper.HitTest(surface, null, r => { found = r.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(e.GetPosition(surface)));
        if (Tagged(found) is DesignerHit { Column: { } to and not "*" } target && target.Box != column.Box) AddJoin(column.Box, from, target.Box, to);
    }

    // ---- Menus (SSMS 22.11, captures of M1046) ----
    internal void FillMenu(ContextMenu menu, object? target)
    {
        menu.Items.Clear();
        void Add(string header, Action click, string name, bool enabled = true, bool isChecked = false)
        {
            var item = new MenuItem { Header = header, Name = name, IsEnabled = enabled, IsChecked = isChecked };
            item.Click += (_, _) => click(); menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new Separator());
        switch (target)
        {
            case DesignerHit { Box: var box }:
                Add("Column Names", () => { box.NameOnly = false; DrawTables(); }, "DesignerColumnNames", isChecked: !box.NameOnly);
                Add("Name Only", () => { box.NameOnly = true; DrawTables(); }, "DesignerNameOnly", isChecked: box.NameOnly);
                Separator();
                Add("Select All Columns", () => SelectAllColumns(box), "DesignerSelectAllColumns", enabled: design.Statement != DesignStatement.Delete);
                if (queryDialog) Add("Add Related Tables", () => _ = AddRelatedTablesAsync(box), "DesignerAddRelated", enabled: design.Statement is DesignStatement.Select or DesignStatement.InsertResults or DesignStatement.MakeTable);
                Add("Remove", () => RemoveSource(box), "DesignerRemoveTable");
                break;
            case DesignerJoinLine line:
                var kind = design.Joins.First(j => j.Left == line.Left && j.Right == line.Right).Kind;
                Add("Remove", () => RemoveJoin(line), "DesignerRemoveJoin");
                Separator();
                Add("Select All Rows from " + line.Left, () => ToggleAllRows(line, true), "DesignerAllRowsLeft", isChecked: kind is DesignJoinKind.Left or DesignJoinKind.Full);
                Add("Select All Rows from " + line.Right, () => ToggleAllRows(line, false), "DesignerAllRowsRight", isChecked: kind is DesignJoinKind.Right or DesignJoinKind.Full);
                break;
            default:
                Add("Execute SQL", () => _ = ExecuteAsync(), "DesignerExecute", enabled: !queryDialog);
                Add("Add Group By", () => SetGrouped(!design.Grouped), "DesignerAddGroupBy", enabled: design.Statement is DesignStatement.Select or DesignStatement.InsertResults or DesignStatement.MakeTable, isChecked: design.Grouped);
                if (queryDialog) { menu.Items.Add(ChangeTypeMenu()); Separator(); AddUndoItems(menu); }
                Separator();
                Add("Add Table…", ShowAddTable, "DesignerAddTable");
                if (queryDialog) Add("Arrange Tables", ArrangeTables, "DesignerArrange", enabled: boxes.Count > 0);
                Separator();
                menu.Items.Add(PaneMenu());
                if (!queryDialog) Add("Clear Results", ClearResults, "DesignerClearResults");
                break;
        }
    }

    private MenuItem PaneMenu()
    {
        var pane = new MenuItem { Header = "Pane", Name = "DesignerPane" };
        string[] names = ["Diagram", "Criteria", "SQL", "Results"];
        for (int i = 0; i < (queryDialog ? 3 : 4); i++) { int p = i; var item = new MenuItem { Header = names[i], IsChecked = IsPaneVisible(i) }; item.Click += (_, _) => SetPane(p, !IsPaneVisible(p)); pane.Items.Add(item); }
        return pane;
    }

    private MenuItem ChangeTypeMenu()
    {
        var menu = new MenuItem { Header = "Change Type", Name = "DesignerChangeType", IsEnabled = representable };
        foreach (var (label, kind) in new[] { ("Select", DesignStatement.Select), ("Insert Results…", DesignStatement.InsertResults), ("Insert Values", DesignStatement.InsertValues), ("Update", DesignStatement.Update), ("Delete", DesignStatement.Delete), ("Make Table…", DesignStatement.MakeTable) })
        {
            var item = new MenuItem { Header = label, IsChecked = design.Statement == kind };
            item.Click += (_, _) => _ = ChangeTypeAsync(kind);
            menu.Items.Add(item);
        }
        return menu;
    }

    private void AddUndoItems(ContextMenu menu)
    {
        var redo = new MenuItem { Header = "Redo", Name = "DesignerRedo", IsEnabled = CanRedo }; redo.Click += (_, _) => _ = RedoAsync(); menu.Items.Add(redo);
        var undo = new MenuItem { Header = "Undo", Name = "DesignerUndo", IsEnabled = CanUndo }; undo.Click += (_, _) => _ = UndoAsync(); menu.Items.Add(undo);
    }

    /// <summary>The Criteria pane menu of the Query Designer.</summary>
    internal void FillGridMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Execute SQL", Name = "DesignerExecute", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(ChangeTypeMenu());
        menu.Items.Add(new Separator());
        AddUndoItems(menu);
        menu.Items.Add(new Separator());
        menu.Items.Add(PaneMenu());
    }

    /// <summary>The SQL pane menu of the Query Designer.</summary>
    internal void FillSqlMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        MenuItem Add(string header, Action click, string name, bool enabled = true) { var item = new MenuItem { Header = header, Name = name, IsEnabled = enabled }; item.Click += (_, _) => click(); menu.Items.Add(item); return item; }
        Add("Execute SQL", () => { }, "DesignerExecute", enabled: false);
        Add("Verify SQL Syntax", () => _ = VerifyAsync(), "DesignerVerify");
        menu.Items.Add(new Separator());
        menu.Items.Add(ChangeTypeMenu());
        menu.Items.Add(new Separator());
        AddUndoItems(menu);
        bool selected = sqlEditor.SelectionLength > 0;
        Add("Cut", sqlEditor.Cut, "DesignerCut", selected);
        Add("Copy", sqlEditor.Copy, "DesignerCopy", selected);
        Add("Paste", sqlEditor.Paste, "DesignerPaste");
        Add("Delete", () => sqlEditor.SelectedText = "", "DesignerDelete", selected);
        Add("Select All", sqlEditor.SelectAll, "DesignerSelectAll");
        menu.Items.Add(new Separator());
        menu.Items.Add(PaneMenu());
    }

    // ---- Execute and Verify ----
    /// <summary>Execute SQL into the Results pane: read only, at most QueryDesignService.MaxRows rows.</summary>
    public async Task ExecuteAsync()
    {
        if (IsBusy) return;
        await LeaveSqlFirstAsync();
        using var cancellation = operation = new CancellationTokenSource();
        cancelButton.Visibility = Visibility.Visible; resultStatus.Text = "Executing…";
        string sql = sqlEditor.Text; DateTime startedUtc = DateTime.UtcNow; var timer = System.Diagnostics.Stopwatch.StartNew();
        Exception? failure = null; long? rows = null;
        try
        {
            var result = await service.ExecuteAsync(sql, cancellation.Token);
            rows = result.Table.Rows.Count;
            results.Columns.Clear();
            foreach (System.Data.DataColumn column in result.Table.Columns)
                results.Columns.Add(new DataGridTextColumn { Header = column.Caption, CellStyle = ResultGridStyle.NullAwareCell($"[{column.ColumnName}]"), Binding = new Binding($"[{column.ColumnName}]") { Converter = new DatabaseValueConverter() } });
            results.ItemsSource = result.Table.DefaultView;
            resultStatus.Text = $"{result.Table.Rows.Count} row(s)" + (result.Truncated ? $"; only the first {QueryDesignService.MaxRows} are shown." : "");
            changedMark.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException ex) { failure = ex; resultStatus.Text = "Canceled."; }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException) { failure = ex; resultStatus.Text = Message(ex); }
        finally
        {
            operation = null; cancelButton.Visibility = Visibility.Collapsed;
            if (sql.Trim().Length > 0) ExecutionObserved?.Invoke(sql, startedUtc, timer.Elapsed, failure, failure is OperationCanceledException || cancellation.IsCancellationRequested, rows);
        }
    }
    /// <summary>Each Execute SQL after it ends (SQL, start in UTC, duration, error, canceled, rows shown); the query tab hands it to Query History (PGMS-043).</summary>
    internal Action<string, DateTime, TimeSpan, Exception?, bool, long?>? ExecutionObserved { get; set; }

    /// <summary>Verify SQL Syntax against the server; nothing runs.</summary>
    public async Task<bool> VerifyAsync()
    {
        if (IsBusy) return false;
        await LeaveSqlFirstAsync();
        using var cancellation = operation = new CancellationTokenSource();
        try
        {
            await service.VerifyAsync(sqlEditor.Text, cancellation.Token, allowWrites: queryDialog);
            Notice("The SQL syntax has been verified against the data source.");
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or TimeoutException or ArgumentException or OperationCanceledException) { Notice(Message(ex)); return false; }
        finally { operation = null; }
    }

    /// <summary>Text typed in the SQL pane reaches the other panes before it runs or is saved.</summary>
    internal async Task LeaveSqlFirstAsync()
    {
        SqlPutBack = false;
        criteria.CommitEdit(DataGridEditingUnit.Row, true);
        ReadGrid();
        await SqlLeftAsync();
    }
}
