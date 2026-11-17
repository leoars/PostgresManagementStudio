using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>M1114: Search opens with Ctrl+Q and lists the Tools ▸ Options pages and settings next to the commands.</summary>
internal static class SearchSettingsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            int before = Application.Current.Windows.Count;
            FeatureSearchWindow? shown = null;
            window.FeatureSearchShown = w => shown = w;
            check(MainWindow.FeatureSearchCommand.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.Q && g.Modifiers == ModifierKeys.Control), "Search has the SSMS shortcut Ctrl+Q");
            MainWindow.FeatureSearchCommand.Execute(null, window);
            await Idle();
            check(shown is not null, "Ctrl+Q opens Search");
            if (shown is null) return;
            check(Application.Current.Windows.OfType<OptionsDialog>().All(d => d.IsVisible) && Application.Current.Windows.Count == before + 1, "Reading the Options settings leaves no hidden Options window behind");
            shown.Show();
            await Idle();
            shown.SearchBox.Text = "fonts and colors";
            var results = shown.Results.Items.OfType<System.Windows.Controls.ListBoxItem>().Select(i => (FeatureSearchResult)i.Tag).ToList();
            check(results.Any(r => r.Title == "Fonts and Colors" && r.Path == "Tools > Options > Environment > Fonts and Colors" && r.OptionsPage == "Environment > Fonts and Colors"),
                "Search lists Tools ▸ Options ▸ Environment ▸ Fonts and Colors as a page: " + string.Join(" | ", results.Take(4).Select(r => r.Path)));
            shown.SearchBox.Text = "results";
            results = shown.Results.Items.OfType<System.Windows.Controls.ListBoxItem>().Select(i => (FeatureSearchResult)i.Tag).ToList();
            check(results.FirstOrDefault()?.Item is not null && results.Any(r => r.OptionsPage is not null),
                "Commands come first, then the Options settings that match: " + string.Join(" | ", results.Take(4).Select(r => r.Path)));
            shown.Close();

            OptionsDialog? opened = null;
            window.OptionsShown = d => opened = d;
            var setting = FeatureSearch.Settings([new OptionsPageIndex("Environment > Keyboard", ["Press shortcut keys"])])[1];
            check(window.RunFeatureSearchResult(setting) && opened is not null, "Picking a setting opens Tools ▸ Options");
            if (opened is not null)
            {
                opened.Show(); await Idle();
                check(opened.FindName("KeyboardPanel") is UIElement { Visibility: Visibility.Visible }, "on the page of the setting");
                opened.Close();
            }
        }
        finally { window.FeatureSearchShown = null; window.OptionsShown = null; window.Close(); }
    }
}
