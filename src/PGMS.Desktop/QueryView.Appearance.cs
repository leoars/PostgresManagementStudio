using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Options ▸ Environment ▸ Fonts and Colors applied to one query window (PGMS-071): editor font, size and colors, and the fonts of
/// the result grids and of Results to Text. The zoom percentage is kept when the size changes.</summary>
public partial class QueryView
{
    private FontsAndColorsSettings appearance = new();
    internal FontsAndColorsSettings Appearance => appearance;

    internal void ApplyAppearance(FontsAndColorsSettings settings)
    {
        appearance = settings;
        int zoom = Zoom;
        BaseFontSize = FontsAndColorsSettings.Pixels(settings.EditorPoints);
        foreach (var editor in Editors()) ApplyEditorAppearance(editor, settings, BaseFontSize * zoom / 100.0);
        ResultsText.FontFamily = new FontFamily(settings.TextResultsFontName);
        ResultsText.FontSize = FontsAndColorsSettings.Pixels(settings.TextResultsPoints);
        foreach (var state in gridStates) ApplyGridAppearance(state.Grid, settings);
    }

    private IEnumerable<SqlTextEditor> Editors() => SplitEditor is { } split ? [Editor, split] : [Editor];

    internal static void ApplyEditorAppearance(SqlTextEditor editor, FontsAndColorsSettings settings, double fontSize)
    {
        var plain = settings.Item(FontsAndColorsSettings.PlainText);
        editor.FontFamily = new FontFamily(settings.EditorFontName);
        editor.FontSize = fontSize;
        editor.Foreground = FontsAndColorsSettings.Brush(plain.Foreground);
        editor.Background = FontsAndColorsSettings.Brush(plain.Background);
        editor.LineNumbersForeground = FontsAndColorsSettings.Brush(settings.Item(FontsAndColorsSettings.LineNumber).Foreground);
        editor.TextArea.SelectionBrush = FontsAndColorsSettings.SelectionBrush(settings.Item(FontsAndColorsSettings.SelectedText).Background);
        // A window shown without SQL colors (a JSON document) keeps none.
        if (editor.SyntaxHighlighting is not null) editor.SyntaxHighlighting = settings.Highlighting();
    }

    /// <summary>Rows grow with the font; at the default 9 points they keep the 19 pixels of the SSMS grid.</summary>
    internal static void ApplyGridAppearance(DataGrid grid, FontsAndColorsSettings settings)
    {
        double size = FontsAndColorsSettings.Pixels(settings.GridPoints);
        grid.FontFamily = new FontFamily(settings.GridFontName);
        grid.FontSize = size;
        grid.RowHeight = Math.Max(19, Math.Ceiling(size + 7));
    }
}
