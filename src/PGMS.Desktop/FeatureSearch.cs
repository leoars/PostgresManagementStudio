using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PGMS.Desktop;

/// <summary>A menu command found by Search (PGMS-048, M1111): its text, where it is in the menu ("Edit > Advanced > Format SQL") and its shortcut.
/// Since M1114 also a Tools ▸ Options page or setting: no menu item, and the page path to open.</summary>
public sealed record FeatureSearchResult(string Title, string Path, string Shortcut, MenuItem? Item, string? OptionsPage = null);

/// <summary>
/// Search in the title band, the Feature Search of SSMS 22.11 limited to what PGMS has: the commands of the main menu, by the words of their name
/// and menu path, and since M1114 the Tools ▸ Options pages and settings, as SSMS lists settings next to commands. Code Search is not searched.
/// The command runs as a click on the menu would; a setting opens Options on its page.
/// </summary>
public static class FeatureSearch
{
    /// <summary>A menu header as shown, without access-key markers ("Display E_stimated…" → "Display Estimated…"; "__" stays "_").</summary>
    public static string Text(object? header)
    {
        string raw = header as string ?? (header as TextBlock)?.Text ?? "";
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s?\(_[^)\s]\)", ""); // Japanese and Korean access keys: "ファイル(_F)" (M1129).
        var text = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '_') { text.Append(raw[i]); continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '_') { text.Append('_'); i++; }
        }
        return text.ToString();
    }

    /// <summary>The visible commands of the menu, in menu order (the commands Environment ▸ Keyboard lists, without hidden ones).</summary>
    public static IReadOnlyList<FeatureSearchResult> Commands(ItemsControl menu)
    {
        var result = new List<FeatureSearchResult>();
        foreach (var (_, item) in KeyboardMap.Commands(menu))
        {
            bool hidden = item.Visibility != Visibility.Visible;
            var path = new List<string> { Text(item.Header) };
            for (var parent = ItemsControl.ItemsControlFromItemContainer(item) as MenuItem; parent is not null; parent = ItemsControl.ItemsControlFromItemContainer(parent) as MenuItem)
            {
                path.Insert(0, Text(parent.Header));
                hidden |= parent.Visibility != Visibility.Visible;
            }
            if (!hidden) result.Add(new FeatureSearchResult(path[^1], string.Join(" > ", path), item.InputGestureText ?? "", item));
        }
        return result;
    }

    /// <summary>The Options pages and their settings as results (M1114): "Fonts and Colors" with the path "Tools > Options > Environment > Fonts
    /// and Colors", and each caption of the page under it.</summary>
    public static IReadOnlyList<FeatureSearchResult> Settings(IEnumerable<OptionsPageIndex> pages)
    {
        var result = new List<FeatureSearchResult>();
        foreach (var page in pages)
        {
            string path = FeatureSearch.Text(Localization.T("_Tools")) + " > " + FeatureSearch.Text(Localization.T("_Options…")).TrimEnd('…', '.') + " > " + page.Path;
            result.Add(new(page.Path.Split(" > ")[^1], path, "", null, page.Path));
            foreach (var setting in page.Settings) result.Add(new(setting, path + " > " + setting, "", null, page.Path));
        }
        return result;
    }

    /// <summary>Commands whose path holds every word, ignoring case: names starting with the text first, then names holding every word, then
    /// the rest, each in menu order. Nothing for empty text.</summary>
    public static IReadOnlyList<FeatureSearchResult> Find(IEnumerable<FeatureSearchResult> commands, string text)
    {
        string query = text.Trim();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        int Rank(FeatureSearchResult c) => c.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : words.All(w => c.Title.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 1 : 2;
        return [.. commands.Where(c => words.All(w => c.Path.Contains(w, StringComparison.OrdinalIgnoreCase))).OrderBy(Rank)];
    }
}

/// <summary>The Search window: a text box, the matching commands with their menu path and shortcut, Enter or double click runs, Esc closes.</summary>
public sealed class FeatureSearchWindow : Window
{
    private readonly IReadOnlyList<FeatureSearchResult> commands;
    public TextBox SearchBox { get; }
    public ListBox Results { get; }
    public TextBlock NoResults { get; }
    /// <summary>Raised with the command picked, right before the window closes.</summary>
    public event Action<FeatureSearchResult>? Picked;

    public FeatureSearchWindow(IReadOnlyList<FeatureSearchResult> commands)
    {
        this.commands = commands;
        Title = "Search"; Width = 640; Height = 440; ShowInTaskbar = false; WindowStyle = WindowStyle.ToolWindow; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Pgms.DialogBackground");
        SearchBox = new TextBox { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(2, 3, 2, 3) };
        AutomationProperties.SetName(SearchBox, "Search PGMS commands");
        var hint = new TextBlock { Text = "Menu commands and Tools ▸ Options settings of PGMS. Enter runs the command or opens the Options page; Esc closes.", Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Pgms.SecondaryText");
        Results = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(Results, "Search results");
        ScrollViewer.SetHorizontalScrollBarVisibility(Results, ScrollBarVisibility.Disabled);
        NoResults = new TextBlock { Text = "No results found", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = SystemColors.GrayTextBrush, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var list = new Grid { Children = { Results, NoResults } };
        var panel = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(SearchBox, Dock.Top); DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(SearchBox); panel.Children.Add(hint); panel.Children.Add(list);
        Content = panel;
        SearchBox.TextChanged += (_, _) => Refresh();
        Results.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(Results, d) is ListBoxItem) RunSelected(); };
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { SearchBox.Focus(); Keyboard.Focus(SearchBox); };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Close(); break;
            case Key.Enter: e.Handled = true; RunSelected(); break;
            case Key.Down or Key.Up when SearchBox.IsKeyboardFocusWithin && Results.Items.Count > 0:
                e.Handled = true;
                Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
        }
    }

    /// <summary>Lists the commands matching the text; the first is selected so Enter runs it.</summary>
    public void Refresh()
    {
        Results.Items.Clear();
        foreach (var found in FeatureSearch.Find(commands, SearchBox.Text)) Results.Items.Add(Row(found));
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
        NoResults.Visibility = Results.Items.Count == 0 && SearchBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ListBoxItem Row(FeatureSearchResult found)
    {
        var title = new TextBlock { Text = found.Title, FontWeight = FontWeights.SemiBold };
        var path = new TextBlock { Text = found.Path, Foreground = SystemColors.GrayTextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        var shortcut = new TextBlock { Text = found.Shortcut, Margin = new Thickness(12, 0, 2, 0), VerticalAlignment = VerticalAlignment.Top };
        var row = new DockPanel { Margin = new Thickness(2, 2, 2, 3) };
        DockPanel.SetDock(shortcut, Dock.Right);
        row.Children.Add(shortcut); row.Children.Add(new StackPanel { Children = { title, path } });
        var item = new ListBoxItem { Content = row, Tag = found };
        // As SSMS names its results: command, menu path, shortcut.
        AutomationProperties.SetName(item, found.Shortcut.Length > 0 ? $"{found.Title} {found.Path} {found.Shortcut}" : $"{found.Title} {found.Path}");
        return item;
    }

    /// <summary>Runs the selected command (the first when none is selected): raises Picked and closes. False when there is nothing to run.</summary>
    public bool RunSelected()
    {
        var item = Results.SelectedItem as ListBoxItem ?? Results.Items.OfType<ListBoxItem>().FirstOrDefault();
        if (item?.Tag is not FeatureSearchResult found) return false;
        Picked?.Invoke(found);
        if (IsLoaded) Close();
        return true;
    }
}

/// <summary>Search in the title band (PGMS-048, M1111).</summary>
public partial class MainWindow
{
    /// <summary>Tests: receives the Search window instead of showing it modally.</summary>
    internal Action<FeatureSearchWindow>? FeatureSearchShown { get; set; }

    /// <summary>Ctrl+Q puts the focus in Search, as in SSMS 22.11 (observed in M1114).</summary>
    public static RoutedUICommand FeatureSearchCommand { get; } = new("Search", "FeatureSearch", typeof(MainWindow), [new KeyGesture(Key.Q, ModifierKeys.Control)]);

    private void FeatureSearchClicked(object sender, RoutedEventArgs e) => ShowFeatureSearch();

    /// <summary>The Options pages and settings, read once from a dialog that is never shown.</summary>
    private IReadOnlyList<FeatureSearchResult>? optionsSearch;

    internal void ShowFeatureSearch()
    {
        if (optionsSearch is null)
        {
            var index = new OptionsDialog(EnvironmentSettings, () => Task.FromResult(0), KeyboardCommands());
            Localization.Translate(index); // The index is in the interface language, as the menu commands it lists with (M1129).
            try { optionsSearch = FeatureSearch.Settings(index.SearchIndex()); }
            finally { index.Close(); }
        }
        var window = new FeatureSearchWindow([.. FeatureSearch.Commands(MainMenu), .. optionsSearch]) { Owner = this };
        // After the window is gone, so the command reaches the editor or panel that had the focus.
        window.Picked += found => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => RunFeatureSearchResult(found));
        if (FeatureSearchShown is { } shown) { shown(window); return; }
        window.ShowDialog();
    }

    /// <summary>Tests: receives Tools ▸ Options instead of showing it modally.</summary>
    internal Action<OptionsDialog>? OptionsShown { get; set; }

    /// <summary>Runs a found command as its menu item would, or opens Options on the page of a setting; when a command is not available now,
    /// says so in the status bar.</summary>
    internal bool RunFeatureSearchResult(FeatureSearchResult found)
    {
        if (found.OptionsPage is { } page) { ShowOptions(page); return true; }
        if (found.Item is { } item && KeyboardMap.Invoke(item)) return true;
        Status.Text = $"{found.Path} is not available now.";
        return false;
    }
}
