using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace PGMS.Desktop;

// Gives each DataGrid row a short UI Automation name built from its first visible columns.
// Without it WPF announces item.ToString(), which for records lists every internal field.
public static class DataGridRowNames
{
    public const int MaximumValueLength = 64;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DataGridRowNames), new PropertyMetadata(false, EnabledChanged));

    public static readonly DependencyProperty ColumnCountProperty = DependencyProperty.RegisterAttached(
        "ColumnCount", typeof(int), typeof(DataGridRowNames), new PropertyMetadata(3, ColumnCountChanged), value => value is >= 1 and <= 8);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static int GetColumnCount(DependencyObject element) => (int)element.GetValue(ColumnCountProperty);
    public static void SetColumnCount(DependencyObject element, int value) => element.SetValue(ColumnCountProperty, value);

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not DataGrid grid) return;
        grid.LoadingRow -= LoadingRow;
        grid.CellEditEnding -= CellEditEnding;
        if (e.NewValue is not true) return;
        grid.LoadingRow += LoadingRow;
        grid.CellEditEnding += CellEditEnding;
        RefreshRealizedRows(grid);
    }

    private static void ColumnCountChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is DataGrid grid && GetEnabled(grid)) RefreshRealizedRows(grid);
    }

    private static void LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (sender is DataGrid grid) Apply(grid, e.Row);
    }

    // Edited values commit after this event; refresh once the binding has updated the item.
    private static void CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var row = e.Row;
        grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Apply(grid, row));
    }

    public static void RefreshRealizedRows(DataGrid grid)
    {
        foreach (var item in grid.Items)
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row) Apply(grid, row);
    }

    private static void Apply(DataGrid grid, DataGridRow row)
    {
        if (row.Item is null || row.Item == CollectionView.NewItemPlaceholder) { row.ClearValue(AutomationProperties.NameProperty); return; }
        row.SetValue(AutomationProperties.NameProperty, Compose(grid, row.Item));
    }

    public static string Compose(DataGrid grid, object item)
    {
        var parts = new StringBuilder();
        int used = 0, limit = GetColumnCount(grid);
        foreach (var column in grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex))
        {
            if (used == limit) break;
            if (parts.Length > 0) parts.Append(", ");
            string header = HeaderText(column.Header);
            if (header.Length > 0) parts.Append(header).Append(' ');
            parts.Append(ValueText(column.OnCopyingCellClipboardContent(item)));
            used++;
        }
        return parts.Length == 0 ? "Row" : parts.ToString();
    }

    private static string HeaderText(object? header) => header switch
    {
        null => "",
        string text => text,
        TextBlock block => block.Text,
        DependencyObject element when AutomationProperties.GetName(element) is { Length: > 0 } name => name,
        _ => header.ToString() ?? ""
    };

    private static string ValueText(object? value)
    {
        string text = value switch
        {
            null => "",
            bool flag => flag ? "Yes" : "No",
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? ""
        };
        text = text.ReplaceLineEndings(" ").Trim();
        if (text.Length == 0) return "(empty)";
        if (text.Length <= MaximumValueLength) return text;
        int cut = MaximumValueLength - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return string.Concat(text.AsSpan(0, cut), "…");
    }
}
