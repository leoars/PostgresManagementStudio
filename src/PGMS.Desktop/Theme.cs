using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Theme (PGMS-071, M1108, M1123). Stored by name in environment.json; a missing name is Cool Breeze, the look PGMS always had.</summary>
public enum ThemeChoice
{
    CoolBreeze = 0, Dark = 1, System = 2, Light = 3, Bubblegum = 4, CoolSlate = 5, IcyMint = 6, JuicyPlum = 7, MangoParadise = 8, MoonlightGlow = 9,
    MysticalForest = 10, SilkyPink = 11, SpicyRed = 12, SunnyDay = 13,
}

/// <summary>
/// The themes of SSMS 22.11 Tools ▸ Theme. A theme replaces the colour resources of Resources/Shell.xaml (M1107) at application level, so every
/// DynamicResource follows at once; Cool Breeze removes the replacements, which gives back exactly the colours PGMS always had (M1123 found that the
/// blue look PGMS was modelled on is the Cool Breeze theme of SSMS, not Light). Dark-based themes also merge Resources/Dark.xaml (M1112).
/// </summary>
public static class Theme
{
    /// <summary>The four surfaces measured on SSMS 22.11 for each theme (M1123): main window frame, toolbar, tool window body and status bar.</summary>
    public sealed record Spec(ThemeChoice Choice, string Header, bool IsDark, Color Frame, Color ToolBar, Color Panel, Color Status);

    /// <summary>Every theme in the order of the SSMS menu, without "Use system setting". Colours measured on screenshots of SSMS 22.11 (M1123).</summary>
    public static readonly IReadOnlyList<Spec> Specs =
    [
        new(ThemeChoice.Light, "_Light", false, Rgb(0xEE, 0xEE, 0xEE), Rgb(0xF7, 0xF7, 0xF7), Rgb(0xF9, 0xF9, 0xF9), Rgb(0x6C, 0x6C, 0x6C)),
        new(ThemeChoice.Dark, "_Dark", true, Rgb(0x1C, 0x1C, 0x1C), Rgb(0x26, 0x26, 0x26), Rgb(0x28, 0x28, 0x28), Rgb(0x14, 0x14, 0x14)),
        new(ThemeChoice.Bubblegum, "Bubblegum", false, Rgb(0xF9, 0xBC, 0xD3), Rgb(0xFB, 0xD5, 0xE4), Rgb(0xFE, 0xF5, 0xF8), Rgb(0x71, 0x56, 0x60)),
        new(ThemeChoice.CoolBreeze, "Cool Breeze", false, Rgb(0xA8, 0xC7, 0xD9), Rgb(0xD5, 0xE5, 0xEF), Rgb(0xF5, 0xFA, 0xFE), Rgb(0x42, 0x5C, 0x6C)),
        new(ThemeChoice.CoolSlate, "Cool Slate", true, Rgb(0x2B, 0x34, 0x41), Rgb(0x1E, 0x24, 0x2D), Rgb(0x24, 0x27, 0x2B), Rgb(0x1E, 0x24, 0x2D)),
        new(ThemeChoice.IcyMint, "Icy Mint", false, Rgb(0xCA, 0xE0, 0xE4), Rgb(0xDE, 0xEC, 0xEE), Rgb(0xF8, 0xFB, 0xFB), Rgb(0x5C, 0x66, 0x68)),
        new(ThemeChoice.JuicyPlum, "Juicy Plum", true, Rgb(0x45, 0x23, 0x72), Rgb(0x30, 0x18, 0x50), Rgb(0x27, 0x24, 0x2B), Rgb(0x30, 0x18, 0x50)),
        new(ThemeChoice.MangoParadise, "Mango Paradise", false, Rgb(0xF1, 0xCB, 0xA0), Rgb(0xF6, 0xDF, 0xC4), Rgb(0xFD, 0xFA, 0xF6), Rgb(0x6E, 0x5C, 0x49)),
        new(ThemeChoice.MoonlightGlow, "Moonlight Glow", true, Rgb(0x0F, 0x2B, 0x48), Rgb(0x0A, 0x1E, 0x32), Rgb(0x24, 0x27, 0x2B), Rgb(0x0A, 0x1E, 0x32)),
        new(ThemeChoice.MysticalForest, "Mystical Forest", true, Rgb(0x14, 0x3E, 0x1D), Rgb(0x0E, 0x2B, 0x14), Rgb(0x24, 0x2B, 0x26), Rgb(0x0E, 0x2B, 0x14)),
        new(ThemeChoice.SilkyPink, "Silky Pink", false, Rgb(0xF0, 0xE3, 0xE1), Rgb(0xF6, 0xEE, 0xEC), Rgb(0xFB, 0xF8, 0xF8), Rgb(0x6D, 0x67, 0x66)),
        new(ThemeChoice.SpicyRed, "Spicy Red", true, Rgb(0x6A, 0x0D, 0x12), Rgb(0x4A, 0x09, 0x0D), Rgb(0x2B, 0x24, 0x25), Rgb(0x4A, 0x09, 0x0D)),
        new(ThemeChoice.SunnyDay, "Sunny Day", false, Rgb(0xED, 0xE1, 0xBA), Rgb(0xF4, 0xEC, 0xD4), Rgb(0xFC, 0xFB, 0xF6), Rgb(0x6C, 0x66, 0x55)),
    ];

    public static Spec SpecOf(ThemeChoice choice) => Specs.Single(s => s.Choice == choice);

    /// <summary>The colours of Resources/Shell.xaml that carry the tint of Cool Breeze; light themes tint them the same way (see <see cref="Tint"/>).</summary>
    private static readonly IReadOnlyDictionary<string, Color> CoolBreezeTinted = new Dictionary<string, Color>
    {
        ["Pgms.PaneBackground"] = Rgb(0xE4, 0xEF, 0xF6), ["Pgms.PaneBorder"] = Rgb(0xB5, 0xC5, 0xD2), ["Pgms.EditorInfoBar"] = Rgb(0xF3, 0xF6, 0xF9),
        ["Pgms.EditorSplitter"] = Rgb(0xD6, 0xE3, 0xED), ["Pgms.PropertyCategory"] = Rgb(0xEE, 0xF3, 0xFA),
    };

    /// <summary>The dark palette before the measured surfaces of a dark-based theme are laid over it (M1108, M1112).</summary>
    private static readonly IReadOnlyDictionary<string, Color> DarkBase = new Dictionary<string, Color>
    {
        ["ShellBrush"] = Rgb(0x2D, 0x2D, 0x30), ["PanelBrush"] = Rgb(0x25, 0x25, 0x26), ["Pgms.DialogBackground"] = Rgb(0x2D, 0x2D, 0x30), ["Pgms.PaneBackground"] = Rgb(0x25, 0x25, 0x26),
        ["Pgms.ToolBarBackground"] = Rgb(0x2D, 0x2D, 0x30), ["Pgms.ContentBackground"] = Rgb(0x1E, 0x1E, 0x1E), ["Pgms.ControlBackground"] = Rgb(0x33, 0x33, 0x37),
        ["Pgms.InfoBackground"] = Rgb(0x4D, 0x44, 0x20), ["Pgms.ConnectionBarBackground"] = Rgb(0x7A, 0x6A, 0x1F), ["Pgms.StatusBarBackground"] = Rgb(0x00, 0x7A, 0xCC),
        ["Pgms.StatusBarText"] = Rgb(0xFF, 0xFF, 0xFF), ["Pgms.PaneBorder"] = Rgb(0x3F, 0x3F, 0x46), ["Pgms.ControlBorder"] = Rgb(0x43, 0x43, 0x46), ["Pgms.DialogBorder"] = Rgb(0x3F, 0x3F, 0x46),
        ["Pgms.GridLines"] = Rgb(0x3A, 0x3A, 0x3A), ["Pgms.GridLinesAlt"] = Rgb(0x3A, 0x3A, 0x3A), ["Pgms.PrimaryText"] = Rgb(0xF1, 0xF1, 0xF1), ["Pgms.SecondaryText"] = Rgb(0xA0, 0xA0, 0xA0),
        ["Pgms.LabelText"] = Rgb(0xB4, 0xC0, 0xCA), ["Pgms.ErrorText"] = Rgb(0xF4, 0x87, 0x71), ["Pgms.ErrorTextStrong"] = Rgb(0xF1, 0x70, 0x7B), ["Pgms.HeadingText"] = Rgb(0x8F, 0xB8, 0xD8),
        // M1112: results grid and tree selection.
        ["Pgms.GridRowBackground"] = Rgb(0x25, 0x25, 0x26), ["Pgms.GridSelection"] = Rgb(0x26, 0x4F, 0x78), ["Pgms.GridBorder"] = Rgb(0x3F, 0x3F, 0x46),
        ["Pgms.GridCurrentCell"] = Rgb(0xF1, 0xF1, 0xF1), ["Pgms.TreeInactiveSelection"] = Rgb(0x3F, 0x3F, 0x46),
        ["Pgms.EditorInfoBar"] = Rgb(0x2D, 0x2D, 0x30), ["Pgms.EditorSplitter"] = Rgb(0x3F, 0x3F, 0x46), ["Pgms.QueryStatusBar"] = Rgb(0x4D, 0x44, 0x20), ["Pgms.WarningText"] = Rgb(0xE5, 0xB0, 0x6B),
        ["Pgms.PropertyText"] = Rgb(0xF1, 0xF1, 0xF1), ["Pgms.PropertyDisabledText"] = Rgb(0x80, 0x80, 0x80), ["Pgms.PropertyLines"] = Rgb(0x3F, 0x3F, 0x46), ["Pgms.PropertyBorder"] = Rgb(0x43, 0x43, 0x46),
        ["Pgms.PropertyCategory"] = Rgb(0x33, 0x33, 0x37), ["Pgms.PropertyGlyph"] = Rgb(0xC8, 0xC8, 0xC8),
    };

    /// <summary>The colours of the Dark theme (the dark palette with the surfaces measured on SSMS 22.11 Dark, M1123).</summary>
    public static IReadOnlyDictionary<string, Color> Dark => Palette(ThemeChoice.Dark);

    /// <summary>
    /// The colours a theme puts over Resources/Shell.xaml; empty for Cool Breeze. Dark-based themes: the dark palette with the frame, toolbar, tool
    /// window and status bar measured on SSMS. Light-based themes: the same four surfaces, the Cool Breeze tints moved to the theme's frame colour,
    /// and the status bar colour for headings and labels.
    /// </summary>
    public static IReadOnlyDictionary<string, Color> Palette(ThemeChoice choice)
    {
        if (choice == ThemeChoice.CoolBreeze) return new Dictionary<string, Color>();
        var spec = SpecOf(choice);
        Dictionary<string, Color> colors;
        if (spec.IsDark)
        {
            colors = new Dictionary<string, Color>(DarkBase)
            {
                ["ShellBrush"] = spec.Frame, ["Pgms.ToolBarBackground"] = spec.ToolBar, ["PanelBrush"] = spec.Panel, ["Pgms.PaneBackground"] = spec.Panel,
                ["Pgms.DialogBackground"] = spec.Panel, ["Pgms.EditorInfoBar"] = spec.Panel, ["Pgms.StatusBarBackground"] = spec.Status,
            };
        }
        else
        {
            colors = new Dictionary<string, Color>
            {
                ["ShellBrush"] = spec.Frame, ["Pgms.ToolBarBackground"] = spec.ToolBar, ["PanelBrush"] = spec.Panel, ["Pgms.StatusBarBackground"] = spec.Status,
                ["Pgms.LabelText"] = spec.Status, ["Pgms.HeadingText"] = Scale(spec.Status, 0.7),
            };
            foreach (var (key, coolBreeze) in CoolBreezeTinted) colors[key] = Tint(coolBreeze, spec.Frame);
        }
        return colors;
    }

    /// <summary>
    /// Moves a Cool Breeze tint to another frame colour, channel by channel: its distance from white keeps the same proportion to the frame's distance
    /// from white. A Cool Breeze colour on its own frame comes back unchanged.
    /// </summary>
    public static Color Tint(Color coolBreeze, Color frame)
    {
        var anchor = SpecOf(ThemeChoice.CoolBreeze).Frame;
        static byte Channel(byte value, byte anchorValue, byte frameValue) =>
            (byte)Math.Clamp((int)Math.Round(255 - (255 - value) * (double)(255 - frameValue) / Math.Max(1, 255 - anchorValue)), 0, 255);
        return Rgb(Channel(coolBreeze.R, anchor.R, frame.R), Channel(coolBreeze.G, anchor.G, frame.G), Channel(coolBreeze.B, anchor.B, frame.B));
    }

    private static Color Scale(Color color, double factor) => Rgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Whether the theme in use is dark-based (for "Use system setting", what Windows says for apps).</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The theme in use; for "Use system setting", Cool Breeze or Dark as Windows says.</summary>
    public static ThemeChoice Current { get; private set; } = ThemeChoice.CoolBreeze;

    /// <summary>Replaceable in tests: whether Windows uses the dark mode for apps.</summary>
    public static Func<bool> SystemUsesDark { get; set; } = ReadSystemDark;

    /// <summary>HKCU …\Themes\Personalize AppsUseLightTheme = 0 means dark; a missing value means light, the Windows default.</summary>
    public static bool ReadSystemDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }

    /// <summary>The theme a choice shows: "Use system setting" is Dark or Cool Breeze, the default light look, as Windows says for apps.</summary>
    public static ThemeChoice ResolveTheme(ThemeChoice choice) =>
        choice == ThemeChoice.System ? (SystemUsesDark() ? ThemeChoice.Dark : ThemeChoice.CoolBreeze) : Enum.IsDefined(choice) ? choice : ThemeChoice.CoolBreeze;

    public static bool Resolve(ThemeChoice choice) => ResolveTheme(choice) is var theme && theme != ThemeChoice.CoolBreeze && SpecOf(theme).IsDark;

    /// <summary>Applies the colours to the application resources; true when the theme is dark-based. Editor colours left at their defaults follow
    /// (Fonts and Colors defaults for the dark theme), as Editor appearance "Match Theme" does in SSMS.</summary>
    public static bool Apply(ThemeChoice choice)
    {
        Current = ResolveTheme(choice);
        IsDark = Resolve(choice);
        if (Application.Current is { } app)
        {
            var palette = Palette(Current);
            foreach (var key in DarkBase.Keys.Concat(Palette(ThemeChoice.Light).Keys).Distinct())
            {
                if (palette.TryGetValue(key, out var color)) app.Resources[key] = Frozen(color);
                else app.Resources.Remove(key);
            }
            var merged = app.Resources.MergedDictionaries;
            if (IsDark && !merged.Contains(darkStyles)) merged.Add(darkStyles);
            if (!IsDark) merged.Remove(darkStyles);
            if (IsDark && !windowTextHooked)
            {
                // Windows take their text color from the theme; Window subclasses ignore implicit Window styles, so a class handler sets it.
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ((Window)sender).SetResourceReference(Control.ForegroundProperty, WindowText)));
                windowTextHooked = true;
            }
            if (windowTextHooked) foreach (Window window in app.Windows) window.SetResourceReference(Control.ForegroundProperty, WindowText);
            if (IsDark) app.Resources[WindowText] = Frozen(DarkBase["Pgms.PrimaryText"]); else app.Resources.Remove(WindowText);
        }
        FontsAndColorsSettings.DarkDefaults = IsDark;
        return IsDark;
    }

    /// <summary>The text color of windows: SystemColors.ControlText in the light theme (what windows always had), the primary text when dark.</summary>
    public const string WindowText = "Pgms.WindowText";
    private static bool windowTextHooked;
    private static readonly ResourceDictionary darkStyles = new() { Source = new Uri("pack://application:,,,/PGMS;component/Resources/Dark.xaml") };

    /// <summary>The docking colours PGMS sets on its DockingManager (MainWindow.xaml, Cool Breeze values), for the theme in use.</summary>
    public static void ApplyDocking(FrameworkElement dock, Dictionary<object, Color> lightValues)
    {
        if (lightValues.Count == 0)
            foreach (var key in dock.Resources.Keys) if (dock.Resources[key] is SolidColorBrush brush) lightValues[key] = brush.Color;
        foreach (var (key, light) in lightValues)
            dock.Resources[key] = Frozen(DockColor(light, Current));
        var spec = SpecOf(Current);
        foreach (var (key, role) in DarkTabKeys)
        {
            if (lightValues.ContainsKey(key)) continue;
            if (!spec.IsDark) { dock.Resources.Remove(key); continue; }
            dock.Resources[key] = Frozen(role switch { 'f' => spec.Frame, 'h' => spec.Panel, 'c' => DarkBase["Pgms.ContentBackground"], _ => DarkBase["Pgms.PrimaryText"] });
        }
    }

    /// <summary>B5 (M1134): tab colours the VS2026 blue docking theme draws light, set only in the dark themes (f frame, h hover, c content,
    /// t text). In Dark the unselected document and tool window tabs kept their light blue with light text, unreadable.</summary>
    private static readonly (object Key, char Role)[] DarkTabKeys =
    [
        (AvalonDock.Themes.VS.Themes.ResourceKeys.DocumentWellTabUnselectedBackground, 'f'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.DocumentWellTabUnselectedHoveredBackground, 'h'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabUnselectedBackground, 'f'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabUnselectedText, 't'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabUnselectedHoveredBackground, 'h'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabUnselectedHoveredText, 't'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabSelectedActiveBackground, 'c'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabSelectedActiveText, 't'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabSelectedInactiveBackground, 'c'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.ToolWindowTabSelectedInactiveText, 't'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.AutoHideTabDefaultBackground, 'f'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.AutoHideTabDefaultText, 't'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.AutoHideTabHoveredBackground, 'h'),
        (AvalonDock.Themes.VS.Themes.ResourceKeys.AutoHideTabHoveredText, 't'),
    ];

    /// <summary>A docking colour of MainWindow.xaml in a theme: the frame, the tool window caption, the selected document tab and the text.</summary>
    public static Color DockColor(Color coolBreeze, ThemeChoice theme)
    {
        if (theme == ThemeChoice.CoolBreeze) return coolBreeze;
        var spec = SpecOf(theme);
        var cb = SpecOf(ThemeChoice.CoolBreeze);
        if (coolBreeze == cb.Frame) return spec.Frame;
        if (coolBreeze == cb.Panel) return spec.Panel;
        if (spec.IsDark && coolBreeze == Rgb(0x1E, 0x1E, 0x1E)) return DarkBase["Pgms.PrimaryText"];
        if (coolBreeze == Rgb(0xF7, 0xFA, 0xFC)) return spec.IsDark ? DarkBase["Pgms.ContentBackground"] : Tint(coolBreeze, spec.Frame);
        return coolBreeze;
    }

    private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}

public partial class MainWindow
{
    private readonly Dictionary<object, Color> lightDockColors = [];
    private bool followingSystemTheme;

    /// <summary>Tools ▸ Theme ▸ Light, Dark, Use system setting: the choice is saved and applied at once.</summary>
    private void ThemeMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<ThemeChoice>(tag, out var choice)) SetTheme(choice);
    }

    private void ThemeMenuOpened(object sender, RoutedEventArgs e)
    {
        var current = EnvironmentSettings.Load().ThemeChoice;
        foreach (var item in ThemeMenu.Items.OfType<MenuItem>()) item.IsChecked = (string)item.Tag == current.ToString();
    }

    internal void SetTheme(ThemeChoice choice)
    {
        EnvironmentSettings.Update(d => d with { Theme = choice == ThemeChoice.CoolBreeze ? null : choice.ToString() });
        ApplyTheme();
    }

    /// <summary>Applies the saved theme to the application, the docking colours and the open editors; at startup and after a change.</summary>
    internal void ApplyTheme()
    {
        var choice = EnvironmentSettings.Load().ThemeChoice;
        Theme.Apply(choice);
        Theme.ApplyDocking(DockManager, lightDockColors);
        ApplyFontsAndColors();
        if (choice == ThemeChoice.System && !followingSystemTheme) { SystemEvents.UserPreferenceChanged += SystemThemeChanged; followingSystemTheme = true; }
        else if (choice != ThemeChoice.System && followingSystemTheme) { SystemEvents.UserPreferenceChanged -= SystemThemeChanged; followingSystemTheme = false; }
    }

    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(() => { if (!closingWindow) ApplyTheme(); });
    }

    private void StopFollowingSystemTheme()
    {
        if (followingSystemTheme) { SystemEvents.UserPreferenceChanged -= SystemThemeChanged; followingSystemTheme = false; }
    }
}
