using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

public enum FileSearchScope { Folder, AllOpenDocuments, CurrentDocument }

/// <summary>A Find All or Replace All request from the Find and Replace window.</summary>
public sealed record FileSearchRequest(FileSearchOptions Options, FileSearchScope Scope, string? Folder = null, string? Types = "*.sql", bool Subfolders = true);

/// <summary>Edit ▸ Find and Replace ▸ Find in Files / Replace in Files (PGMS-066, M1091): modeless window with the two tabs of SSMS 22.11.
/// Find All and Replace All only; results go to the Find Results pane.</summary>
public sealed class FindInFilesWindow : Window
{
    public const string AllOpenDocuments = "All open documents", CurrentDocument = "Current document";
    private readonly TabItem findTab = new() { Header = "Find in Files" }, replaceTab = new() { Header = "Replace in Files" };
    private readonly TextBlock feedback = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button findAll = new() { Content = "Find _All", Padding = new Thickness(14, 3, 14, 3), MinWidth = 100 };
    private readonly Button replaceAll = new() { Content = "Replace _All", Padding = new Thickness(14, 3, 14, 3), MinWidth = 100 };
    public TabControl Tabs { get; } = new() { Margin = new Thickness(0, 0, 0, 8) };
    public ComboBox FindText { get; } = new() { IsEditable = true };
    public ComboBox ReplaceText { get; } = new() { IsEditable = true };
    public CheckBox MatchCase { get; } = Box("Match _case");
    public CheckBox WholeWord { get; } = Box("Match _whole word");
    public CheckBox UseRegex { get; } = Box("Us_e regular expressions");
    public ComboBox LookIn { get; } = new() { IsEditable = true };
    public CheckBox Subfolders { get; } = Box("Include sub-_folders");
    public ComboBox FileTypes { get; } = new() { IsEditable = true, ItemsSource = new[] { "*.sql", "*.sql;*.txt", "*.*" }, Text = "*.sql" };
    /// <summary>Runs Find All; set by the main window.</summary>
    public Func<FileSearchRequest, Task>? FindAllRequested { get; set; }
    /// <summary>Runs Replace All with the replacement text; set by the main window.</summary>
    public Func<FileSearchRequest, string, Task>? ReplaceAllRequested { get; set; }
    /// <summary>Picks a folder for Look in; tests replace it.</summary>
    public Func<string?, string?>? ChooseFolder { get; set; }
    public bool IsReplace => Tabs.SelectedItem == replaceTab;
    public string Feedback => feedback.Text;

    private static CheckBox Box(string text) => new() { Content = text, Margin = new Thickness(0, 2, 0, 2) };

    public FindInFilesWindow()
    {
        Title = "Find and Replace"; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Tabs.Items.Add(findTab); Tabs.Items.Add(replaceTab);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Tabs);
        panel.Children.Add(new Label { Content = "_Find:", Target = FindText, Padding = new Thickness(0, 0, 0, 2) });
        AutomationProperties.SetName(FindText, "Find"); panel.Children.Add(FindText);
        var replaceCaption = new Label { Content = "Re_place:", Target = ReplaceText, Padding = new Thickness(0, 6, 0, 2) };
        AutomationProperties.SetName(ReplaceText, "Replace");
        panel.Children.Add(replaceCaption); panel.Children.Add(ReplaceText);
        var options = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var box in new[] { MatchCase, WholeWord, UseRegex }) options.Children.Add(box);
        panel.Children.Add(options);
        var look = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        look.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); look.ColumnDefinitions.Add(new ColumnDefinition()); look.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lookLabel = new Label { Content = "_Look in:", Target = LookIn, Padding = new Thickness(0, 3, 0, 0) };
        AutomationProperties.SetName(LookIn, "Look in location");
        var choose = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose Search Folders" };
        AutomationProperties.SetName(choose, "Choose Search Folders");
        choose.Click += (_, _) => { string? start = Folder(); if ((ChooseFolder ?? PickFolder)(start) is { } picked) LookIn.Text = picked; };
        Grid.SetColumn(LookIn, 1); Grid.SetColumn(choose, 2);
        look.Children.Add(lookLabel); look.Children.Add(LookIn); look.Children.Add(choose);
        panel.Children.Add(look);
        Subfolders.IsChecked = true; Subfolders.Margin = new Thickness(70, 2, 0, 6);
        panel.Children.Add(Subfolders);
        var types = new Grid();
        types.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); types.ColumnDefinitions.Add(new ColumnDefinition());
        AutomationProperties.SetName(FileTypes, "File types filter");
        Grid.SetColumn(FileTypes, 1);
        types.Children.Add(new Label { Content = "File _types:", Target = FileTypes, Padding = new Thickness(0, 3, 0, 0) }); types.Children.Add(FileTypes);
        panel.Children.Add(types);
        panel.Children.Add(feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(findAll); buttons.Children.Add(replaceAll);
        panel.Children.Add(buttons);
        Content = panel;
        LookIn.Items.Add(AllOpenDocuments); LookIn.Items.Add(CurrentDocument);
        LookIn.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateFolderControls()));
        LookIn.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateFolderControls);
        findAll.Click += async (_, _) => await FindAllAsync();
        replaceAll.Click += async (_, _) => await ReplaceAllAsync();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Tabs.SelectionChanged += (_, e) => { if (e.Source == Tabs) Arrange(); };
        Arrange();
        void Arrange()
        {
            bool replace = IsReplace;
            replaceCaption.Visibility = ReplaceText.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
            findAll.Visibility = replace ? Visibility.Collapsed : Visibility.Visible;
            replaceAll.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
            findAll.IsDefault = !replace; replaceAll.IsDefault = replace;
        }
    }

    /// <summary>The folder typed in Look in, or null for the open-document scopes.</summary>
    private string? Folder() => LookIn.Text is AllOpenDocuments or CurrentDocument || string.IsNullOrWhiteSpace(LookIn.Text) ? null : LookIn.Text.Trim();

    private void UpdateFolderControls()
    {
        bool folder = Folder() is not null;
        Subfolders.IsEnabled = FileTypes.IsEnabled = folder;
    }

    private static string? PickFolder(string? start)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose Search Folder" };
        if (start is not null && System.IO.Directory.Exists(start)) dialog.InitialDirectory = start;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>Selects the tab, puts the text to find and focuses it.</summary>
    public void Prepare(bool replace, string? findText, string? defaultLookIn)
    {
        Tabs.SelectedItem = replace ? replaceTab : findTab;
        if (!string.IsNullOrEmpty(findText)) FindText.Text = findText;
        if (string.IsNullOrWhiteSpace(LookIn.Text) && defaultLookIn is not null) LookIn.Text = defaultLookIn;
        feedback.Text = "";
        Dispatcher.BeginInvoke(() => { FindText.Focus(); if (FindText.Template?.FindName("PART_EditableTextBox", FindText) is TextBox box) box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>The request the controls describe; throws ArgumentException when something is missing or invalid.</summary>
    public FileSearchRequest BuildRequest()
    {
        var options = new FileSearchOptions(FindText.Text ?? "", MatchCase.IsChecked == true, WholeWord.IsChecked == true, UseRegex.IsChecked == true);
        options.ToRegex();
        if (LookIn.Text == AllOpenDocuments) return new(options, FileSearchScope.AllOpenDocuments);
        if (LookIn.Text == CurrentDocument) return new(options, FileSearchScope.CurrentDocument);
        if (Folder() is not { } folder) throw new ArgumentException("Choose where to look.");
        if (!System.IO.Directory.Exists(folder)) throw new ArgumentException($"The folder {folder} does not exist.");
        return new(options, FileSearchScope.Folder, folder, FileTypes.Text, Subfolders.IsChecked == true);
    }

    private static void Remember(ComboBox box)
    {
        string text = box.Text ?? "";
        if (text.Length == 0) return;
        if (box.Items.Contains(text)) box.Items.Remove(text);
        box.Items.Insert(0, text); box.Text = text;
    }

    public async Task<bool> FindAllAsync()
    {
        FileSearchRequest request;
        try { request = BuildRequest(); } catch (ArgumentException ex) { feedback.Text = ex.Message; return false; }
        feedback.Text = ""; Remember(FindText);
        if (FindAllRequested is { } run) { findAll.IsEnabled = false; try { await run(request); } finally { findAll.IsEnabled = true; } }
        return true;
    }

    public async Task<bool> ReplaceAllAsync()
    {
        FileSearchRequest request;
        try { request = BuildRequest(); } catch (ArgumentException ex) { feedback.Text = ex.Message; return false; }
        feedback.Text = ""; Remember(FindText); Remember(ReplaceText);
        if (ReplaceAllRequested is { } run) { replaceAll.IsEnabled = false; try { await run(request, ReplaceText.Text ?? ""); } finally { replaceAll.IsEnabled = true; } }
        return true;
    }
}
