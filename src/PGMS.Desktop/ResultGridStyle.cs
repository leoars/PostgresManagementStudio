using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>True for the cell at the grid's current column and item: the SSMS results grid outlines it with a dotted rectangle, focused or not.</summary>
public sealed class IsCurrentCellConverter : IMultiValueConverter
{
    public static readonly IsCurrentCellConverter Instance = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 4 && values[0] is DataGridColumn column && ReferenceEquals(column, values[2]) && values[1] is not null && ReferenceEquals(values[1], values[3]);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True for a null database value.</summary>
public sealed class IsDbNullConverter : IValueConverter
{
    public static readonly IsDbNullConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null or DBNull;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

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
    }

    /// <summary>Cell style of a column bound to <paramref name="path"/>: the SSMS cell, yellow when the value is NULL.</summary>
    internal static Style NullAwareCell(string path)
    {
        var style = new Style(typeof(DataGridCell), Application.Current?.TryFindResource("SsmsResultCell") as Style);
        style.Triggers.Add(new DataTrigger { Binding = new Binding(path) { Converter = IsDbNullConverter.Instance }, Value = true, Setters = { new Setter(Control.BackgroundProperty, NullBackground) } });
        return style;
    }

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
