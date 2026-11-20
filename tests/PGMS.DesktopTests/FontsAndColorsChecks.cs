using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using PGMS.Desktop;

/// <summary>PGMS-071 (M1098): Tools ▸ Options ▸ Environment ▸ Fonts and Colors and its effect on query windows.</summary>
internal static class FontsAndColorsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var defaults = new FontsAndColorsSettings();
        check(defaults.EditorFontName == "Consolas" && FontsAndColorsSettings.Pixels(defaults.EditorPoints) == 14 && FontsAndColorsSettings.Pixels(defaults.GridPoints) == 12 && FontsAndColorsSettings.Pixels(defaults.TextResultsPoints) == 13,
            "The defaults are the fonts and sizes PGMS used before (14, 12 and 13 pixels)");
        check(defaults.Item(FontsAndColorsSettings.Keyword).Foreground == "#0000FF" && defaults.Item(FontsAndColorsSettings.Comment).Foreground == "#008000" && defaults.Item(FontsAndColorsSettings.SqlString).Foreground == "#A31515",
            "Keyword, Comment and SQL String start with the colors of the SQL highlighting");
        check(FontsAndColorsSettings.ParseColor("Maroon") == Color.FromRgb(0x80, 0, 0) && FontsAndColorsSettings.ParseColor("#12ab9C") == Color.FromRgb(0x12, 0xAB, 0x9C) && FontsAndColorsSettings.ParseColor("12AB9C") is null && FontsAndColorsSettings.ParseColor("Rosa") is null,
            "Colors are a Visual Studio name or #RRGGBB");
        check(FontsAndColorsSettings.ParseSize("12") == 12 && FontsAndColorsSettings.ParseSize("10.5") == 10.5 && FontsAndColorsSettings.ParseSize("5") is null && FontsAndColorsSettings.ParseSize("73") is null && FontsAndColorsSettings.ParseSize("x") is null,
            "Sizes go from 6 to 72 points");
        var odd = new FontsAndColorsSettings(EditorItems: new Dictionary<string, ColorItem> { [FontsAndColorsSettings.Keyword] = new("bad", "#FFFFFF", true), [FontsAndColorsSettings.SelectedText] = new("#FF0000", "#00FF00", true) });
        check(odd.Item(FontsAndColorsSettings.Keyword) == new ColorItem("#0000FF", null, true) && odd.Item(FontsAndColorsSettings.SelectedText) == new ColorItem(null, "#00FF00", false),
            "An unreadable color falls back to the default, and an item keeps only what it supports");

        // The highlighting definition carries the chosen colors; the same colors give the same definition.
        var red = new FontsAndColorsSettings(EditorItems: new Dictionary<string, ColorItem> { [FontsAndColorsSettings.Keyword] = new("#FF0000", null, true) });
        var definition = red.Highlighting();
        check(definition.GetNamedColor("Keyword") is { } keyword && keyword.Foreground.GetColor(null) == Color.FromRgb(0xFF, 0, 0) && keyword.FontWeight == FontWeights.Bold && definition.GetNamedColor("Comment").Foreground.GetColor(null) == Color.FromRgb(0, 0x80, 0),
            "Keyword turns red and bold in the SQL highlighting, the others keep theirs");
        check(ReferenceEquals(definition, new FontsAndColorsSettings(EditorItems: new Dictionary<string, ColorItem> { [FontsAndColorsSettings.Keyword] = new("#FF0000", null, true) }).Highlighting()), "The same colors reuse the definition");

        // The page.
        string directory = Path.Combine(Path.GetTempPath(), "pgms-fonts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var store = new EnvironmentOptions(Path.Combine(directory, "environment.json"));
        var dialog = new OptionsDialog(store) { ShowInTaskbar = false }; dialog.Show(); await Idle();
        var page = (TreeViewItem)dialog.FindName("FontsAndColorsPage");
        var environment = (TreeViewItem)page.Parent;
        check((string)environment.Header == "Environment" && environment.Items.IndexOf(page) == 3, "Fonts and Colors is under Environment, after General, AutoRecover and Documents (SSMS 22.11 order, M1141)");
        page.IsSelected = true; await Idle();
        check(dialog.FontsAndColorsPanel.Visibility == Visibility.Visible && dialog.FontCategoryBox.Items.Cast<string>().SequenceEqual(FontsAndColorsSettings.Categories) && (string)dialog.FontCategoryBox.SelectedItem == "Text Editor",
            "Show settings for lists Text Editor, Grid Results and Text Results, Text Editor first");
        check(dialog.FontNameBox.Text == "Consolas" && dialog.FontSizeBox.Text == 10.5.ToString(System.Globalization.CultureInfo.CurrentCulture) && dialog.DisplayItemsBox.Items.Count == 6 && (string)dialog.DisplayItemsBox.SelectedItem == "Plain Text",
            "Text Editor shows Consolas 10.5 and six display items");
        check(dialog.ItemForegroundBox.Text == "Default" && dialog.ItemBackgroundBox.IsEnabled && !dialog.ItemBoldBox.IsEnabled, "Plain Text has foreground and background, no bold");
        dialog.OkButton.Focus();
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        check(store.Load().FontsAndColors is null, "OK without changes stores nothing");

        dialog = new OptionsDialog(store) { ShowInTaskbar = false }; dialog.Show(); await Idle();
        ((TreeViewItem)dialog.FindName("FontsAndColorsPage")).IsSelected = true; await Idle();
        dialog.FontNameBox.Text = "Courier New"; dialog.FontSizeBox.Text = "12";
        dialog.DisplayItemsBox.SelectedItem = FontsAndColorsSettings.Keyword; await Idle();
        check(dialog.ItemBoldBox.IsEnabled && !dialog.ItemBackgroundBox.IsEnabled, "Keyword has foreground and bold, no background");
        dialog.ItemForegroundBox.Text = "Maroon"; dialog.ItemBoldBox.IsChecked = true;
        dialog.UpdateFontSample(); await Idle();
        check(dialog.FontSample.FontWeight == FontWeights.Bold && ((SolidColorBrush)dialog.FontSample.Foreground).Color == Color.FromRgb(0x80, 0, 0) && dialog.FontSample.FontFamily.Source == "Courier New" && dialog.FontSample.FontSize == 16,
            "The sample shows the chosen font, size, color and weight");
        dialog.FontCategoryBox.SelectedIndex = 1; await Idle();
        check(dialog.FontNameBox.Text == "Segoe UI" && dialog.FontSizeBox.Text == "9" && dialog.DisplayItemsBox.Visibility == Visibility.Collapsed && dialog.ItemColorsPanel.Visibility == Visibility.Collapsed,
            "Grid Results shows Segoe UI 9 and no display items");
        dialog.FontSizeBox.Text = "80";
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        check(dialog.IsVisible && dialog.FontsError.Visibility == Visibility.Visible && dialog.FontsError.Text.Contains("Grid Results", StringComparison.Ordinal) && store.Load().FontsAndColors is null,
            "A size outside 6 to 72 keeps the dialog open and says which category: " + dialog.FontsError.Text);
        dialog.FontSizeBox.Text = "11";
        dialog.FontCategoryBox.SelectedIndex = 2; await Idle();
        dialog.FontNameBox.Text = "No Such Font PGMS";
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        check(dialog.IsVisible && dialog.FontsError.Text.Contains("not installed", StringComparison.Ordinal), "A font that is not installed is refused");
        dialog.FontNameBox.Text = "Consolas";
        dialog.FontCategoryBox.SelectedIndex = 0; await Idle();
        check(dialog.FontNameBox.Text == "Courier New" && dialog.FontSizeBox.Text == "12", "Switching categories keeps what was typed");
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        var saved = store.Load().Appearance;
        check(saved.EditorFontName == "Courier New" && saved.EditorPoints == 12 && saved.GridPoints == 11 && saved.GridFont is null && saved.TextResultsFont is null && saved.TextResultsSize == 0
              && saved.Item(FontsAndColorsSettings.Keyword) == new ColorItem("#800000", null, true) && saved.EditorItems?.Count == 1,
            "OK stores only what differs from the defaults");

        dialog = new OptionsDialog(store) { ShowInTaskbar = false }; dialog.Show(); await Idle();
        ((TreeViewItem)dialog.FindName("FontsAndColorsPage")).IsSelected = true; await Idle();
        dialog.DisplayItemsBox.SelectedItem = FontsAndColorsSettings.Keyword; await Idle();
        check(dialog.FontNameBox.Text == "Courier New" && dialog.ItemForegroundBox.Text == "Maroon" && dialog.ItemBoldBox.IsChecked == true, "The page reopens with what was saved");
        dialog.ItemForegroundBox.Text = "pink";
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        check(dialog.IsVisible && dialog.FontsError.Text.Contains("#RRGGBB", StringComparison.Ordinal), "A color that is not in the list nor #RRGGBB is refused");
        dialog.ItemForegroundBox.Text = "#123456";
        dialog.ResetFonts(allCategories: false); await Idle();
        check(dialog.FontNameBox.Text == "Consolas" && dialog.ItemForegroundBox.Text == "Default" && dialog.ItemBoldBox.IsChecked == false, "Reset category to defaults brings Text Editor back");
        dialog.FontCategoryBox.SelectedIndex = 1; await Idle();
        check(dialog.FontSizeBox.Text == "11", "Resetting one category leaves the others");
        dialog.ResetFonts(allCategories: true); await Idle();
        check(dialog.FontSizeBox.Text == "9", "Reset all categories brings every category back");
        dialog.Accept(dialog, new RoutedEventArgs()); await Idle();
        check(store.Load().FontsAndColors is null, "After a reset OK stores nothing again");
        try { Directory.Delete(directory, true); } catch (IOException) { }

        // Applied to a query window: editor, zoom, split view, grids and Results to Text.
        var before = FontsAndColorsSettings.Current;
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            var query = window.NewQueryTab("SELECT 1; -- note"); await Idle();
            check(query.Editor.FontFamily.Source == "Consolas" && query.Editor.FontSize == 14 && query.Zoom == 100, "A new query starts in Consolas 14 pixels at 100%");
            query.Zoom = 200;
            var chosen = new FontsAndColorsSettings("Courier New", 12, new Dictionary<string, ColorItem>
            {
                [FontsAndColorsSettings.PlainText] = new("#000080", "#FFFFE0"), [FontsAndColorsSettings.Keyword] = new("#800000", null, true), [FontsAndColorsSettings.LineNumber] = new("#FF0000"),
            }, "Consolas", 12, "Courier New", 11);
            query.ApplyAppearance(chosen); await Idle();
            check(query.Editor.FontFamily.Source == "Courier New" && Math.Abs(query.Editor.FontSize - 32) < 0.01 && query.Zoom == 200, "The editor takes the font and size and keeps its 200% zoom");
            check(((SolidColorBrush)query.Editor.Foreground).Color == Color.FromRgb(0, 0, 0x80) && ((SolidColorBrush)query.Editor.Background).Color == Color.FromRgb(0xFF, 0xFF, 0xE0) && ((SolidColorBrush)query.Editor.LineNumbersForeground).Color == Color.FromRgb(0xFF, 0, 0),
                "Plain Text and Line Number colors reach the editor");
            check(query.Editor.SyntaxHighlighting?.GetNamedColor("Keyword")?.Foreground.GetColor(null) == Color.FromRgb(0x80, 0, 0), "The editor highlights keywords in the chosen color");
            check(query.ResultsText.FontFamily.Source == "Courier New" && Math.Abs(query.ResultsText.FontSize - 11 * 96 / 72.0) < 0.01, "Results to Text takes its font");
            query.Zoom = 100;
            check(Math.Abs(query.Editor.FontSize - 16) < 0.01, "100% is the chosen size (12 points, 16 pixels)");
            query.SetSplit(true); await Idle();
            check(query.SplitEditor is { } split && split.FontFamily.Source == "Courier New" && ((SolidColorBrush)split.Background).Color == Color.FromRgb(0xFF, 0xFF, 0xE0), "Split Tab's second view uses the same font and colors");
            query.SetSplit(false); await Idle();
            var grid = new DataGrid();
            QueryView.ApplyGridAppearance(grid, chosen);
            check(grid.FontFamily.Source == "Consolas" && Math.Abs(grid.FontSize - 16) < 0.01 && grid.RowHeight == 23, "Grid Results takes its font and the rows grow with it");
            QueryView.ApplyGridAppearance(grid, defaults);
            check(grid.RowHeight == 19 && grid.FontSize == 12, "At the default size the grid keeps 19-pixel rows");
            FontsAndColorsSettings.Current = chosen;
            var second = window.NewQueryTab("SELECT 2;"); await Idle();
            check(second.Editor.FontFamily.Source == "Courier New" && Math.Abs(second.Editor.FontSize - 16) < 0.01, "Query windows opened later use the current settings");
        }
        finally { FontsAndColorsSettings.Current = before; foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false; window.Close(); }
    }
}
