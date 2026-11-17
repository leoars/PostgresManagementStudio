using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-071: the theme colors. M1107 moved the fixed surface colors to resources with the values they had, so the light look is unchanged.</summary>
internal static class ThemeChecks
{
    /// <summary>The colors of the light theme, which are the colors PGMS had before M1107.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["ShellBrush"] = "#FFA8C7D9", ["PanelBrush"] = "#FFF5FAFE", ["Pgms.DialogBackground"] = "#FFF5F5F5", ["Pgms.PaneBackground"] = "#FFE4EFF6", ["Pgms.ToolBarBackground"] = "#FFD5E5EF",
        ["Pgms.ContentBackground"] = "#FFFFFFFF", ["Pgms.ControlBackground"] = "#FFF0F0F0", ["Pgms.InfoBackground"] = "#FFFFF4CE", ["Pgms.ConnectionBarBackground"] = "#FFD5C777",
        ["Pgms.StatusBarBackground"] = "#FF425C6C", ["Pgms.StatusBarText"] = "#FFFFFFFF", ["Pgms.PaneBorder"] = "#FFB5C5D2", ["Pgms.ControlBorder"] = "#FFCCCEDB", ["Pgms.DialogBorder"] = "#FFC8C8C8",
        ["Pgms.GridLines"] = "#FFE3E3E3", ["Pgms.GridLinesAlt"] = "#FFE0E0E0", ["Pgms.PrimaryText"] = "#FF1E1E1E", ["Pgms.SecondaryText"] = "#FF5A5A5A", ["Pgms.LabelText"] = "#FF4B5964",
        ["Pgms.ErrorText"] = "#FFB00020", ["Pgms.ErrorTextStrong"] = "#FFA4262C", ["Pgms.HeadingText"] = "#FF294A60",
        ["Pgms.GridRowBackground"] = "#FFF5F5F5", ["Pgms.GridSelection"] = "#FFA5CFF1", ["Pgms.GridBorder"] = "#FFABADB3", ["Pgms.GridCurrentCell"] = "#FF000000",
        ["Pgms.TreeInactiveSelection"] = "#FFCCE4F7", ["Pgms.EditorInfoBar"] = "#FFF3F6F9", ["Pgms.EditorSplitter"] = "#FFD6E3ED", ["Pgms.QueryStatusBar"] = "#FFFFF6CD",
        ["Pgms.WarningText"] = "#FF814600", ["Pgms.PropertyText"] = "#FF000000", ["Pgms.PropertyDisabledText"] = "#FF8A8A8A", ["Pgms.PropertyLines"] = "#FFE8E8E8",
        ["Pgms.PropertyBorder"] = "#FFA0A0A0", ["Pgms.PropertyCategory"] = "#FFEEF3FA", ["Pgms.PropertyGlyph"] = "#FF444444",
    };

    internal static string ColorOf(object? brush) => brush is SolidColorBrush solid ? solid.Color.ToString() : "?";

    /// <summary>A window with one of each Windows standard control PGMS uses, without styles of their own.</summary>
    private static (Window Window, Dictionary<string, FrameworkElement> Sample) StandardControls()
    {
        var sample = new Dictionary<string, FrameworkElement>
        {
            ["button"] = new Button { Content = "OK", Width = 75, Height = 23 },
            ["text box"] = new TextBox { Text = "text", Width = 120, Height = 23 },
            ["combo box"] = new ComboBox { ItemsSource = new[] { "one", "two" }, SelectedIndex = 0, Width = 120, Height = 23 },
            ["editable combo box"] = new ComboBox { IsEditable = true, Text = "typed", Width = 120, Height = 23 },
            ["check box"] = new CheckBox { Content = "Check", IsChecked = true },
            ["radio button"] = new RadioButton { Content = "Radio", IsChecked = true },
            ["list"] = new ListBox { ItemsSource = new[] { "a", "b", "c" }, Width = 120, Height = 60 },
            ["scroll bar"] = new ScrollBar { Orientation = Orientation.Vertical, Height = 80, Maximum = 10, ViewportSize = 2 },
            ["horizontal scroll bar"] = new ScrollBar { Orientation = Orientation.Horizontal, Width = 120, Maximum = 10, ViewportSize = 2 },
            ["tab control"] = new TabControl { Width = 160, Height = 60, Items = { new TabItem { Header = "Results" }, new TabItem { Header = "Messages" } } },
            ["data grid"] = new DataGrid { ItemsSource = new[] { new { Name = "x", Value = 1 } }, Width = 160, Height = 60 },
            ["menu"] = new Menu { Items = { new MenuItem { Header = "_File", Items = { new MenuItem { Header = "New", InputGestureText = "Ctrl+N" } } } } },
            ["group box"] = new GroupBox { Header = "Group", Content = new TextBlock { Text = "inside" }, Width = 120, Height = 60 },
            ["property grid"] = new DesignerPropertyGrid { Width = 220, Height = 160, ShowDescription = true },
        };
        ((DesignerPropertyGrid)sample["property grid"]).Show([new DesignerProperty("General", "Name", "The name.", () => "orders", _ => { }), new DesignerProperty("General", "Owner", "Read-only.", () => "pgms_test"),
            new DesignerProperty("General", "Kind", "A choice.", () => "table", _ => { }, ["table", "view"])]);
        var panel = new WrapPanel { Margin = new Thickness(4) };
        foreach (var element in sample.Values) { element.Margin = new Thickness(4); panel.Children.Add(element); }
        var toolCombo = new ComboBox { ItemsSource = new[] { "No active query" }, SelectedIndex = 0, Width = 120 };
        var toolText = new TextBox { Text = "0", Width = 60 };
        panel.Children.Add(new ToolBar { Items = { toolCombo, toolText } });
        sample["tool bar combo box"] = toolCombo; sample["tool bar text box"] = toolText;
        var window = new Window { Content = panel, Width = 700, Height = 300, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.SetResourceReference(Control.BackgroundProperty, "Pgms.DialogBackground");
        return (window, sample);
    }

    /// <summary>Average luminance (0–255) of what the element draws, over a dark backdrop so transparent parts count as dark.</summary>
    private static double Luminance(FrameworkElement element)
    {
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        double sum = 0;
        for (int i = 0; i < pixels.Length; i += 4) sum += 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];
        return sum / (width * height);
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var wrong = Light.Where(p => ColorOf(Application.Current.TryFindResource(p.Key)) != p.Value).Select(p => p.Key).ToList();
        check(wrong.Count == 0, "Every theme color starts with the value PGMS used before the themes" + (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""));

        var options = new EnvironmentOptions(EnvironmentOptions.DefaultPath);
        var dialog = new OptionsDialog(options) { ShowInTaskbar = false };
        dialog.Show();
        try
        {
            await Idle();
            var note = (TextBlock)((Panel)dialog.FindName("GeneralPanel")).Children.OfType<TextBlock>().First(t => t.Text.StartsWith("When this is on", StringComparison.Ordinal));
            check(ColorOf(dialog.Background) == "#FFF5F5F5" && ColorOf(note.Foreground) == "#FF5A5A5A", "Options keeps its background and grey notes through the resources");
        }
        finally { dialog.Close(); }

        // M1108: Tools ▸ Theme
        var original = options.Load();
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            var theme = (MenuItem)window.FindName("ThemeMenu");
            var tools = (ItemsControl)theme.Parent;
            check(tools.Items.IndexOf(theme) == tools.Items.IndexOf(window.FindName("ExternalToolsMenu")) + 1 && tools.Items.IndexOf(theme) == tools.Items.IndexOf(window.FindName("ImportExportSettingsMenu")) - 2
                && theme.Items.OfType<MenuItem>().Select(i => (string)i.Tag).SequenceEqual(["Light", "Dark", "System"]), "Tools ▸ Theme sits between External Tools… and Import and Export Settings…, with Light, Dark and Use system setting");
            var query = window.NewQueryTab("SELECT 1; -- note");
            await Idle();
            var dock = (FrameworkElement)window.FindName("DockManager");
            string DockBackground() => ColorOf(dock.Resources[AvalonDock.Themes.VS.Themes.ResourceKeys.Background]);
            check(!Theme.IsDark && DockBackground() == "#FFA8C7D9" && ColorOf(query.Editor.Background) == "#FFFFFFFF", "PGMS starts light, as before");

            window.SetTheme(ThemeChoice.Dark);
            await Idle();
            check(options.Load().Theme == "Dark" && Theme.IsDark && ColorOf(Application.Current.TryFindResource("Pgms.DialogBackground")) == Theme.Dark["Pgms.DialogBackground"].ToString(), "Dark is saved and replaces the theme colors");
            check(DockBackground() == "#FF2D2D30" && ColorOf(query.Editor.Background) == "#FF1E1E1E" && ColorOf(query.Editor.Foreground) == "#FFDCDCDC", "Docking and the editor turn dark at once (editor defaults match the theme)");
            check(ColorOf(window.Foreground) == Theme.Dark["Pgms.PrimaryText"].ToString(), "Window text turns light");
            var darkOptions = new OptionsDialog(options) { ShowInTaskbar = false }; darkOptions.Show(); await Idle();
            check(ColorOf(darkOptions.Background) == "#FF2D2D30" && ColorOf(darkOptions.Foreground) == Theme.Dark["Pgms.PrimaryText"].ToString(), "A dialog opened in the dark theme is dark too");
            darkOptions.Close();
            theme.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, theme));
            check(theme.Items.OfType<MenuItem>().Single(i => i.IsChecked).Tag as string == "Dark", "The menu checks the theme in use");

            Theme.SystemUsesDark = () => false;
            window.SetTheme(ThemeChoice.System);
            check(options.Load().Theme == "System" && !Theme.IsDark && DockBackground() == "#FFA8C7D9", "Use system setting follows a light Windows");
            Theme.SystemUsesDark = () => true;
            window.ApplyTheme();
            check(Theme.IsDark && ColorOf(query.Editor.Background) == "#FF1E1E1E", "and a dark one");

            // M1112: Windows standard controls, the results grid and the tree selection follow the dark theme.
            var (controls, sample) = StandardControls();
            controls.Show();
            try
            {
                await Idle();
                foreach (var (name, element) in sample)
                {
                    double light = Luminance(element);
                    check(light < 110, $"Dark theme: {name} is drawn dark (average luminance {light:0})");
                }
                var app = Application.Current;
                var button = (Button)sample["button"];
                check(button.Template.FindName("Bd", button) is Border, "Dark theme: buttons use the dark template");
                var menu = (Menu)sample["menu"];
                var top = (MenuItem)menu.Items[0];
                check(top.Template == app.FindResource("Pgms.Dark.TopLevelMenuItem") && ((MenuItem)top.Items[0]).Template == app.FindResource("Pgms.Dark.SubmenuItem"),
                    "Dark theme: menu bar items and menu popups use the dark templates");
                check(ColorOf(app.TryFindResource("Pgms.GridRowBackground")) == "#FF252526" && ColorOf(app.TryFindResource("Pgms.GridSelection")) == "#FF264F78"
                    && ColorOf(app.TryFindResource(SystemColors.WindowBrushKey)) == "#FF252526" && ColorOf(app.TryFindResource(SystemColors.ControlTextBrushKey)) == "#FFF1F1F1",
                    "Dark theme: results grid rows and selection, and the Windows system colours, are dark with light text");
                var resultGrid = new DataGrid { Style = (Style)app.FindResource("SsmsResultGrid") };
                controls.Content = resultGrid; await Idle();
                check(ColorOf(resultGrid.RowBackground) == "#FF252526" && ColorOf(resultGrid.BorderBrush) == "#FF3F3F46", "Dark theme: the results grid style reads the theme colours");
            }
            finally { controls.Close(); }

            window.SetTheme(ThemeChoice.Light);
            await Idle();
            var back = Light.Where(p => ColorOf(Application.Current.TryFindResource(p.Key)) != p.Value).Select(p => p.Key).ToList();
            check(options.Load().Theme is null && back.Count == 0 && DockBackground() == "#FFA8C7D9" && ColorOf(query.Editor.Background) == "#FFFFFFFF" && !FontsAndColorsSettings.DarkDefaults,
                "Light brings back exactly the colors PGMS had" + (back.Count > 0 ? ": " + string.Join(", ", back) : ""));
            var (lightControls, lightSample) = StandardControls();
            lightControls.Show();
            try
            {
                await Idle();
                var lightButton = (Button)lightSample["button"];
                check(lightButton.Template.FindName("Bd", lightButton) is null && Application.Current.TryFindResource("Pgms.Dark.TopLevelMenuItem") is null
                    && Luminance(lightSample["text box"]) > 200 && Luminance(lightSample["list"]) > 200 && Luminance(lightSample["property grid"]) > 200,
                    "Light: the standard controls are back to the Windows look");
            }
            finally { lightControls.Close(); }
        }
        finally
        {
            Theme.SystemUsesDark = Theme.ReadSystemDark;
            options.Update(_ => original);
            window.ApplyTheme();
            window.Close();
        }
    }
}
