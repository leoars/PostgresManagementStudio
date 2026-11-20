using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Object Explorer Details (View ▸ Object Explorer Details, F7; PGMS-052, M1085): a document listing what the node selected in Object Explorer contains.</summary>
public sealed partial class ObjectExplorerDetailsView : UserControl
{
    public sealed record Row(CatalogNode Node, string Name, string Type, string Schema, string Owner);

    private readonly TextBlock path = new() { Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBox search = new() { Width = 220, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock count = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button up = new() { Content = "_Up", Padding = new Thickness(10, 2, 10, 2), ToolTip = "Show the folder that contains this one" };
    private readonly Button refresh = new() { Content = "_Refresh", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Read the list from the server again (F5 in Object Explorer)" };
    private readonly DataGrid grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None
    };
    private IReadOnlyList<Row> all = [];
    private int generation;

    public string Title => "Object Explorer Details";
    public CatalogNode? Current { get; private set; }
    /// <summary>True while the list of <see cref="Current"/> is being read.</summary>
    public bool IsLoading { get; private set; }
    public DataGrid Grid => grid;
    public TextBox Search => search;
    public string PathText => path.Text;
    public string CountText => count.Text;
    public IReadOnlyList<Row> Rows => (IReadOnlyList<Row>?)grid.ItemsSource ?? [];
    /// <summary>Asks Object Explorer to select a node (keeps both views in step); the main window sets it.</summary>
    public Action<CatalogNode>? SelectInExplorer { get; set; }

    public ObjectExplorerDetailsView()
    {
        foreach (var (header, property, width) in new[] { ("Name", nameof(Row.Name), 2.0), ("Type", nameof(Row.Type), 1.0), ("Schema", nameof(Row.Schema), 1.0), ("Owner", nameof(Row.Owner), 1.0) })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        AutomationProperties.SetName(grid, "Object Explorer Details");
        AutomationProperties.SetName(search, "Search");
        search.TextChanged += (_, _) => ApplySearch();
        grid.MouseDoubleClick += (_, e) => { if (grid.SelectedItem is Row row) { _ = OpenAsync(row); e.Handled = true; } };
        grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && grid.SelectedItem is Row row) { _ = OpenAsync(row); e.Handled = true; } else if (e.Key == Key.Back) { _ = UpAsync(); e.Handled = true; } };
        up.Click += (_, _) => _ = UpAsync();
        refresh.Click += (_, _) => _ = RefreshAsync();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { up, refresh } };
        var find = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { new TextBlock { Text = "Search:", VerticalAlignment = VerticalAlignment.Center }, search } };
        DockPanel.SetDock(buttons, Dock.Left); bar.Children.Add(buttons); bar.Children.Add(find);
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(path, Dock.Top); DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(count, Dock.Bottom);
        root.Children.Add(path); root.Children.Add(bar); root.Children.Add(count); root.Children.Add(grid);
        Content = root;
        Background = SystemColors.WindowBrush;
        _ = ShowAsync(null);
    }

    /// <summary>Lists the contents of a folder or object; for an item that contains nothing, its siblings with it selected. Loads the node like expanding it would.</summary>
    public async Task ShowAsync(CatalogNode? node)
    {
        int mine = ++generation;
        CatalogNode? folder = node is null ? null : node.Entry.CanExpand ? node : node.Parent ?? node;
        Current = folder;
        if (folder is null)
        {
            path.Text = "Object Explorer is not connected, or nothing is selected."; IsLoading = false; all = []; ApplySearch(); up.IsEnabled = refresh.IsEnabled = false; return;
        }
        path.Text = PathOf(folder);
        IsLoading = true; count.Text = "Loading…";
        up.IsEnabled = folder.Parent is not null; refresh.IsEnabled = true;
        if (folder.Entry.CanExpand) await folder.LoadAsync();
        // A load already running (started by Object Explorer) returns at once: wait for it.
        while (folder.IsBusy) { await Task.Delay(50); if (mine != generation) return; }
        if (mine != generation) return;
        IsLoading = false;
        all = folder.Entry.CanExpand ? folder.Children.Where(c => !IsPlaceholder(c)).Select(ToRow).ToList() : [];
        ApplySearch();
        if (node is not null && !ReferenceEquals(node, folder) && Rows.FirstOrDefault(r => ReferenceEquals(r.Node, node)) is { } selected) { grid.SelectedItem = selected; grid.ScrollIntoView(selected); }
    }

    private static bool IsPlaceholder(CatalogNode node) => node.Entry.Kind == CatalogKind.Column && node.Entry.Name == "Expand to load…" && node.Parent is { } p && p.Entry.Kind != CatalogKind.Columns;

    public async Task OpenAsync(Row row)
    {
        if (!row.Node.Entry.CanExpand) return;
        if (row.Node.Parent is { } parent) parent.IsExpanded = true;
        SelectInExplorer?.Invoke(row.Node);
        await ShowAsync(row.Node);
    }

    public async Task UpAsync()
    {
        if (Current?.Parent is not { } parent) return;
        var from = Current;
        SelectInExplorer?.Invoke(parent);
        await ShowAsync(parent);
        if (Rows.FirstOrDefault(r => ReferenceEquals(r.Node, from)) is { } back) grid.SelectedItem = back;
    }

    public async Task RefreshAsync()
    {
        if (Current is not { } folder) return;
        await folder.LoadAsync(refresh: true);
        await ShowAsync(folder);
    }

    private static readonly double[] DefaultStarWidths = [2.0, 1.0, 1.0, 1.0];

    /// <summary>Window ▸ Reset Current Object Explorer Details View (PGMS-082, M1121): column order and widths as created, no sorting, no search text.</summary>
    public void ResetView()
    {
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            column.DisplayIndex = i;
            column.Width = new DataGridLength(DefaultStarWidths[Math.Min(i, DefaultStarWidths.Length - 1)], DataGridLengthUnitType.Star);
            column.SortDirection = null;
        }
        grid.Items.SortDescriptions.Clear();
        search.Text = "";
        ApplySearch();
    }

    private void ApplySearch()
    {
        string text = search.Text.Trim();
        var shown = text.Length == 0 ? all : all.Where(r => r.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        grid.ItemsSource = shown;
        count.Text = Current is null ? "" : text.Length == 0 ? $"{all.Count:N0} item(s)" : $"{shown.Count:N0} of {all.Count:N0} item(s)";
    }

    private static Row ToRow(CatalogNode node) => new(node, node.Entry.Name, TypeOf(node.Entry), node.Entry.Schema ?? "", node.Entry.Owner ?? "");

    internal static string PathOf(CatalogNode node)
    {
        var names = new List<string>();
        for (var n = node; n is not null; n = n.Parent) names.Add(n.Entry.Name);
        names.Reverse();
        return string.Join(" › ", names);
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBreak();

    /// <summary>What a row is, in the words Object Explorer uses for it.</summary>
    internal static string TypeOf(CatalogEntry entry) => entry.Kind switch
    {
        CatalogKind.Relation => entry.RelationType switch { "r" => "Table", "p" => "Partitioned Table", "v" => "View", "m" => "Materialized View", "S" => "Sequence", "f" => "Foreign Table", _ => "Relation" },
        CatalogKind.Routine => entry.RoutineKind switch { "p" => "Procedure", "a" => "Aggregate Function", "w" => "Window Function", _ => "Function" },
        CatalogKind.DataType => entry.DataTypeKind switch { "e" => "Enum", "d" => "Domain", "c" => "Composite Type", "r" => "Range Type", _ => "Data Type" },
        CatalogKind.Databases or CatalogKind.Schemas or CatalogKind.Tables or CatalogKind.Views or CatalogKind.MaterializedViews or CatalogKind.Sequences or CatalogKind.Functions or CatalogKind.Procedures
            or CatalogKind.Columns or CatalogKind.Indexes or CatalogKind.Roles or CatalogKind.Enums or CatalogKind.Domains or CatalogKind.DatabaseDiagrams or CatalogKind.Keys or CatalogKind.Constraints or CatalogKind.Triggers or CatalogKind.Extensions
            or CatalogKind.CompositeTypes or CatalogKind.ForeignServers or CatalogKind.Replication or CatalogKind.Publications or CatalogKind.Subscriptions
        or CatalogKind.SystemDatabases or CatalogKind.SystemSchemas => "Folder",
        CatalogKind.DatabaseDiagram => "Database Diagram",
        _ => WordBreak().Replace(entry.Kind.ToString(), " ")
    };
}
