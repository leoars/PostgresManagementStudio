using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>True for a cell value that is a JSON object or array.</summary>
public sealed class IsJsonLinkConverter : IValueConverter
{
    public static readonly IsJsonLinkConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string text && PGMS.PostgreSql.ResultJson.IsDocument(text);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Results grid with the SSMS 22.11 look (styles in Resources/Shell.xaml): numbered row headers and NULL cells in light yellow.</summary>
internal static class ResultGridStyle
{
    internal static readonly Brush NullBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xFB, 0xAC)));
    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }

    internal static void Apply(DataGrid grid)
    {
        if (Application.Current?.TryFindResource("SsmsResultGrid") is Style style) grid.Style = style;
        grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);
        // The dotted rectangle follows the current cell; a cell generated later is marked by ResultTextColumn.
        object? lastItem = null; DataGridColumn? lastColumn = null;
        grid.CurrentCellChanged += (_, _) =>
        {
            if (CellContainer(grid, lastItem, lastColumn) is { } previous) ResultCellChrome.SetIsCurrentCell(previous, false);
            lastItem = grid.CurrentItem; lastColumn = grid.CurrentColumn;
            if (CellContainer(grid, lastItem, lastColumn) is { } current) ResultCellChrome.SetIsCurrentCell(current, true);
        };
    }

    private static DataGridCell? CellContainer(DataGrid grid, object? item, DataGridColumn? column)
    {
        if (column is null || item is null || !grid.Columns.Contains(column)) return null;
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return null;
        for (DependencyObject? node = column.GetCellContent(row); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is DataGridCell cell) return cell;
        return null;
    }

    /// <summary>A results column bound to <paramref name="columnName"/> of a <see cref="System.Data.DataView"/>: the SSMS cell, yellow when the value is NULL.</summary>
    internal static ResultTextColumn TextColumn(string header, string columnName) => new()
    {
        Header = header, SortMemberPath = columnName, NullColumn = columnName,
        CellStyle = Application.Current?.TryFindResource("SsmsResultCell") as Style,
        Binding = new Binding($"[{columnName}]") { Converter = new DatabaseValueConverter() }
    };

    /// <summary>Text of a column that may hold JSON: a value that is a JSON document looks like a link and a click on it opens it. A press that started on
    /// another cell (a drag selection) does not count, nor does a click extending the selection.</summary>
    internal static Style JsonLinkText(string path, Action<string> open)
    {
        TextBlock? pressed = null;
        var style = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
        style.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(path) { Converter = IsJsonLinkConverter.Instance }, Value = true,
            Setters = { new Setter(TextBlock.ForegroundProperty, LinkBrush), new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline), new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand) }
        });
        style.Setters.Add(new EventSetter(UIElement.MouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler((sender, _) => pressed = sender as TextBlock)));
        style.Setters.Add(new EventSetter(UIElement.MouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler((sender, _) =>
        {
            if (sender is TextBlock text && ReferenceEquals(pressed, text) && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None) open(text.Text);
            pressed = null;
        })));
        return style;
    }

    private static readonly Brush LinkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xCC)));

    /// <summary>Row headers grow with the number of digits of the last row (32 px up to three digits, as in SSMS).</summary>
    internal static void FitRowHeaders(DataGrid grid, int rows)
    {
        int digits = Math.Max(1, rows.ToString(CultureInfo.InvariantCulture).Length);
        grid.RowHeaderWidth = Math.Max(32, 10 + 7 * digits);
    }
}
