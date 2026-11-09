using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Choose Name, as SSMS asks for a new table or diagram ("Enter a name for the …:").</summary>
internal static class NamePrompt
{
    public static string? Show(Window? owner, string title, string label, string suggested)
    {
        var name = new TextBox { Text = suggested, Margin = new Thickness(0, 6, 0, 12) };
        AutomationProperties.SetName(name, label.TrimEnd(':'));
        var ok = new Button { Content = "OK", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
        var dialog = new Window
        {
            Title = title, Width = 340, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Owner = owner,
            Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = label }, name, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } }
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        return dialog.ShowDialog() == true ? name.Text : null;
    }
}

/// <summary>Modify Custom of Table View: the fields the Custom view shows after the column name. SSMS calls this Column Selection; its layout was not captured.</summary>
internal static class ColumnSelectionPrompt
{
    public static IReadOnlyList<string>? Show(Window? owner, IReadOnlyList<string> current)
    {
        var boxes = DiagramColumnFields.All.Select(f => new CheckBox { Content = f, IsChecked = current.Contains(f), Margin = new Thickness(0, 3, 0, 3) }).ToArray();
        var ok = new Button { Content = "OK", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
        var fields = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
        foreach (var box in boxes) fields.Children.Add(box);
        var dialog = new Window
        {
            Title = "Column Selection", Width = 300, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Owner = owner,
            Content = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = "Show these fields after the column name:", TextWrapping = TextWrapping.Wrap }, fields, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } }
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToArray() : null;
    }
}

/// <summary>Add Table of a diagram (SSMS 22.11): only tables not yet in the diagram, several at once; Add keeps the dialog open until Close.</summary>
public sealed class AddTableDialog : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiagramTableRef>>> load;
    private readonly Func<IReadOnlyList<DiagramTableRef>, Task> add;
    private readonly ListBox list = new() { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(DiagramTableRef.DisplayName) };
    private readonly Button addButton = new() { Content = "_Add", Width = 80, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
    private readonly TextBlock status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    public ListBox Tables => list;
    public string StatusText => status.Text;

    public AddTableDialog(Func<CancellationToken, Task<IReadOnlyList<DiagramTableRef>>> load, Func<IReadOnlyList<DiagramTableRef>, Task> add)
    {
        this.load = load; this.add = add;
        Title = "Add Table"; Width = 380; Height = 420; MinWidth = 300; MinHeight = 260; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResizeWithGrip;
        AutomationProperties.SetName(list, "Tables");
        var refresh = new Button { Content = "_Refresh", Width = 80 };
        var close = new Button { Content = "_Close", Width = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        refresh.Click += async (_, _) => await ReloadAsync();
        addButton.Click += async (_, _) => await AddSelectedAsync();
        close.Click += (_, _) => Close();
        list.MouseDoubleClick += async (_, e) => { if (e.ChangedButton == MouseButton.Left && list.SelectedItem is not null) await AddSelectedAsync(); };
        list.SelectionChanged += (_, _) => addButton.IsEnabled = list.SelectedItems.Count > 0;
        var tab = new TabControl { Items = { new TabItem { Header = "Tables", Content = list } } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { refresh, addButton, close } };
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom); DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(buttons); root.Children.Add(status); root.Children.Add(tab);
        Content = root;
        Loaded += async (_, _) => await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        status.Text = "Reading tables…"; addButton.IsEnabled = false;
        try
        {
            var tables = await load(CancellationToken.None);
            list.ItemsSource = tables;
            status.Text = tables.Count == 0 ? "Every table of the database is already in the diagram." : "";
            if (tables.Count > 0) { list.SelectedIndex = 0; (list.ItemContainerGenerator.ContainerFromIndex(0) as UIElement ?? list).Focus(); }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.IO.IOException or InvalidOperationException or TimeoutException) { status.Text = "The tables could not be read: " + ex.Message; }
    }

    public async Task AddSelectedAsync()
    {
        var chosen = list.SelectedItems.Cast<DiagramTableRef>().ToArray();
        if (chosen.Length == 0) return;
        addButton.IsEnabled = false;
        await add(chosen);
        await ReloadAsync();
    }
}

/// <summary>New Relationship, shown when a column is dragged onto another table of a diagram (pedido do usuário, 08/11/2026). SSMS 22.11 asks in two
/// dialogs, Tables and Columns and then Foreign Key Relationship; here one window has the name, the tables, the column pairs and the rules.</summary>
public sealed class NewRelationshipDialog : Window
{
    private static readonly string[] Actions = ["No Action", "Restrict", "Cascade", "Set Null", "Set Default"];
    private readonly IReadOnlyList<DiagramTableModel> tables;
    private readonly DiagramTableModel foreign;
    private readonly List<(ComboBox Primary, ComboBox Foreign)> pairs = [];
    private readonly StackPanel pairPanel = new();
    private readonly TextBlock warning = new() { Foreground = System.Windows.Media.Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    public TextBox NameBox { get; } = new() { Margin = new Thickness(0, 4, 0, 10) };
    public ComboBox PrimaryTable { get; } = new() { DisplayMemberPath = nameof(DiagramTableModel.Caption), Margin = new Thickness(0, 4, 8, 0) };
    public ComboBox DeleteRule { get; } = new() { ItemsSource = Actions, SelectedIndex = 0, Margin = new Thickness(0, 4, 8, 0) };
    public ComboBox UpdateRule { get; } = new() { ItemsSource = Actions, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 0) };
    public CheckBox CheckExisting { get; } = new() { Content = "Check existing data on creation", IsChecked = true, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Unchecked creates the key as NOT VALID: existing rows are not validated." };
    public TextBox DescriptionBox { get; } = new() { Margin = new Thickness(0, 4, 0, 0) };
    public DiagramTableModel? Primary => PrimaryTable.SelectedItem as DiagramTableModel;
    public string Warning => warning.Text;
    public ReferentialAction OnDelete => DesignerObjectKinds.ActionValue((string)DeleteRule.SelectedItem);
    public ReferentialAction OnUpdate => DesignerObjectKinds.ActionValue((string)UpdateRule.SelectedItem);

    public NewRelationshipDialog(IReadOnlyList<DiagramTableModel> tables, DiagramTableModel primary, DiagramTableModel foreign, string name, IReadOnlyList<(string Primary, string Foreign)> columns)
    {
        this.tables = tables; this.foreign = foreign;
        Title = "New Relationship"; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NameBox.Text = name;
        AutomationProperties.SetName(NameBox, "Relationship name"); AutomationProperties.SetName(PrimaryTable, "Primary key table");
        AutomationProperties.SetName(DeleteRule, "Delete rule"); AutomationProperties.SetName(UpdateRule, "Update rule"); AutomationProperties.SetName(DescriptionBox, "Description");
        PrimaryTable.ItemsSource = tables; PrimaryTable.SelectedItem = primary;
        var foreignBox = new TextBox { Text = foreign.Caption, IsReadOnly = true, Background = System.Windows.Media.Brushes.WhiteSmoke, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(foreignBox, "Foreign key table");
        Grid TwoColumns(string left, UIElement leftValue, string right, UIElement rightValue)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            void Put(UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
            Put(new TextBlock { Text = left }, 0, 0); Put(new TextBlock { Text = right }, 0, 1);
            Put(leftValue, 1, 0); Put(rightValue, 1, 1);
            return grid;
        }
        var heads = TwoColumns("Primary key table:", PrimaryTable, "Foreign key table:", foreignBox);
        var rules = TwoColumns("Delete rule:", DeleteRule, "Update rule:", UpdateRule);
        rules.Margin = new Thickness(0, 12, 0, 0);
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (Validate()) DialogResult = true; };
        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                new TextBlock { Text = "Relationship name:" }, NameBox, heads,
                new ScrollViewer { MaxHeight = 220, Margin = new Thickness(0, 8, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pairPanel },
                rules, CheckExisting,
                new TextBlock { Text = "Description:", Margin = new Thickness(0, 10, 0, 0) }, DescriptionBox,
                warning,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } }
            }
        };
        Fill(columns);
        PrimaryTable.SelectionChanged += (_, _) =>
        {
            // Another primary key table: its key columns, with the foreign key columns kept in order where they were chosen.
            if (Primary is not { } table) return;
            var kept = pairs.Select(p => p.Foreign.SelectedItem as string).Where(c => c is not null).ToList();
            var key = table.PrimaryKey.Count > 0 ? table.PrimaryKey : [table.Columns.First().Name];
            Fill(key.Select((c, i) => (c, i < kept.Count ? kept[i]! : "")).ToArray());
        };
        // Dropped on the title, the foreign key column is still to be chosen: the first empty one gets the focus.
        Loaded += (_, _) =>
        {
            if (pairs.FirstOrDefault(p => (p.Primary.SelectedItem as string)?.Length > 0 && (p.Foreign.SelectedItem as string) is null or "") is { Foreign: { } empty }) empty.Focus();
            else { NameBox.Focus(); NameBox.SelectAll(); }
        };
    }

    private Grid pairGrid = new();
    private void Fill(IReadOnlyList<(string Primary, string Foreign)> columns)
    {
        pairPanel.Children.Clear(); pairs.Clear();
        pairGrid = new Grid();
        pairGrid.ColumnDefinitions.Add(new()); pairGrid.ColumnDefinitions.Add(new());
        pairGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var h1 = new TextBlock { Text = Primary?.Caption ?? "", FontWeight = FontWeights.SemiBold }; var h2 = new TextBlock { Text = foreign.Caption, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(h2, 1); pairGrid.Children.Add(h1); pairGrid.Children.Add(h2);
        pairPanel.Children.Add(pairGrid);
        foreach (var (primaryColumn, foreignColumn) in columns) AddRow(primaryColumn, foreignColumn);
        Changed();
    }
    private void AddRow(string primaryColumn, string foreignColumn)
    {
        int row = pairGrid.RowDefinitions.Count; pairGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var p = new ComboBox { ItemsSource = (Primary?.Columns.Select(c => c.Name) ?? []).Prepend("").ToArray(), SelectedItem = primaryColumn, Margin = new Thickness(0, 3, 8, 0) };
        var f = new ComboBox { ItemsSource = foreign.Columns.Select(c => c.Name).Prepend("").ToArray(), SelectedItem = foreignColumn, Margin = new Thickness(0, 3, 0, 0) };
        AutomationProperties.SetName(p, $"Primary key column {row}"); AutomationProperties.SetName(f, $"Foreign key column {row}");
        Grid.SetRow(p, row); Grid.SetRow(f, row); Grid.SetColumn(f, 1);
        pairGrid.Children.Add(p); pairGrid.Children.Add(f); pairs.Add((p, f));
        p.SelectionChanged += (_, _) => Changed(); f.SelectionChanged += (_, _) => Changed();
    }
    // One empty pair stays at the end, as in the SSMS grid, to add a column to a composite key.
    private void Changed()
    {
        if (pairs.Count == 0 || (pairs[^1].Primary.SelectedItem as string)?.Length > 0 || (pairs[^1].Foreign.SelectedItem as string)?.Length > 0) AddRow("", "");
        warning.Text = Warnings();
    }    private static bool Complete((ComboBox Primary, ComboBox Foreign) pair) => (pair.Primary.SelectedItem as string)?.Length > 0 && (pair.Foreign.SelectedItem as string)?.Length > 0;
    /// <summary>The complete pairs, in order.</summary>
    public IReadOnlyList<(string Primary, string Foreign)> Columns => pairs.Where(Complete).Select(x => ((string)x.Primary.SelectedItem, (string)x.Foreign.SelectedItem)).ToArray();
    public void SetPair(int index, string primary, string foreignColumn) { pairs[index].Primary.SelectedItem = primary; pairs[index].Foreign.SelectedItem = foreignColumn; }

    /// <summary>What PostgreSQL would refuse at save time, said before: types that differ and referenced columns without a primary or unique key.</summary>
    private string Warnings()
    {
        if (Primary is not { } table) return "";
        var notes = new List<string>();
        foreach (var (p, f) in Columns)
            if (table.Column(p)?.Type is { } pt && foreign.Column(f)?.Type is { } ft && !string.Equals(pt, ft, StringComparison.Ordinal))
                notes.Add($"{f} ({ft}) and {p} ({pt}) have different data types; PostgreSQL refuses the key unless the types are comparable.");
        var referenced = Columns.Select(c => c.Primary).ToHashSet(StringComparer.Ordinal);
        if (referenced.Count > 0 && !table.Indexes.Any(i => i.Kind != DesignIndexKind.Index && i.FixedDefinition is null && i.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal).SetEquals(referenced)))
            notes.Add($"{table.Caption} has no primary or unique key on exactly these columns; PostgreSQL refuses the relationship.");
        return string.Join(Environment.NewLine, notes);
    }

    private bool Validate()
    {
        string error = NameBox.Text.Trim().Length == 0 ? "Enter the relationship name."
            : Columns.Count == 0 ? "Choose at least one pair of columns."
            : Columns.Select(c => c.Primary).Distinct().Count() != Columns.Count || Columns.Select(c => c.Foreign).Distinct().Count() != Columns.Count ? "A column can appear only once on each side."
            : "";
        if (error.Length == 0) return true;
        warning.Text = error;
        return false;
    }
}
