using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Environment ▸ Fonts and Colors (PGMS-071). The page edits a working copy; OK saves it and the main window applies it to every open query
/// window. Colors are a name of the Visual Studio list or #RRGGBB; "Default" (empty) keeps the PGMS color.</summary>
public partial class OptionsDialog
{
    private const string DefaultColorText = "Default";
    private static readonly double[] StandardSizes = [6, 7, 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];
    private readonly string[] fontNames = new string[3];
    private readonly string[] fontSizes = new string[3];
    private readonly Dictionary<string, ColorItem> itemDrafts = [];
    private int fontCategory = -1;
    private string? shownItem;
    private bool loadingFonts;

    private void LoadFontsAndColors(FontsAndColorsSettings settings)
    {
        loadingFonts = true;
        try
        {
            if (FontCategoryBox.Items.Count == 0)
            {
                foreach (var category in FontsAndColorsSettings.Categories) FontCategoryBox.Items.Add(category);
                foreach (var family in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)) FontNameBox.Items.Add(family);
                foreach (double size in StandardSizes) FontSizeBox.Items.Add(SizeText(size));
                foreach (var item in FontsAndColorsSettings.EditorItemNames) DisplayItemsBox.Items.Add(item);
                foreach (var box in new[] { ItemForegroundBox, ItemBackgroundBox })
                {
                    box.Items.Add(DefaultColorText);
                    foreach (var (name, _) in FontsAndColorsSettings.NamedColors) box.Items.Add(name);
                }
            }
            fontNames[0] = settings.EditorFontName; fontSizes[0] = SizeText(settings.EditorPoints);
            fontNames[1] = settings.GridFontName; fontSizes[1] = SizeText(settings.GridPoints);
            fontNames[2] = settings.TextResultsFontName; fontSizes[2] = SizeText(settings.TextResultsPoints);
            itemDrafts.Clear();
            foreach (var item in FontsAndColorsSettings.EditorItemNames)
                if (settings.EditorItems is not null && settings.EditorItems.ContainsKey(item)) itemDrafts[item] = settings.Item(item);
            fontCategory = -1; shownItem = null;
            FontCategoryBox.SelectedIndex = 0;
            ShowFontCategory(0);
            DisplayItemsBox.SelectedIndex = 0;
            ShowItem(FontsAndColorsSettings.PlainText);
        }
        finally { loadingFonts = false; }
        UpdateFontSample();
    }

    private static string SizeText(double points) => points.ToString("0.##", CultureInfo.CurrentCulture);

    private void ShowFontCategory(int category)
    {
        KeepFontCategory();
        fontCategory = category;
        FontNameBox.Text = fontNames[category];
        FontSizeBox.Text = fontSizes[category];
        var items = category == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisplayItemsLabel.Visibility = DisplayItemsBox.Visibility = ItemColorsPanel.Visibility = items;
    }

    private void KeepFontCategory()
    {
        if (fontCategory < 0) return;
        fontNames[fontCategory] = FontNameBox.Text.Trim();
        fontSizes[fontCategory] = FontSizeBox.Text.Trim();
    }

    private void ShowItem(string item)
    {
        KeepItem();
        shownItem = item;
        var value = itemDrafts.TryGetValue(item, out var chosen) ? chosen : null;
        var (foreground, background, bold) = FontsAndColorsSettings.Supports(item);
        ItemForegroundBox.IsEnabled = foreground; ItemBackgroundBox.IsEnabled = background; ItemBoldBox.IsEnabled = bold;
        ItemForegroundBox.Text = foreground && value?.Foreground is { } f ? FontsAndColorsSettings.ColorName(f) : DefaultColorText;
        ItemBackgroundBox.Text = background && value?.Background is { } b ? FontsAndColorsSettings.ColorName(b) : DefaultColorText;
        ItemBoldBox.IsChecked = bold && value?.Bold == true;
    }

    /// <summary>Stores the shown item; false when a color cannot be read, so the page can say so.</summary>
    private bool KeepItem()
    {
        if (shownItem is null) return true;
        string? foreground = ColorInput(ItemForegroundBox.Text), background = ColorInput(ItemBackgroundBox.Text);
        if (foreground == "" || background == "") return false;
        var (canForeground, canBackground, canBold) = FontsAndColorsSettings.Supports(shownItem);
        var item = new ColorItem(canForeground ? foreground : null, canBackground ? background : null, canBold && ItemBoldBox.IsChecked == true);
        if (item == new ColorItem()) itemDrafts.Remove(shownItem); else itemDrafts[shownItem] = item;
        return true;
    }

    /// <summary>Null for Default (or empty), #RRGGBB for a color, and "" for text that is not a color.</summary>
    private static string? ColorInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), DefaultColorText, StringComparison.OrdinalIgnoreCase)) return null;
        return FontsAndColorsSettings.ParseColor(text) is { } color ? FontsAndColorsSettings.Hex(color) : "";
    }

    private void FontCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingFonts || FontCategoryBox.SelectedIndex < 0) return;
        loadingFonts = true;
        try { ShowFontCategory(FontCategoryBox.SelectedIndex); }
        finally { loadingFonts = false; }
        UpdateFontSample();
    }

    private void DisplayItemChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingFonts || DisplayItemsBox.SelectedItem is not string item) return;
        loadingFonts = true;
        try { ShowItem(item); }
        finally { loadingFonts = false; }
        UpdateFontSample();
    }

    private void FontSampleInputChanged(object sender, RoutedEventArgs e) { if (!loadingFonts) Dispatcher.BeginInvoke(UpdateFontSample); }
    private void ItemColorInputChanged(object sender, RoutedEventArgs e) { if (!loadingFonts) Dispatcher.BeginInvoke(UpdateFontSample); }

    /// <summary>The sample shows the chosen font and size, and for Text Editor the colors of the selected item.</summary>
    internal void UpdateFontSample()
    {
        if (fontCategory < 0) return;
        try { FontSample.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(FontNameBox.Text) ? "Segoe UI" : FontNameBox.Text.Trim()); } catch (ArgumentException) { }
        if (FontsAndColorsSettings.ParseSize(FontSizeBox.Text) is { } size) FontSample.FontSize = FontsAndColorsSettings.Pixels(size);
        var settings = fontCategory == 0 ? DraftSettings() : null;
        string? item = fontCategory == 0 ? shownItem : null;
        var plain = settings?.Item(FontsAndColorsSettings.PlainText) ?? new ColorItem("#000000", "#FFFFFF");
        var shown = item is not null && settings is not null ? settings.Item(item) : plain;
        FontSample.Foreground = FontsAndColorsSettings.Brush(item == FontsAndColorsSettings.SelectedText ? plain.Foreground : shown.Foreground ?? plain.Foreground);
        FontSampleBorder.Background = item == FontsAndColorsSettings.SelectedText ? FontsAndColorsSettings.SelectionBrush(shown.Background) : FontsAndColorsSettings.Brush(plain.Background);
        FontSample.FontWeight = shown.Bold ? FontWeights.Bold : FontWeights.Normal;
    }

    /// <summary>The page as settings, without checking; readable colors only.</summary>
    private FontsAndColorsSettings DraftSettings()
    {
        var drafts = new Dictionary<string, ColorItem>(itemDrafts);
        if (shownItem is not null)
        {
            string? foreground = ColorInput(ItemForegroundBox.Text), background = ColorInput(ItemBackgroundBox.Text);
            drafts[shownItem] = new ColorItem(foreground is "" ? null : foreground, background is "" ? null : background, ItemBoldBox.IsChecked == true);
        }
        return new FontsAndColorsSettings(EditorItems: drafts);
    }

    private void ResetFontCategory(object sender, RoutedEventArgs e) => ResetFonts(allCategories: false);
    private void ResetAllFontCategories(object sender, RoutedEventArgs e) => ResetFonts(allCategories: true);

    internal void ResetFonts(bool allCategories)
    {
        var defaults = new FontsAndColorsSettings();
        int category = Math.Max(0, fontCategory);
        string[] names = [defaults.EditorFontName, defaults.GridFontName, defaults.TextResultsFontName];
        double[] sizes = [defaults.EditorPoints, defaults.GridPoints, defaults.TextResultsPoints];
        loadingFonts = true;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                if (!allCategories && i != category) continue;
                fontNames[i] = names[i]; fontSizes[i] = SizeText(sizes[i]);
                if (i == 0) itemDrafts.Clear();
            }
            fontCategory = -1; shownItem = null;
            ShowFontCategory(category);
            ShowItem(DisplayItemsBox.SelectedItem as string ?? FontsAndColorsSettings.PlainText);
        }
        finally { loadingFonts = false; }
        FontsError.Visibility = Visibility.Collapsed;
        UpdateFontSample();
    }

    /// <summary>The page as settings for OK, or null after pointing at what is wrong. Sizes are 6 to 72 points; a font must be installed.</summary>
    private FontsAndColorsSettings? ReadFontsAndColors()
    {
        KeepFontCategory();
        if (!KeepItem()) return FontsRejected($"Enter a color name from the list or #RRGGBB for {shownItem}.", fontCategory, ItemForegroundBox.IsEnabled && ColorInput(ItemForegroundBox.Text) == "" ? ItemForegroundBox : ItemBackgroundBox);
        var installed = new HashSet<string>(FontNameBox.Items.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var sizes = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (FontsAndColorsSettings.ParseSize(fontSizes[i]) is not { } size)
                return FontsRejected($"Enter a size from {FontsAndColorsSettings.MinimumSize} to {FontsAndColorsSettings.MaximumSize} points for {FontsAndColorsSettings.Categories[i]}.", i, FontSizeBox);
            sizes[i] = size;
            if (!installed.Contains(fontNames[i])) return FontsRejected($"The font \"{fontNames[i]}\" is not installed. Choose a font from the list for {FontsAndColorsSettings.Categories[i]}.", i, FontNameBox);
        }
        var defaults = new FontsAndColorsSettings();
        // Values equal to the defaults are not stored, so a later change of a PGMS default reaches them.
        return new FontsAndColorsSettings(
            Same(fontNames[0], defaults.EditorFontName) ? null : fontNames[0], sizes[0] == defaults.EditorPoints ? 0 : sizes[0],
            itemDrafts.Count == 0 ? null : new Dictionary<string, ColorItem>(itemDrafts),
            Same(fontNames[1], defaults.GridFontName) ? null : fontNames[1], sizes[1] == defaults.GridPoints ? 0 : sizes[1],
            Same(fontNames[2], defaults.TextResultsFontName) ? null : fontNames[2], sizes[2] == defaults.TextResultsPoints ? 0 : sizes[2]);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private FontsAndColorsSettings? FontsRejected(string message, int category, Control wrong)
    {
        FontsAndColorsPage.IsSelected = true;
        if (FontCategoryBox.SelectedIndex != category) FontCategoryBox.SelectedIndex = category;
        FontsError.Text = message; FontsError.Visibility = Visibility.Visible;
        System.Windows.Input.FocusManager.SetFocusedElement(this, wrong);
        wrong.Focus();
        return null;
    }
}
