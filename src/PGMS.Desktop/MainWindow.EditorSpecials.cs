using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>PGMS-082 (M1120): Edit ▸ Paste Special (Format SQL, Paste with AI), Edit ▸ IntelliSense ▸ Switch Completion Mode and File ▸ New ▸ File….</summary>
public partial class MainWindow
{
    /// <summary>Reads the clipboard text; tests replace it so they never touch the real clipboard.</summary>
    internal Func<string?> ReadClipboardText { get; set; } = () =>
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (COMException) { return null; } // another program holds the clipboard
    };

    /// <summary>Paste Special ▸ Format SQL: pastes the clipboard text laid out by Edit ▸ Format SQL (M1077), replacing the selection. One Undo removes it.</summary>
    internal bool PasteFormatted()
    {
        if (ActiveQuery is not { } query || query.Editor.IsReadOnly) return false;
        if (ReadClipboardText() is not { Length: > 0 } text) { Status.Text = "The clipboard has no text to paste."; return false; }
        string newline = query.Editor.Document.Lines.Count > 1 && query.Editor.Document.GetText(query.Editor.Document.Lines[0].EndOffset, query.Editor.Document.Lines[0].DelimiterLength) is { Length: > 0 } delimiter ? delimiter : Environment.NewLine;
        bool formatted = text.Length <= SqlFormatter.MaximumCharacters;
        string pasted = formatted ? SqlFormatter.Format(text, newline) : text;
        query.Editor.TextArea.Selection.ReplaceSelectionWithText(pasted);
        query.Editor.Focus();
        Status.Text = formatted ? "Pasted as formatted SQL." : $"Pasted without formatting: Format SQL handles up to {SqlFormatter.MaximumCharacters:N0} characters.";
        return true;
    }

    /// <summary>Paste Special ▸ Paste with AI…: the Generate SQL with AI window opens with the clipboard SQL and a request to adapt it to this database.
    /// Nothing is sent until Generate; Insert puts the answer where Paste would.</summary>
    internal void PasteWithAi(string? settingsPath = null)
    {
        if (ActiveQuery is null) return;
        if (ReadClipboardText() is not { } text || string.IsNullOrWhiteSpace(text)) { Status.Text = "The clipboard has no text to paste."; return; }
        OpenAiQuery(AiSqlAction.Paste, settingsPath, text);
    }

    private void PasteFormattedClicked(object sender, RoutedEventArgs e) => PasteFormatted();
    private void PasteWithAiClicked(object sender, RoutedEventArgs e) => PasteWithAi();

    private void SwitchCompletionModeClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveQuery is { } query) query.SwitchCompletionMode();
        else { QueryView.SuggestionMode = !QueryView.SuggestionMode; }
        SwitchCompletionModeMenu.IsChecked = QueryView.SuggestionMode;
    }

    // ---- File ▸ New ▸ File… ----

    /// <summary>The kinds New File offers. Templates stay in Template Explorer, which the dialog can open.</summary>
    internal static readonly IReadOnlyList<(string Name, string Description)> NewFileKinds =
    [
        ("SQL File", "An empty query window, not connected. Connect it with Query ▸ Connection ▸ Connect."),
        ("Text File", "An empty text window without SQL highlighting, not connected, saved as .txt."),
        ("SQL File from Template", "Opens Template Explorer to start from a template."),
    ];

    /// <summary>Tests: receives the kind chosen instead of showing the dialog; null cancels.</summary>
    internal Func<int?>? ChooseNewFile { get; set; }
    private int textFileSequence;

    /// <summary>File ▸ New ▸ File…, Ctrl+Alt+N as in SSMS 22.11 (matrix, M1063).</summary>
    public static RoutedUICommand NewFileCommand { get; } = new("New File", "NewFile", typeof(MainWindow), [new KeyGesture(Key.N, ModifierKeys.Control | ModifierKeys.Alt)]);
    private void ConfigureEditorSpecials()
    {
        CommandBindings.Add(new CommandBinding(NewFileCommand, (_, e) => { NewFile(); e.Handled = true; }));
        IntelliSenseEditMenu.SubmenuOpened += (_, _) => SwitchCompletionModeMenu.IsChecked = QueryView.SuggestionMode;
    }

    internal QueryView? NewFile()
    {
        int? choice = ChooseNewFile is { } choose ? choose() : ShowNewFileDialog();
        switch (choice)
        {
            case 0: { var view = NewQuery(); Status.Text = "New SQL file, not connected."; return view; }
            case 1:
            {
                var view = NewQuery();
                view.Editor.SyntaxHighlighting = null;
                documents[view].Title = $"TextFile{++textFileSequence}.txt";
                Status.Text = "New text file.";
                return view;
            }
            case 2: ShowTemplateExplorer(); return null;
            default: return null;
        }
    }

    private int? ShowNewFileDialog()
    {
        var list = new ListBox { Height = 120, Margin = new Thickness(0, 4, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(list, "Templates");
        foreach (var (name, _) in NewFileKinds) list.Items.Add(name);
        list.SelectedIndex = 0;
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
        description.SetResourceReference(TextBlock.ForegroundProperty, "Pgms.SecondaryText");
        list.SelectionChanged += (_, _) => description.Text = list.SelectedIndex >= 0 ? NewFileKinds[list.SelectedIndex].Description : "";
        description.Text = NewFileKinds[0].Description;
        var open = new Button { Content = "_Open", IsDefault = true, Padding = new Thickness(20, 3, 20, 3) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(open); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Label { Content = "_Templates:", Target = list, Padding = new Thickness(0) });
        panel.Children.Add(list); panel.Children.Add(description); panel.Children.Add(buttons);
        var dialog = new Window { Title = "New File", Content = panel, Owner = this, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(BackgroundProperty, "Pgms.DialogBackground");
        open.Click += (_, _) => dialog.DialogResult = true;
        list.MouseDoubleClick += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { list.Focus(); (list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus(); };
        return dialog.ShowDialog() == true && list.SelectedIndex >= 0 ? list.SelectedIndex : null;
    }
}
