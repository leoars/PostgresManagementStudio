using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace PGMS.Desktop;

/// <summary>Colors and weight of one display item of Fonts and Colors; a null color keeps the PGMS default.</summary>
public sealed record ColorItem(string? Foreground = null, string? Background = null, bool Bold = false);

/// <summary>Tools ▸ Options ▸ Environment ▸ Fonts and Colors (PGMS-071), modelled on SSMS 22.11 as piloted in M1098: a font and a size, in points,
/// for Text Editor, Grid Results and Text Results, and the colors of the editor display items. Zero sizes and null fonts keep the PGMS defaults,
/// which are the looks the editor and results had before the page existed.</summary>
public sealed record FontsAndColorsSettings(string? EditorFont = null, double EditorSize = 0, IReadOnlyDictionary<string, ColorItem>? EditorItems = null,
    string? GridFont = null, double GridSize = 0, string? TextResultsFont = null, double TextResultsSize = 0)
{
    public const string DefaultEditorFont = "Consolas", DefaultGridFont = "Segoe UI", DefaultTextResultsFont = "Consolas";
    /// <summary>14, 12 and 13 device-independent pixels, the sizes PGMS used before.</summary>
    public const double DefaultEditorSize = 10.5, DefaultGridSize = 9, DefaultTextResultsSize = 9.75, MinimumSize = 6, MaximumSize = 72;
    public const string PlainText = "Plain Text", SelectedText = "Selected Text", LineNumber = "Line Number", Comment = "Comment", Keyword = "Keyword", SqlString = "SQL String";

    /// <summary>Display items of Text Editor, in the order of the list. The last three are the named colors of the SQL highlighting.</summary>
    public static readonly IReadOnlyList<string> EditorItemNames = [PlainText, SelectedText, LineNumber, Comment, Keyword, SqlString];
    /// <summary>The "Show settings for" entries.</summary>
    public static readonly IReadOnlyList<string> Categories = ["Text Editor", "Grid Results", "Text Results"];

    /// <summary>Set by the dark theme (M1108): items left at their defaults use the dark editor colors, as SSMS Editor appearance "Match Theme".</summary>
    public static bool DarkDefaults { get; set; }

    /// <summary>Tools ▸ Editor Appearance (M1113); Match Theme follows <see cref="DarkDefaults"/>.</summary>
    public static EditorAppearanceChoice EditorAppearance { get; set; }

    /// <summary>The palette the editor defaults come from: the one chosen, or for Match Theme the one of the theme.</summary>
    public static EditorAppearanceChoice Palette => EditorAppearance != EditorAppearanceChoice.MatchTheme ? EditorAppearance : DarkDefaults ? EditorAppearanceChoice.Dark : EditorAppearanceChoice.Light;

    /// <summary>What an item looks like with nothing chosen.</summary>
    public static ColorItem DefaultItem(string item) => Palette switch
    {
        EditorAppearanceChoice.Dark => DarkItem(item),
        EditorAppearanceChoice.LightExtraContrast => LightContrastItem(item),
        EditorAppearanceChoice.DarkExtraContrast => DarkContrastItem(item),
        _ => LightItem(item),
    };

    /// <summary>The light defaults: the editor colors PGMS always had.</summary>
    public static ColorItem LightItem(string item) => item switch
    {
        PlainText => new("#000000", "#FFFFFF"),
        SelectedText => new(null, "#3399FF"),
        LineNumber => new("#808080"),
        Comment => new("#008000"),
        Keyword => new("#0000FF"),
        SqlString => new("#A31515"),
        _ => new(),
    };

    /// <summary>The defaults of the dark theme, after the Visual Studio dark editor (inferred; SSMS dark was not observed).</summary>
    public static ColorItem DarkItem(string item) => item switch
    {
        PlainText => new("#DCDCDC", "#1E1E1E"),
        SelectedText => new(null, "#264F78"),
        LineNumber => new("#858585"),
        Comment => new("#57A64A"),
        Keyword => new("#569CD6"),
        SqlString => new("#D69D85"),
        _ => new(),
    };

    /// <summary>Light Extra Contrast (M1113, inferred: SSMS was not switched): black on white with darker, bolder-looking colors.</summary>
    public static ColorItem LightContrastItem(string item) => item switch
    {
        PlainText => new("#000000", "#FFFFFF"),
        SelectedText => new(null, "#0050C8"),
        LineNumber => new("#404040"),
        Comment => new("#005000"),
        Keyword => new("#0000A0"),
        SqlString => new("#800000"),
        _ => new(),
    };

    /// <summary>Dark Extra Contrast (M1113, inferred): white on black with brighter colors.</summary>
    public static ColorItem DarkContrastItem(string item) => item switch
    {
        PlainText => new("#FFFFFF", "#000000"),
        SelectedText => new(null, "#1C6BD6"),
        LineNumber => new("#C0C0C0"),
        Comment => new("#7CE07C"),
        Keyword => new("#7FC8FF"),
        SqlString => new("#FFB894"),
        _ => new(),
    };

    /// <summary>Whether the item has a foreground, a background and a bold option on the page (Selected Text only has a background, as in SSMS).</summary>
    public static (bool Foreground, bool Background, bool Bold) Supports(string item) => item switch
    {
        PlainText => (true, true, false),
        SelectedText => (false, true, false),
        LineNumber => (true, false, false),
        _ => (true, false, true),
    };

    /// <summary>The item as chosen over its default: a missing or unreadable color falls back to the default one.</summary>
    public ColorItem Item(string item)
    {
        var fallback = DefaultItem(item);
        if (EditorItems is null || !EditorItems.TryGetValue(item, out var chosen)) return fallback;
        var (foreground, background, bold) = Supports(item);
        return new(foreground && ParseColor(chosen.Foreground) is { } f ? Hex(f) : fallback.Foreground,
            background && ParseColor(chosen.Background) is { } b ? Hex(b) : fallback.Background, bold && chosen.Bold);
    }

    public string EditorFontName => ValidFont(EditorFont) ?? DefaultEditorFont;
    public string GridFontName => ValidFont(GridFont) ?? DefaultGridFont;
    public string TextResultsFontName => ValidFont(TextResultsFont) ?? DefaultTextResultsFont;
    public double EditorPoints => ValidSize(EditorSize) ?? DefaultEditorSize;
    public double GridPoints => ValidSize(GridSize) ?? DefaultGridSize;
    public double TextResultsPoints => ValidSize(TextResultsSize) ?? DefaultTextResultsSize;

    /// <summary>Points to WPF device-independent pixels (1/96 inch).</summary>
    public static double Pixels(double points) => points * 96.0 / 72.0;

    /// <summary>A size typed in the page: a number from 6 to 72 points, with a dot or the culture decimal separator.</summary>
    public static double? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return null;
        return ValidSize(value);
    }

    private static double? ValidSize(double size) => size is >= MinimumSize and <= MaximumSize && !double.IsNaN(size) ? Math.Round(size * 4) / 4 : null;
    private static string? ValidFont(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>The 16 standard colors of the Visual Studio color lists, by their names there.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> NamedColors =
    [
        ("Black", "#000000"), ("White", "#FFFFFF"), ("Maroon", "#800000"), ("Dark Green", "#008000"), ("Olive", "#808000"), ("Navy", "#000080"),
        ("Purple", "#800080"), ("Teal", "#008080"), ("Gray", "#808080"), ("Silver", "#C0C0C0"), ("Red", "#FF0000"), ("Green", "#00FF00"),
        ("Yellow", "#FFFF00"), ("Blue", "#0000FF"), ("Magenta", "#FF00FF"), ("Cyan", "#00FFFF"),
    ];

    /// <summary>A color from the page: a name of <see cref="NamedColors"/> or #RRGGBB; anything else is null.</summary>
    public static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        foreach (var (name, hex) in NamedColors) if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)) trimmed = hex;
        if (trimmed.Length != 7 || trimmed[0] != '#' || !int.TryParse(trimmed.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb)) return null;
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>The name of the color in <see cref="NamedColors"/>, or #RRGGBB.</summary>
    public static string ColorName(string? hex) => ParseColor(hex) is { } color ? NamedColors.FirstOrDefault(c => c.Hex == Hex(color)).Name ?? Hex(color) : "";

    public static string Hex(Color color) => "#" + color.R.ToString("X2", CultureInfo.InvariantCulture) + color.G.ToString("X2", CultureInfo.InvariantCulture) + color.B.ToString("X2", CultureInfo.InvariantCulture);

    public static Brush Brush(string? hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex) ?? Colors.Black);
        brush.Freeze();
        return brush;
    }

    /// <summary>The selection is drawn translucent over the text, as AvalonEdit does by default, so the text stays readable.</summary>
    public static Brush SelectionBrush(string? hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex) ?? Color.FromRgb(0x33, 0x99, 0xFF)) { Opacity = 0.45 };
        brush.Freeze();
        return brush;
    }

    private static readonly object HighlightingLock = new();
    private static (string Key, IHighlightingDefinition Definition)? highlighting;

    /// <summary>The SQL highlighting with the colors of Comment, Keyword and SQL String. Definitions are read-only once loaded, so a change of colors
    /// loads a new one from the embedded XSHD; the same colors give the same definition.</summary>
    public IHighlightingDefinition Highlighting()
    {
        var items = new[] { (Name: "Comment", Item: Item(Comment)), (Name: "Keyword", Item: Item(Keyword)), (Name: "String", Item: Item(SqlString)) };
        string key = string.Join("|", items.Select(i => $"{i.Name}={i.Item.Foreground}/{i.Item.Bold}"));
        lock (HighlightingLock)
        {
            if (highlighting is { } cached && cached.Key == key) return cached.Definition;
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/PGMS;component/Resources/PostgreSQL.xshd")).Stream;
            using var reader = XmlReader.Create(stream);
            var xshd = HighlightingLoader.LoadXshd(reader);
            foreach (var color in xshd.Elements.OfType<XshdColor>())
            {
                if (items.FirstOrDefault(i => i.Name == color.Name).Item is not { } item) continue;
                color.Foreground = new SimpleHighlightingBrush(ParseColor(item.Foreground) ?? Colors.Black);
                color.FontWeight = item.Bold ? FontWeights.Bold : null;
            }
            var definition = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
            highlighting = (key, definition);
            return definition;
        }
    }

    /// <summary>The settings PGMS applies now; the main window sets them at startup and when Options is accepted.</summary>
    public static FontsAndColorsSettings Current { get; set; } = new();
}
