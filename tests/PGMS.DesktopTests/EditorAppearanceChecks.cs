using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-071 (M1113): Tools ▸ Editor Appearance and View ▸ Presenter Mode.</summary>
internal static class EditorAppearanceChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var options = new EnvironmentOptions(EnvironmentOptions.DefaultPath);
        var original = options.Load();
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            var menu = (MenuItem)window.FindName("EditorAppearanceMenu");
            var tools = (ItemsControl)menu.Parent;
            check(tools.Items.IndexOf(menu) == tools.Items.IndexOf(window.FindName("ThemeMenu")) + 1 && tools.Items.IndexOf(menu) == tools.Items.IndexOf(window.FindName("ImportExportSettingsMenu")) - 1
                && menu.Items.OfType<MenuItem>().Select(i => (string)i.Tag).SequenceEqual(["MatchTheme", "Light", "Dark", "LightExtraContrast", "DarkExtraContrast"]),
                "Tools ▸ Editor Appearance follows Theme, with Match Theme, Light, Dark, Light Extra Contrast and Dark Extra Contrast");
            var query = window.NewQueryTab("SELECT 1; -- note");
            await Idle();
            string Back() => ThemeChecks.ColorOf(query.Editor.Background);
            string Fore() => ThemeChecks.ColorOf(query.Editor.Foreground);
            check(options.Load().EditorAppearanceChoice == EditorAppearanceChoice.MatchTheme && Back() == "#FFFFFFFF", "Match Theme by default: the light editor in the light theme");

            window.SetEditorAppearance(EditorAppearanceChoice.Dark);
            await Idle();
            check(options.Load().EditorAppearance == "Dark" && Back() == "#FF1E1E1E" && Fore() == "#FFDCDCDC" && !Theme.IsDark, "Dark: the editor is dark while the theme stays light");
            window.SetEditorAppearance(EditorAppearanceChoice.DarkExtraContrast);
            await Idle();
            check(Back() == "#FF000000" && Fore() == "#FFFFFFFF" && FontsAndColorsSettings.Current.Item(FontsAndColorsSettings.Keyword).Foreground == "#7FC8FF", "Dark Extra Contrast: white on black, brighter keywords");
            window.SetEditorAppearance(EditorAppearanceChoice.LightExtraContrast);
            await Idle();
            check(Back() == "#FFFFFFFF" && FontsAndColorsSettings.Current.Item(FontsAndColorsSettings.Keyword).Foreground == "#0000A0" && FontsAndColorsSettings.Current.Item(FontsAndColorsSettings.Comment).Foreground == "#005000",
                "Light Extra Contrast: darker keywords and comments");
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
            check(menu.Items.OfType<MenuItem>().Single(i => i.IsChecked).Tag as string == "LightExtraContrast", "The menu checks the appearance in use");

            options.Update(d => d with { FontsAndColors = d.Appearance with { EditorItems = new Dictionary<string, ColorItem> { [FontsAndColorsSettings.PlainText] = new(null, "#FFFFE0") } } });
            window.SetEditorAppearance(EditorAppearanceChoice.Dark);
            await Idle();
            check(Back() == "#FFFFFFE0" && Fore() == "#FFDCDCDC", "A color chosen in Fonts and Colors wins over the appearance; the others follow it");
            options.Update(d => d with { FontsAndColors = original.FontsAndColors });

            window.SetTheme(ThemeChoice.Dark);
            window.SetEditorAppearance(EditorAppearanceChoice.Light);
            await Idle();
            check(Theme.IsDark && Back() == "#FFFFFFFF", "Light: a light editor inside the dark theme");
            window.SetEditorAppearance(EditorAppearanceChoice.MatchTheme);
            await Idle();
            check(options.Load().EditorAppearance is null && Back() == "#FF1E1E1E", "Match Theme follows the dark theme again and is stored as the default");
            window.SetTheme(ThemeChoice.Light);
            await Idle();
            check(Back() == "#FFFFFFFF", "and the light one");

            // View ▸ Presenter Mode
            var presenter = (MenuItem)window.FindName("PresenterModeMenu");
            var view = (ItemsControl)presenter.Parent;
            var toggle = (MenuItem)window.FindName("TogglePresenterModeMenu");
            check(view.Items.IndexOf(presenter) == view.Items.IndexOf(window.FindName("ToolbarsMenu")) - 1 && presenter.Items.OfType<MenuItem>().Select(i => i.Header as string)
                .SequenceEqual(["Toggle _Presenter Mode", "_Edit Presenter Mode Settings", "_Restore Default Font Settings"]) && toggle.InputGestureText == "Ctrl+Shift+F5"
                && MainWindow.PresenterModeCommand.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.F5 && g.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)),
                "View ▸ Presenter Mode comes before Toolbars, with Toggle (Ctrl+Shift+F5), Edit Presenter Mode Settings and Restore Default Font Settings");
            double editorBefore = query.Editor.FontSize, windowBefore = window.FontSize;
            MainWindow.PresenterModeCommand.Execute(null, window);
            await Idle();
            check(window.PresenterModeOn && Math.Abs(query.Editor.FontSize - FontsAndColorsSettings.Pixels(16)) < 0.01 && FontsAndColorsSettings.Current.GridPoints == 14
                && Math.Abs(window.FontSize - FontsAndColorsSettings.Pixels(12)) < 0.01 && options.Load().FontsAndColors == original.FontsAndColors,
                $"Presenter Mode enlarges the editor (16 pt), results (14 pt) and window text (12 pt) without changing Fonts and Colors: {query.Editor.FontSize:0.##}, {window.FontSize:0.##}");
            presenter.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, presenter));
            check(toggle.IsChecked, "The menu shows Presenter Mode on");
            MainWindow.PresenterModeCommand.Execute(null, window);
            await Idle();
            check(!window.PresenterModeOn && Math.Abs(query.Editor.FontSize - editorBefore) < 0.01 && Math.Abs(window.FontSize - windowBefore) < 0.01, "Toggling again gives back the sizes of before");

            var dialog = new PresenterModeDialog(new PresenterModeSettings()) { ShowInTaskbar = false };
            dialog.Show();
            try
            {
                dialog.SetSizes("abc", "14", "14", "12");
                check(!dialog.Accept(), "Edit Presenter Mode Settings refuses a size that is not a number");
                dialog.SetSizes("20", "15", "15", "13");
                check(dialog.Accept() && dialog.Result == new PresenterModeSettings(20, 15, 15, 13), "and keeps four valid sizes");
            }
            finally { dialog.Close(); }
            options.Update(d => d with { PresenterMode = new PresenterModeSettings(20, 15, 15, 13) });
            window.SetPresenterMode(true);
            await Idle();
            check(Math.Abs(query.Editor.FontSize - FontsAndColorsSettings.Pixels(20)) < 0.01 && Math.Abs(window.FontSize - FontsAndColorsSettings.Pixels(13)) < 0.01, "Presenter Mode uses the saved sizes");
            window.SetPresenterMode(false);

            options.Update(d => d with { FontsAndColors = new FontsAndColorsSettings("Courier New", 12, new Dictionary<string, ColorItem> { [FontsAndColorsSettings.Keyword] = new("#800080") }, "Arial", 11, "Courier New", 11) });
            window.RestoreDefaultFontSettings();
            await Idle();
            var restored = options.Load().Appearance;
            check(restored.EditorFont is null && restored.EditorSize == 0 && restored.GridFont is null && restored.GridSize == 0 && restored.TextResultsFont is null && restored.TextResultsSize == 0
                && restored.Item(FontsAndColorsSettings.Keyword).Foreground == "#800080" && Math.Abs(query.Editor.FontSize - FontsAndColorsSettings.Pixels(10.5)) < 0.01,
                "Restore Default Font Settings gives back the PGMS fonts and sizes and keeps the chosen colors");
        }
        finally
        {
            options.Update(_ => original);
            if (window.PresenterModeOn) window.SetPresenterMode(false);
            window.ApplyTheme();
            window.Close();
        }
    }
}
