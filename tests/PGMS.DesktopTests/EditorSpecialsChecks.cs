using System.Windows.Controls;
using System.Windows.Input;
using PGMS.Desktop;

/// <summary>PGMS-082 (M1120): Paste Special, Switch Completion Mode and File ▸ New ▸ File….</summary>
internal static class EditorSpecialsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var shell = new MainWindow { ShowInTaskbar = false }; shell.Show(); await shell.RecoveryCompletion;
        try
        {
            var status = (TextBlock)shell.FindName("Status");
            var special = (MenuItem)shell.FindName("PasteSpecialMenu");
            check(special.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(["_Format SQL", "Paste with _AI…"]), "Edit ▸ Paste Special offers Format SQL and Paste with AI");
            var editMenu = (MenuItem)special.Parent;
            var editHeaders = editMenu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            check(editHeaders.IndexOf("Paste _Special") == editHeaders.IndexOf("_Paste") + 1, "Paste Special follows Paste");

            var query = shell.NewQueryTab("SELECT 1;\n");
            query.Editor.CaretOffset = query.Editor.Document.TextLength;
            const string clip = "select a,b from t join u on u.id=t.id where x=1 order by a";
            shell.ReadClipboardText = () => clip;
            check(shell.PasteFormatted(), "Format SQL pastes");
            string expected = SqlFormatter.Format(clip, "\n");
            check(query.Editor.Text == "SELECT 1;\n" + expected && expected.Contains('\n') && expected.Contains("SELECT") && status.Text == "Pasted as formatted SQL.", "The clipboard text arrives laid out by Format SQL:\n" + query.Editor.Text);
            query.Editor.Undo();
            check(query.Editor.Text == "SELECT 1;\n", "One Undo removes the paste");
            query.Editor.Select(0, 6);
            shell.PasteFormatted();
            check(query.Editor.Text == expected + " 1;" + Environment.NewLine.Substring(Environment.NewLine.Length - 1), "The paste replaces the selection");
            shell.ReadClipboardText = () => null;
            string before = query.Editor.Text;
            check(!shell.PasteFormatted() && query.Editor.Text == before && status.Text == "The clipboard has no text to paste.", "An empty clipboard pastes nothing and says so");
            shell.ReadClipboardText = () => "   ";
            shell.PasteWithAi();
            check(status.Text == "The clipboard has no text to paste.", "Paste with AI needs clipboard text");
            shell.ReadClipboardText = () => clip;
            AiQueryDialog? shown = null; shell.AiQueryShown = d => shown = d;
            shell.PasteWithAi();
            check(shown is null && status.Text.StartsWith("Connect this query window", StringComparison.Ordinal), "Paste with AI, like the other AI commands, needs a connected query window");
            check(AiQueryGenerator.ActionRequest(AiSqlAction.Paste).StartsWith("Adapt this SQL, pasted from the clipboard, to this database", StringComparison.Ordinal), "The request asks to adapt the pasted SQL to this database");

            var intelli = (MenuItem)shell.FindName("SwitchCompletionModeMenu");
            check((string)intelli.Header == "Switch _Completion Mode" && intelli.InputGestureText == "Ctrl+Alt+Space" && intelli.Parent == shell.FindName("IntelliSenseEditMenu"), "Edit ▸ IntelliSense ▸ Switch Completion Mode (Ctrl+Alt+Space)");
            check(!QueryView.SuggestionMode, "Completion mode is the default");
            query.SwitchCompletionMode();
            var queryStatus = (TextBlock)query.FindName("QueryStatus");
            check(QueryView.SuggestionMode && queryStatus.Text.StartsWith("IntelliSense suggestion mode"), "Switching turns on suggestion mode and says what Enter and Tab do");
            ((MenuItem)shell.FindName("IntelliSenseEditMenu")).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            check(intelli.IsChecked, "The menu item is checked in suggestion mode");
            var second = shell.NewQueryTab();
            check(QueryView.SuggestionMode, "The mode is shared by every query window");
            second.SwitchCompletionMode();
            ((MenuItem)shell.FindName("IntelliSenseEditMenu")).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
            check(!QueryView.SuggestionMode && !intelli.IsChecked, "Switching again returns to completion mode");

            var newFile = (MenuItem)shell.FindName("NewFileMenu");
            check((string)newFile.Header == "_File…" && newFile.Parent == shell.FindName("NewMenu") && newFile.InputGestureText == "Ctrl+Alt+N" && MainWindow.NewFileCommand.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.N && g.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)), "File ▸ New ▸ File… exists, with Ctrl+Alt+N");
            check(MainWindow.NewFileKinds.Select(k => k.Name).SequenceEqual(["SQL File", "Text File", "SQL File from Template"]), "New File offers an SQL file, a text file and a template");
            shell.ChooseNewFile = () => 1;
            var text = shell.NewFile();
            check(text is not null && text.Editor.SyntaxHighlighting is null && !text.HasSession && status.Text == "New text file.", "Text File opens a window without SQL highlighting, not connected");
            shell.ChooseNewFile = () => 0;
            var sql = shell.NewFile();
            check(sql is not null && sql.Editor.SyntaxHighlighting is not null && !sql.HasSession, "SQL File opens an empty query window, not connected");
            shell.ChooseNewFile = () => null;
            check(shell.NewFile() is null, "Cancel opens nothing");
            shell.ChooseNewFile = () => 2;
            check(shell.NewFile() is null && shell.TemplateExplorer.IsVisible, "SQL File from Template opens Template Explorer");
        }
        finally { QueryView.SuggestionMode = false; foreach (var tab in shell.OpenQueryViews()) tab.IsDirty = false; shell.Close(); await shell.CloseCompletion; }
    }
}
