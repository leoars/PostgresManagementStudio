using System.Windows;
using System.Windows.Controls;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Restore ▸ Choose Items… (PGMS-064, M1100): the archive's table of contents with a check box per item, all checked at first (or the
/// earlier choice), a text filter, and Select All / Clear All for the items shown.</summary>
public sealed class ArchiveItemsDialog : Window
{
    private sealed class Row(ArchiveItem item, bool chosen)
    {
        public ArchiveItem Item { get; } = item;
        public CheckBox Box { get; } = new() { Content = item.Description, IsChecked = chosen, Margin = new Thickness(2) };
    }

    private readonly List<Row> rows;
    private readonly StackPanel list = new();
    private readonly TextBox filter = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock count = new() { Margin = new Thickness(0, 6, 0, 0) };
    internal TextBox Filter => filter;
    /// <summary>The checked items, in archive order, after OK.</summary>
    public IReadOnlyList<ArchiveItem> Chosen { get; private set; } = [];

    public ArchiveItemsDialog(IReadOnlyList<ArchiveItem> items, IReadOnlyList<ArchiveItem>? chosen)
    {
        Title = "Choose Items to Restore"; Width = 640; Height = 520; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        var ids = chosen?.Select(i => i.Id).ToHashSet();
        rows = items.Select(i => new Row(i, ids?.Contains(i.Id) ?? true)).ToList();
        foreach (var row in rows) { row.Box.Click += (_, _) => UpdateCount(); list.Children.Add(row.Box); }
        System.Windows.Automation.AutomationProperties.SetName(filter, "Filter items");
        filter.TextChanged += (_, _) => ApplyFilter();
        var all = new Button { Content = "_Select All", Padding = new Thickness(10, 2, 10, 2) };
        var none = new Button { Content = "C_lear All", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        all.Click += (_, _) => SetShown(true); none.Click += (_, _) => SetShown(false);
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(24, 4, 24, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => Accept();
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Items come from pg_restore --list. Items the chosen ones depend on (for example the table of a TABLE DATA item) are not added automatically.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        top.Children.Add(new Label { Content = "_Filter:", Target = filter, Padding = new Thickness(0, 0, 0, 2) });
        top.Children.Add(filter);
        var tools = new StackPanel { Orientation = Orientation.Horizontal }; tools.Children.Add(all); tools.Children.Add(none); tools.Children.Add(count);
        count.Margin = new Thickness(12, 0, 0, 0); count.VerticalAlignment = VerticalAlignment.Center;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(scroll, "Archive items");
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(tools, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(tools); root.Children.Add(buttons); root.Children.Add(scroll);
        Content = root;
        UpdateCount();
    }

    internal IEnumerable<CheckBox> Boxes => rows.Select(r => r.Box);

    private void ApplyFilter()
    {
        string text = filter.Text.Trim();
        foreach (var row in rows) row.Box.Visibility = text.Length == 0 || row.Item.Description.Contains(text, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    internal void SetShown(bool chosen)
    {
        foreach (var row in rows.Where(r => r.Box.Visibility == Visibility.Visible)) row.Box.IsChecked = chosen;
        UpdateCount();
    }

    private void UpdateCount() => count.Text = $"{rows.Count(r => r.Box.IsChecked == true):N0} of {rows.Count:N0} chosen";

    internal void Accept()
    {
        var picked = rows.Where(r => r.Box.IsChecked == true).Select(r => r.Item).ToList();
        if (picked.Count == 0) { count.Text = "Choose at least one item."; return; }
        Chosen = picked;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
}
