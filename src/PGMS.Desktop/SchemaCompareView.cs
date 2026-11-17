using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Schema Compare (PGMS-075, M1102), laid out like the SSMS 22.11 document: a toolbar, Source and Target with Select,
/// a yellow message bar, the grid of differences grouped by type and the two definitions side by side. Generate Script opens the script in a
/// query tab on the target and never runs it; there is no Apply.</summary>
public sealed class SchemaCompareView : UserControl
{
    private readonly Button compare = Tool("Compare", "Compare the source and the target"), stop = Tool("Stop", "Stop the comparison"),
        generate = Tool("Generate Script", "Open the script that makes the target look like the source in a new query window on the target. Nothing is run"),
        switchDirection = Tool("Switch Direction", "Swap the source and the target");
    private readonly ComboBox groupBy = new() { Width = 110, Margin = new Thickness(4, 0, 0, 0), ItemsSource = new[] { "Type", "Schema", "Action" }, SelectedIndex = 0 };
    private readonly TextBox sourceBox = Endpoint("Source"), targetBox = Endpoint("Target");
    private readonly Button selectSource = SelectButton("Select source"), selectTarget = SelectButton("Select target"), compareRight = new() { Content = "Compare", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Border messageBar = new() { Background = new SolidColorBrush(Color.FromRgb(0xFC, 0xF4, 0xA3)), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid grid = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, Margin = new Thickness(0, 6, 0, 0)
    };
    private readonly TextEditor sourceText = Definition("Source definition"), targetText = Definition("Target definition");
    private readonly DiffLines sourceLines = new(Color.FromRgb(0xD9, 0xEA, 0xC4)), targetLines = new(Color.FromRgb(0xF4, 0xC7, 0xC3));
    private readonly TextBlock placeholder = new() { Text = "Select a row to view differences", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private string? source, target;
    private SchemaSnapshot? sourceSnapshot, targetSnapshot;
    private List<SchemaDifference> differences = [];
    private CancellationTokenSource? running;

    public string Title => "Schema Compare";
    /// <summary>The Connect dialog of the main window; returns a connection string or null. Replaced in tests.</summary>
    public Func<string?>? PickConnection { get; set; }
    /// <summary>Opens the generated script in a query tab connected to the target (the main window sets it). The script is never executed.</summary>
    public Func<string, string, Task>? OpenScript { get; set; }
    public string? SourceConnection => source;
    public string? TargetConnection => target;
    public string SourceText => sourceBox.Text;
    public string TargetText => targetBox.Text;
    public string MessageText => messageBar.Visibility == Visibility.Visible ? message.Text : "";
    public IReadOnlyList<SchemaDifference> Differences => differences;
    public bool IsComparing => running is not null;
    public DataGrid Grid => grid;
    public string SourceDefinition => sourceText.Text;
    public string TargetDefinition => targetText.Text;
    public IReadOnlyCollection<int> SourceChangedLines => sourceLines.Lines;
    public IReadOnlyCollection<int> TargetChangedLines => targetLines.Lines;
    public string GroupBy { get => (string)groupBy.SelectedItem; set => groupBy.SelectedItem = value; }
    public bool CanCompare => compare.IsEnabled;
    public bool CanGenerateScript => generate.IsEnabled;

    public SchemaCompareView()
    {
        foreach (var (header, property, width) in new[] { ("Type", nameof(SchemaDifference.TypeName), 1.0), ("Source Name", nameof(SchemaDifference.SourceName), 2.2), ("Action", nameof(SchemaDifference.Action), 0.8), ("Target Name", nameof(SchemaDifference.TargetName), 2.2) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star), IsReadOnly = true });
        grid.Columns.Insert(0, new DataGridTemplateColumn
        {
            Header = "Include", Width = new DataGridLength(64),
            CellTemplate = (DataTemplate)XamlReader.Parse("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><CheckBox HorizontalAlignment="Center" IsChecked="{Binding Include, UpdateSourceTrigger=PropertyChanged}" ToolTip="Include this difference in Generate Script"/></DataTemplate>""")
        });
        grid.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = (DataTemplate)XamlReader.Parse("""<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Margin="4,6,0,2" FontWeight="SemiBold"><Run Text="{Binding Name, Mode=OneWay}"/><Run Text=" ("/><Run Text="{Binding ItemCount, Mode=OneWay}"/><Run Text=")"/></TextBlock></DataTemplate>""")
        });
        AutomationProperties.SetName(grid, "Schema differences");
        AutomationProperties.SetName(groupBy, "Group By");
        grid.SelectionChanged += (_, _) => ShowDefinitions(grid.SelectedItem as SchemaDifference);
        groupBy.SelectionChanged += (_, _) => ApplyGrouping();
        sourceText.TextArea.TextView.BackgroundRenderers.Add(sourceLines);
        targetText.TextArea.TextView.BackgroundRenderers.Add(targetLines);

        compare.Click += async (_, _) => await CompareAsync();
        compareRight.Click += async (_, _) => await CompareAsync();
        stop.Click += (_, _) => running?.Cancel();
        generate.Click += async (_, _) => await GenerateScriptAsync();
        switchDirection.Click += (_, _) => SwitchDirection();
        selectSource.Click += (_, _) => OpenSelectMenu(selectSource, isSource: true);
        selectTarget.Click += (_, _) => OpenSelectMenu(selectTarget, isSource: false);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Children = { compare, stop, Separator(), generate, Separator(), switchDirection, Separator(), new TextBlock { Text = "Group By:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }, groupBy } };
        var endpoints = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var width in new[] { 1.0, 0, 16, 1.0, 0, 0 }) endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = width == 0 ? GridLength.Auto : width == 16 ? new GridLength(16) : new GridLength(1, GridUnitType.Star) });
        endpoints.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); endpoints.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Place(UIElement e, int row, int column) { System.Windows.Controls.Grid.SetRow(e, row); System.Windows.Controls.Grid.SetColumn(e, column); endpoints.Children.Add(e); }
        Place(new TextBlock { Text = "Source", Margin = new Thickness(0, 0, 0, 3) }, 0, 0); Place(new TextBlock { Text = "Target", Margin = new Thickness(0, 0, 0, 3) }, 0, 3);
        Place(sourceBox, 1, 0); Place(selectSource, 1, 1); Place(targetBox, 1, 3); Place(selectTarget, 1, 4); Place(compareRight, 1, 5);

        var close = new Button { Content = "✕", Padding = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Close the message" };
        AutomationProperties.SetName(close, "Close message");
        close.Click += (_, _) => messageBar.Visibility = Visibility.Collapsed;
        var bar = new DockPanel(); DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close); bar.Children.Add(message);
        messageBar.Child = bar;
        AutomationProperties.SetName(message, "Schema Compare message");

        var definitions = new Grid();
        definitions.ColumnDefinitions.Add(new ColumnDefinition()); definitions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) }); definitions.ColumnDefinitions.Add(new ColumnDefinition());
        System.Windows.Controls.Grid.SetColumn(targetText, 2);
        var middle = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        System.Windows.Controls.Grid.SetColumn(middle, 1);
        System.Windows.Controls.Grid.SetColumnSpan(placeholder, 3);
        definitions.Children.Add(sourceText); definitions.Children.Add(middle); definitions.Children.Add(targetText); definitions.Children.Add(placeholder);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 80 });
        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        System.Windows.Controls.Grid.SetRow(splitter, 1); System.Windows.Controls.Grid.SetRow(definitions, 2);
        body.Children.Add(grid); body.Children.Add(splitter); body.Children.Add(definitions);

        var root = new DockPanel { Margin = new Thickness(8) };
        foreach (var top in new FrameworkElement[] { toolbar, endpoints, messageBar }) { DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); }
        root.Children.Add(body);
        Content = root;
        Background = SystemColors.WindowBrush;
        ShowDefinitions(null);
        Refresh();
    }

    private static Button Tool(string text, string tip)
    {
        var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }
    private static FrameworkElement Separator() => new Border { Width = 1, Margin = new Thickness(4, 3, 8, 3), Background = SystemColors.ControlDarkBrush };
    private static TextBox Endpoint(string name)
    {
        var box = new TextBox { IsReadOnly = true, Padding = new Thickness(3), Background = SystemColors.ControlLightLightBrush };
        AutomationProperties.SetName(box, name);
        return box;
    }
    private static Button SelectButton(string name)
    {
        var button = new Button { Content = "Select ▾", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0) };
        AutomationProperties.SetName(button, name);
        return button;
    }
    private static TextEditor Definition(string name)
    {
        var editor = new TextEditor { IsReadOnly = true, ShowLineNumbers = true, FontFamily = new FontFamily("Consolas"), FontSize = 13, SyntaxHighlighting = QueryView.SqlDefinition, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(editor, name);
        return editor;
    }

    private void OpenSelectMenu(Button button, bool isSource)
    {
        var database = new MenuItem { Header = "_Database…", Name = isSource ? "SelectSourceDatabase" : "SelectTargetDatabase" };
        database.Click += (_, _) => { if ((PickConnection?.Invoke()) is { } connection) SetEndpoint(connection, isSource); };
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, Items = { database } };
        menu.IsOpen = true;
    }

    /// <summary>Server and database of a connection string, as the Source and Target boxes show them; the password is never shown.</summary>
    public static string Describe(string connection)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection);
        return $"{builder.Host}:{builder.Port}.{builder.Database}";
    }

    /// <summary>Sets the source or the target (Select ▸ Database…). The results of an earlier comparison are kept until Compare runs again.</summary>
    public void SetEndpoint(string connection, bool isSource)
    {
        if (isSource) { source = connection; sourceBox.Text = Describe(connection); }
        else { target = connection; targetBox.Text = Describe(connection); }
        Refresh();
    }

    /// <summary>Switch Direction: the source becomes the target and the other way round; the differences are cleared, since they would read backwards.</summary>
    public void SwitchDirection()
    {
        if (running is not null) return;
        (source, target) = (target, source);
        (sourceBox.Text, targetBox.Text) = (targetBox.Text, sourceBox.Text);
        Clear();
        if (source is not null && target is not null) Show("Source and target switched. Compare again to see the differences.");
        Refresh();
    }

    private void Clear()
    {
        differences = []; sourceSnapshot = targetSnapshot = null;
        grid.ItemsSource = null;
        ShowDefinitions(null);
    }

    private void Show(string text) { message.Text = text; messageBar.Visibility = Visibility.Visible; }

    private void Refresh()
    {
        bool idle = running is null, ready = source is not null && target is not null;
        compare.IsEnabled = compareRight.IsEnabled = idle && ready;
        stop.IsEnabled = !idle;
        generate.IsEnabled = idle && differences.Count > 0 && sourceSnapshot is not null;
        switchDirection.IsEnabled = selectSource.IsEnabled = selectTarget.IsEnabled = groupBy.IsEnabled = idle;
    }

    /// <summary>Reads both databases (only reads) and lists the differences.</summary>
    public async Task CompareAsync()
    {
        if (running is not null || source is null || target is null) return;
        running = new CancellationTokenSource();
        var token = running.Token;
        Clear();
        Show("Comparing schemas…");
        Refresh();
        try
        {
            var readSource = SchemaCompare.ReadAsync(source, token);
            var readTarget = SchemaCompare.ReadAsync(target, token);
            await Task.WhenAll(readSource, readTarget);
            sourceSnapshot = readSource.Result; targetSnapshot = readTarget.Result;
            differences = SchemaCompare.Compare(sourceSnapshot, targetSnapshot).ToList();
            grid.ItemsSource = differences;
            ApplyGrouping();
            Show(SchemaCompare.Summary(differences));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Clear(); Show("Comparison stopped."); }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or InvalidCastException or System.IO.IOException or TimeoutException or OperationCanceledException)
        { Clear(); Show("Comparison failed: " + (ex is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message)); }
        finally { running.Dispose(); running = null; Refresh(); }
    }

    private void ApplyGrouping()
    {
        if (grid.ItemsSource is null) return;
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(GroupBy switch { "Schema" => nameof(SchemaDifference.Schema), "Action" => nameof(SchemaDifference.Action), _ => nameof(SchemaDifference.TypeName) }));
    }

    /// <summary>The script for the included differences, opened by <see cref="OpenScript"/> on the target. Returns the script, or null when there is nothing to script.</summary>
    public async Task<string?> GenerateScriptAsync()
    {
        if (running is not null || sourceSnapshot is null || targetSnapshot is null || target is null) return null;
        if (!differences.Any(d => d.Include)) { Show("No differences are included. Check Include on the rows to script."); return null; }
        string script = SchemaCompare.GenerateScript(sourceSnapshot, targetSnapshot, differences);
        if (OpenScript is { } open) await open(script, target);
        Show("Script generated in a new query window on the target. Nothing was run.");
        return script;
    }

    /// <summary>Selects a row and shows both definitions, the lines that differ highlighted.</summary>
    public void Select(SchemaDifference difference) { grid.SelectedItem = difference; grid.ScrollIntoView(difference); ShowDefinitions(difference); }

    private void ShowDefinitions(SchemaDifference? difference)
    {
        sourceText.Text = difference?.Source?.Definition ?? "";
        targetText.Text = difference?.Target?.Definition ?? "";
        var (left, right) = ChangedLines(sourceText.Text, targetText.Text);
        sourceLines.Lines = left; targetLines.Lines = right;
        sourceText.TextArea.TextView.InvalidateLayer(KnownLayer.Background); targetText.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        placeholder.Visibility = difference is null ? Visibility.Visible : Visibility.Collapsed;
        sourceText.Visibility = targetText.Visibility = difference is null ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Line numbers (1-based) on each side that are not in the longest common subsequence of lines. Very long texts are marked whole.</summary>
    public static (HashSet<int> Source, HashSet<int> Target) ChangedLines(string left, string right)
    {
        string[] a = left.Length == 0 ? [] : left.Split('\n').Select(l => l.TrimEnd('\r')).ToArray(), b = right.Length == 0 ? [] : right.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if ((long)a.Length * b.Length > 4_000_000) return (Enumerable.Range(1, a.Length).ToHashSet(), Enumerable.Range(1, b.Length).ToHashSet());
        var length = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                length[i, j] = a[i] == b[j] ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);
        var changedA = Enumerable.Range(1, a.Length).ToHashSet(); var changedB = Enumerable.Range(1, b.Length).ToHashSet();
        for (int i = 0, j = 0; i < a.Length && j < b.Length;)
        {
            if (a[i] == b[j]) { changedA.Remove(i + 1); changedB.Remove(j + 1); i++; j++; }
            else if (length[i + 1, j] >= length[i, j + 1]) i++;
            else j++;
        }
        return (changedA, changedB);
    }

    /// <summary>Paints the lines that differ, green on the source side and red on the target side, as SSMS does.</summary>
    private sealed class DiffLines(Color color) : IBackgroundRenderer
    {
        private readonly Brush brush = Frozen(color);
        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public HashSet<int> Lines { get; set; } = [];
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Lines.Count == 0) return;
            textView.EnsureVisualLines();
            foreach (var line in textView.VisualLines)
            {
                if (!Lines.Contains(line.FirstDocumentLine.LineNumber)) continue;
                double top = line.VisualTop - textView.ScrollOffset.Y;
                drawingContext.DrawRectangle(brush, null, new Rect(0, top, Math.Max(textView.ActualWidth, 1), line.Height));
            }
        }
    }
}
