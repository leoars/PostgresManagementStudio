using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Editor information bar (PGMS-049), modelled on SSMS 22.11 as piloted in M1078: zoom, Ln/Ch, indentation, line breaks and encoding.</summary>
public partial class QueryView
{
    internal static readonly int[] ZoomLevels = [20, 50, 70, 100, 150, 200, 400];
    /// <summary>Editor font size at 100% zoom, in device-independent pixels; Fonts and Colors sets it (14 by default).</summary>
    private double BaseFontSize = 14;
    private SqlFileFormat fileFormat = SqlFileFormat.Utf8;
    public SqlFileFormat FileFormat { get => fileFormat; internal set { fileFormat = value; UpdateEditorInfo(); } }

    /// <summary>Zoom of the editor in percent (20 to 400); not saved with the document.</summary>
    public int Zoom
    {
        get => (int)Math.Round(Editor.FontSize / BaseFontSize * 100);
        set
        {
            int zoom = Math.Clamp(value, ZoomLevels[0], ZoomLevels[^1]);
            Editor.FontSize = BaseFontSize * zoom / 100.0;
            int index = Array.IndexOf(ZoomLevels, zoom);
            if (ZoomBox.SelectedIndex != index) ZoomBox.SelectedIndex = index;
        }
    }

    internal static string EncodingName(SqlFileFormat format) => format switch
    {
        SqlFileFormat.Utf8 => "UTF-8",
        SqlFileFormat.Utf8Bom => "UTF-8 BOM",
        SqlFileFormat.Utf16LittleEndian => "UTF-16 LE",
        SqlFileFormat.Utf16BigEndian => "UTF-16 BE",
        SqlFileFormat.Utf32LittleEndian => "UTF-32 LE",
        _ => "UTF-32 BE",
    };

    private void ConfigureInfoBar()
    {
        foreach (int level in ZoomLevels) ZoomBox.Items.Add($"{level} %");
        ZoomBox.SelectedIndex = Array.IndexOf(ZoomLevels, 100);
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretInfo();
        Editor.TextChanged += (_, _) => UpdateLineEndingInfo();
        Editor.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            int index = Array.IndexOf(ZoomLevels, Zoom);
            if (index < 0) index = Array.FindLastIndex(ZoomLevels, z => z < Zoom) + (e.Delta > 0 ? 0 : 1);
            Zoom = ZoomLevels[Math.Clamp(index + (e.Delta > 0 ? 1 : -1), 0, ZoomLevels.Length - 1)];
            e.Handled = true;
        };
        // In a very small tab the editor keeps its room: the bar hides below 160 px for the editor area and comes back above it.
        if (Editor.Parent is FrameworkElement area)
            area.SizeChanged += (_, e) => EditorInfoBar.Visibility = e.NewSize.Height < MinimumHeightForInfoBar ? Visibility.Collapsed : Visibility.Visible;
        UpdateEditorInfo();
    }

    internal const double MinimumHeightForInfoBar = 160;

    private void ZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedIndex >= 0) Zoom = ZoomLevels[ZoomBox.SelectedIndex];
    }

    internal void UpdateEditorInfo()
    {
        if (EncodingButton is null) return;
        UpdateCaretInfo(); UpdateLineEndingInfo();
        IndentationButton.Content = Editor.Options.ConvertTabsToSpaces ? "SPC" : "TABS";
        EncodingButton.Content = EncodingName(fileFormat);
    }

    private void UpdateCaretInfo()
    {
        var caret = Editor.TextArea.Caret;
        CaretPositionButton.Content = $"Ln: {caret.Line:N0}, Ch: {caret.Column:N0}";
    }

    private void UpdateLineEndingInfo() => LineEndingButton.Content = SqlTextCommands.LineEndingName(Editor.Document);

    /// <summary>The TABS, CRLF and encoding buttons open their menu above the bar, with the current choice checked.</summary>
    private void OpenInfoMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        UpdateEditorInfo();
        string current = button.Content as string ?? "";
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            object? header = Localization.EnglishHeader(item, item.Header); // The English text: the menu may be translated (M1126).
            item.IsChecked = button == IndentationButton ? Equals(header, current == "SPC" ? "Spaces" : "Tabs") : button == LineEndingButton && Equals(header, current);
        }
        menu.PlacementTarget = button; menu.Placement = PlacementMode.Top; menu.IsOpen = true;
    }
}
