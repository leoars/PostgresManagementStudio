using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop.ViewModels;

namespace PGMS.Desktop;

public sealed record FilterOperatorChoice(CatalogFilterOperator Value, string Label);

/// <summary>One line of the Filter Settings grid.</summary>
public sealed class FilterCriterion(string property, CatalogFilterOperator op, string value, string description) : INotifyPropertyChanged
{
    public static IReadOnlyList<FilterOperatorChoice> Operators { get; } = [new(CatalogFilterOperator.Contains, "Contains"), new(CatalogFilterOperator.Equals, "Equals"), new(CatalogFilterOperator.DoesNotContain, "Does not contain")];
    public string Property => property;
    public string Description => description;
    public string OperatorName => $"{property} operator";
    public string ValueName => $"{property} value";
    public CatalogFilterOperator Operator { get; set; } = op;
    private string text = value;
    public string Value { get => text; set { text = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>SSMS Filter Settings. Properties a PostgreSQL catalog does not hold (creation date, memory-optimized) are not offered, and folders already belong to one schema.</summary>
public partial class FilterSettingsDialog : Window
{
    private readonly FilterCriterion name;
    private readonly FilterCriterion? owner;
    internal IReadOnlyList<FilterCriterion> Rows { get; }
    public CatalogFilter Result { get; private set; } = CatalogFilter.None;

    public FilterSettingsDialog(string server, string? database, CatalogFilter current, bool canFilterByOwner)
    {
        InitializeComponent();
        ServerText.Text = server; DatabaseText.Text = database ?? "";
        DatabaseLabel.Visibility = DatabaseText.Visibility = database is null ? Visibility.Collapsed : Visibility.Visible;
        name = new("Name", current.NameOperator, current.Name, "Include or exclude objects based on the name or part of a name.");
        owner = canFilterByOwner ? new("Owner", current.OwnerOperator, current.Owner, "Include or exclude objects based on the role that owns them.") : null;
        Rows = owner is null ? [name] : [name, owner];
        Criteria.ItemsSource = Rows; Criteria.SelectedIndex = 0;
    }

    private void CriterionSelected(object sender, SelectionChangedEventArgs e) => Description.Text = (Criteria.SelectedItem as FilterCriterion)?.Description ?? "";
    // Clicking an editor inside a row does not select the row by itself.
    private void CriterionFocused(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FilterCriterion row }) Criteria.SelectedItem = row;
    }
    private void ClearFilter(object sender, RoutedEventArgs e) { foreach (var row in Rows) row.Value = ""; }
    private void Accept(object sender, RoutedEventArgs e)
    {
        Result = new(name.Value.Trim(), name.Operator, owner?.Value.Trim() ?? "", owner?.Operator ?? CatalogFilterOperator.Equals);
        DialogResult = true;
    }
}
