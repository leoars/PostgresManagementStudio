using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Theme (PGMS-071, M1108). Stored by name in environment.json.</summary>
public enum ThemeChoice { Light = 0, Dark = 1, System = 2 }

/// <summary>
/// Light, Dark and "Use system setting", the three choices of SSMS 22.11 Tools ▸ Theme that are not colour variants. A theme replaces the colour
/// resources of Resources/Shell.xaml (M1107) at application level, so every DynamicResource follows at once; Light removes the replacements, which
/// gives back exactly the colours PGMS always had. Windows standard controls (text boxes, lists, grids, buttons) keep their own light look.
/// </summary>
public static class Theme
{
    /// <summary>The dark palette, after the Visual Studio dark theme that SSMS 22.11 is built on (inferred: the user's SSMS was not switched).</summary>
    public static readonly IReadOnlyDictionary<string, Color> Dark = new Dictionary<string, Color>
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

    /// <summary>Docking colours of MainWindow.xaml (light value → dark value): the AvalonDock keys PGMS sets itself.</summary>
    private static readonly IReadOnlyDictionary<Color, Color> DockDark = new Dictionary<Color, Color>
    {
        [Rgb(0xA8, 0xC7, 0xD9)] = Rgb(0x2D, 0x2D, 0x30), [Rgb(0x1E, 0x1E, 0x1E)] = Rgb(0xF1, 0xF1, 0xF1), [Rgb(0xF5, 0xFA, 0xFE)] = Rgb(0x25, 0x25, 0x26), [Rgb(0xF7, 0xFA, 0xFC)] = Rgb(0x1E, 0x1E, 0x1E),
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Whether the theme in use is dark (for "Use system setting", what Windows says for apps).</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Replaceable in tests: whether Windows uses the dark mode for apps.</summary>
    public static Func<bool> SystemUsesDark { get; set; } = ReadSystemDark;

    /// <summary>HKCU …\Themes\Personalize AppsUseLightTheme = 0 means dark; a missing value means light, the Windows default.</summary>
    public static bool ReadSystemDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }

    public static bool Resolve(ThemeChoice choice) => choice switch { ThemeChoice.Dark => true, ThemeChoice.System => SystemUsesDark(), _ => false };

    /// <summary>Applies the colours to the application resources; true when the choice is dark. Editor colours left at their defaults follow
    /// (Fonts and Colors defaults for the dark theme), as Editor appearance "Match Theme" does in SSMS.</summary>
    public static bool Apply(ThemeChoice choice)
    {
        IsDark = Resolve(choice);
        if (Application.Current is { } app)
        {
            foreach (var (key, color) in Dark)
            {
                if (IsDark) app.Resources[key] = Frozen(color);
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
            if (IsDark) app.Resources[WindowText] = Frozen(Dark["Pgms.PrimaryText"]); else app.Resources.Remove(WindowText);
        }
        FontsAndColorsSettings.DarkDefaults = IsDark;
        return IsDark;
    }

    /// <summary>The text color of windows: SystemColors.ControlText in the light theme (what windows always had), the primary text when dark.</summary>
    public const string WindowText = "Pgms.WindowText";
    private static bool windowTextHooked;
    private static readonly ResourceDictionary darkStyles = new() { Source = new Uri("pack://application:,,,/PGMS;component/Resources/Dark.xaml") };

    /// <summary>The docking colours PGMS sets on its DockingManager, dark or back to the light values they had.</summary>
    public static void ApplyDocking(FrameworkElement dock, Dictionary<object, Color> lightValues)
    {
        if (lightValues.Count == 0)
            foreach (var key in dock.Resources.Keys) if (dock.Resources[key] is SolidColorBrush brush) lightValues[key] = brush.Color;
        foreach (var (key, light) in lightValues)
            dock.Resources[key] = Frozen(IsDark && DockDark.TryGetValue(light, out var dark) ? dark : light);
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
        EnvironmentSettings.Update(d => d with { Theme = choice == ThemeChoice.Light ? null : choice.ToString() });
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
