using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>One row of the designer property grid. A property without a setter is read-only, shown in gray like the SSMS 22.11 Column Properties.</summary>
public sealed class DesignerProperty(string category, string name, string description, Func<string> get, Action<string>? set = null, IReadOnlyList<string>? choices = null)
{
    public string Category { get; } = category;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public Func<string> Get { get; } = get;
    public Action<string>? Set { get; } = set;
    public IReadOnlyList<string>? Choices { get; } = choices;
    public Func<bool> IsVisible { get; init; } = () => true;
    public Func<bool> IsEditable { get; init; } = () => true;
}

/// <summary>Categorized or alphabetical name/value list with a description box, the part of the SSMS property window that the Table Designer panes and dialogs share.</summary>
public sealed class DesignerPropertyGrid : UserControl
{
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly Brush DisabledBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private sealed record Row(DesignerProperty Property, FrameworkElement Container, FrameworkElement Label, FrameworkElement Editor);

    private readonly StackPanel rows = new();
    private readonly TextBlock descriptionTitle = new() { FontWeight = FontWeights.Bold };
    private readonly TextBlock descriptionText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ToggleButton categorized = new() { Content = "≡", ToolTip = "Categorized", IsChecked = true, Width = 22, Height = 20, Margin = new Thickness(0, 0, 2, 0) };
    private readonly ToggleButton alphabetical = new() { Content = "AZ", ToolTip = "Alphabetical", Width = 26, Height = 20, FontSize = 9 };
    private readonly Border box;
    private readonly List<Row> shown = [];
    private readonly HashSet<string> collapsed = [];
    private IReadOnlyList<DesignerProperty> properties = [];
    private bool refreshing;

    /// <summary>Raised after a property value is written, so the owner can react to it.</summary>
    public event Action? ValueChanged;
    public bool IsReadOnly { get; set; }
    /// <summary>The SSMS object dialogs have their help box above the list instead of a description box below it.</summary>
    public bool ShowDescription { get => box.Visibility == Visibility.Visible; set => box.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public DesignerPropertyGrid()
    {
        AutomationProperties.SetName(categorized, "Categorized"); AutomationProperties.SetName(alphabetical, "Alphabetical");
        categorized.Click += (_, _) => { categorized.IsChecked = true; alphabetical.IsChecked = false; Build(); };
        alphabetical.Click += (_, _) => { alphabetical.IsChecked = true; categorized.IsChecked = false; Build(); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        bar.Children.Add(categorized); bar.Children.Add(alphabetical);
        DockPanel.SetDock(bar, Dock.Top);
        box = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 0), Height = 58, Child = new StackPanel { Children = { descriptionTitle, descriptionText } }
        };
        DockPanel.SetDock(box, Dock.Bottom);
        var list = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)), BorderThickness = new Thickness(1), Background = Brushes.White, Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows } };
        Content = new DockPanel { Children = { bar, box, list } };
    }

    public void Show(IReadOnlyList<DesignerProperty> items) { properties = items; Build(); }

    /// <summary>Reads every value again without rebuilding, so keyboard focus stays where it is.</summary>
    public void RefreshValues()
    {
        refreshing = true;
        foreach (var row in shown)
        {
            bool open = alphabetical.IsChecked == true || !collapsed.Contains(row.Property.Category);
            row.Container.Visibility = open && row.Property.IsVisible() ? Visibility.Visible : Visibility.Collapsed;
            bool editable = Editable(row.Property);
            row.Label.SetValue(TextBlock.ForegroundProperty, editable ? Brushes.Black : DisabledBrush);
            if (row.Editor is TextBox text) { if (!text.IsKeyboardFocusWithin) text.Text = row.Property.Get(); text.IsReadOnly = !editable; text.Foreground = editable ? Brushes.Black : DisabledBrush; }
            else if (row.Editor is ComboBox combo) { combo.SelectedItem = row.Property.Get(); combo.IsEnabled = editable; }
        }
        refreshing = false;
    }

    private bool Editable(DesignerProperty property) => !IsReadOnly && property.Set is not null && property.IsEditable();

    private void Build()
    {
        rows.Children.Clear(); shown.Clear();
        descriptionTitle.Text = properties.Count == 0 ? "" : alphabetical.IsChecked == true ? properties.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).First().Name : properties[0].Category; descriptionText.Text = "";
        if (alphabetical.IsChecked == true) foreach (var property in properties.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) AddRow(property);
        else foreach (var group in properties.GroupBy(p => p.Category))
        {
            string category = group.Key;
            var header = new ToggleButton { Content = category, IsChecked = !collapsed.Contains(category) };
            header.SetResourceReference(StyleProperty, "DesignerCategoryToggle");
            AutomationProperties.SetName(header, category);
            header.Checked += (_, _) => { collapsed.Remove(category); RefreshValues(); };
            header.Unchecked += (_, _) => { collapsed.Add(category); RefreshValues(); };
            rows.Children.Add(header);
            foreach (var property in group) AddRow(property);
        }
        RefreshValues();
    }

    private void AddRow(DesignerProperty property)
    {
        var label = new TextBlock { Text = property.Name, Margin = new Thickness(18, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = property.Name };
        FrameworkElement editor;
        if (property.Choices is { } choices)
        {
            var combo = new ComboBox { ItemsSource = choices, BorderThickness = new Thickness(0), Background = Brushes.White, Padding = new Thickness(3, 0, 0, 0), MinHeight = 18 };
            combo.SetResourceReference(StyleProperty, "DesignerPropertyCombo");
            combo.SelectionChanged += (_, _) => { if (!refreshing && combo.SelectedItem is string value) Write(property, value); };
            editor = combo;
        }
        else
        {
            var text = new TextBox { BorderThickness = new Thickness(0), Padding = new Thickness(1, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 18 };
            text.LostKeyboardFocus += (_, _) => { if (!text.IsReadOnly && text.Text != property.Get()) Write(property, text.Text); };
            text.KeyDown += (_, e) => { if (e.Key == Key.Enter && !text.IsReadOnly) { Write(property, text.Text); e.Handled = true; } };
            editor = text;
        }
        AutomationProperties.SetName(editor, property.Name);
        editor.GotKeyboardFocus += (_, _) => { descriptionTitle.Text = property.Name; descriptionText.Text = property.Description; };
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
        Grid.SetColumn(editor, 1); line.Children.Add(label); line.Children.Add(editor);
        var border = new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = line };
        rows.Children.Add(border);
        shown.Add(new(property, border, label, editor));
    }

    private void Write(DesignerProperty property, string value)
    {
        if (!Editable(property)) return;
        property.Set!(value);
        ValueChanged?.Invoke();
        RefreshValues();
    }
}
