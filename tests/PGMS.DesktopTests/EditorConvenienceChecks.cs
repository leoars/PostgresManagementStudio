using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>PGMS-078 (M1103): Expand Selection, Containing Block, Expand to Line, subwords, Incremental Search, Clipboard History and Go To Last Edit Location.</summary>
internal static class EditorConvenienceChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var view = new QueryView(); var editor = (TextEditor)view.FindName("Editor");
        void Set(string text, int caret) { editor.Text = text; editor.Document.UndoStack.ClearAll(); editor.Select(caret, 0); }
        string Sel() => editor.SelectedText;

        // Expand Selection grows step by step and skips what is inside strings.
        const string sql = "SELECT count(order_id), 'a (b' FROM sales.orders WHERE (total > (10 + 5));\nSELECT 2;";
        Set(sql, sql.IndexOf("order_id", StringComparison.Ordinal) + 2);
        SqlSelectionCommands.ExpandSelection(editor); check(Sel() == "order_id", "Expand Selection first takes the word: " + Sel());
        SqlSelectionCommands.ExpandSelection(editor); check(Sel() == "(order_id)", "Then the parentheses around it (the inside equals the word): " + Sel());
        SqlSelectionCommands.ExpandSelection(editor); check(Sel() == "SELECT count(order_id), 'a (b' FROM sales.orders WHERE (total > (10 + 5));", "Then the statement, with its semicolon: " + Sel());
        SqlSelectionCommands.ExpandSelection(editor); check(Sel() == sql, "Then the whole document");
        check(SqlSelectionCommands.ExpandSelection(editor) == "Nothing larger to select." && Sel() == sql, "Nothing larger after the document");
        Set(sql, sql.IndexOf("10", StringComparison.Ordinal));
        SqlSelectionCommands.ExpandSelection(editor); SqlSelectionCommands.ExpandSelection(editor);
        check(Sel() == "10 + 5", "Inside nested parentheses, the inside comes before the parentheses: " + Sel());
        SqlSelectionCommands.ExpandSelection(editor); check(Sel() == "(10 + 5)", "Then the inner parentheses: " + Sel());
        Set(sql, sql.IndexOf("'a (b'", StringComparison.Ordinal) + 4);
        SqlSelectionCommands.ExpandSelection(editor); SqlSelectionCommands.ExpandSelection(editor);
        check(Sel() == "a (b", "In a string the quoted text is a step, and its parenthesis is ignored: " + Sel());
        var (parens, _, _) = SqlSelectionCommands.Scan("select $$ ( $$, '(', \"x(\" -- (\n/* ( */ (1)");
        check(parens.Count == 1, "Dollar quotes, strings, quoted names and comments do not count as parentheses: " + parens.Count);

        // Containing block and lines.
        Set(sql, sql.IndexOf("total", StringComparison.Ordinal) + 1);
        SqlSelectionCommands.ExpandToContainingBlock(editor); check(Sel() == "(total > (10 + 5))", "Expand to Containing Block takes the parentheses around the caret: " + Sel());
        SqlSelectionCommands.ExpandToContainingBlock(editor); check(Sel().StartsWith("SELECT count", StringComparison.Ordinal) && Sel().EndsWith(";", StringComparison.Ordinal), "Outside every parenthesis, the statement");
        Set("a\nbb\nccc", 3);
        SqlSelectionCommands.ExpandToLine(editor); check(Sel() == "bb\n", "Expand Selection to Line takes the whole line with its break: " + Sel().Replace("\n", "\\n"));
        SqlSelectionCommands.ExpandToLine(editor); check(Sel() == "bb\nccc", "Again, one more line: " + Sel().Replace("\n", "\\n"));

        // Subwords.
        string words = "select order_id, OrderTotal, HTTPCode, item2 from t";
        int Next(int at) => SqlSelectionCommands.NextSubword(words, at);
        int start = words.IndexOf("order_id", StringComparison.Ordinal);
        check(Next(start) == start + 5 && Next(start + 5) == start + 8, "order_id stops after order and after id");
        int camel = words.IndexOf("OrderTotal", StringComparison.Ordinal);
        check(Next(camel) == camel + 5 && Next(camel + 5) == camel + 10, "OrderTotal stops after Order and Total");
        int acronym = words.IndexOf("HTTPCode", StringComparison.Ordinal);
        check(Next(acronym) == acronym + 4 && Next(acronym + 4) == acronym + 8, "HTTPCode stops after HTTP and Code");
        int item = words.IndexOf("item2", StringComparison.Ordinal);
        check(Next(item) == item + 4 && Next(item + 4) == item + 5, "item2 stops between letters and digits");
        check(SqlSelectionCommands.PreviousSubword(words, camel + 10) == camel + 5 && SqlSelectionCommands.PreviousSubword(words, start + 8) == start + 6, "Previous subword goes back the same way");
        Set(words, camel);
        SqlSelectionCommands.MoveSubword(editor, true, true); SqlSelectionCommands.MoveSubword(editor, true, true);
        check(Sel() == "OrderTotal" && editor.CaretOffset == camel + 10, "With Shift the selection grows by subwords: " + Sel());
        SqlSelectionCommands.MoveSubword(editor, false, false); check(editor.SelectionLength == 0 && editor.CaretOffset == camel + 5, "Without Shift only the caret moves");

        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        try
        {
            await Idle();
            check(((MenuItem)window.FindName("ExpandSelectionMenu")).InputGestureText == "Shift+Alt+=" && ((MenuItem)window.FindName("ExpandToBlockMenu")).InputGestureText == "Shift+Alt+]"
                && ((MenuItem)window.FindName("ExpandToLineMenu")).InputGestureText == "Shift+Alt+E" && ((MenuItem)window.FindName("NextSubwordMenu")).InputGestureText == "Ctrl+Alt+Right Arrow"
                && ((MenuItem)window.FindName("IncrementalSearchMenu")).InputGestureText == "" && ((MenuItem)window.FindName("ClipboardHistoryMenu")).InputGestureText == "Ctrl+Shift+V"
                && ((MenuItem)window.FindName("LastEditLocationMenu")).InputGestureText == "Ctrl+Shift+Backspace", "The commands are in the Edit menus with the SSMS shortcuts (Incremental Search without Ctrl+I)");
            var advanced = (MenuItem)window.FindName("AdvancedEditMenu");
            int Index(string name) => advanced.Items.IndexOf(window.FindName(name));
            check(Index("ExpandToBlockMenu") == Index("DeleteHorizontalWhiteSpaceMenu") + 1 && Index("ExpandSelectionMenu") == Index("ExpandToBlockMenu") + 1 && Index("IncrementalSearchMenu") == Index("ToggleLineCommentMenu") - 1
                && Index("ExpandToLineMenu") == Index("CommentSelectionMenu") - 1, "Edit ▸ Advanced keeps the SSMS order");

            var first = window.NewQueryTab("select alpha, beta from gamma;"); await Idle();
            var firstEditor = (TextEditor)first.FindName("Editor");
            var second = window.NewQueryTab("select 1;"); await Idle();
            var secondEditor = (TextEditor)second.FindName("Editor");
            firstEditor.Document.UndoStack.ClearAll(); secondEditor.Document.UndoStack.ClearAll();
            check(window.GoToLastEditLocation() is null, "With no edit, Go To Last Edit Location has nowhere to go");
            firstEditor.Document.Insert(7, "id, ");
            secondEditor.Text = "select 2;"; secondEditor.Document.UndoStack.ClearAll(); // like a file loaded: not an edit to go back to
            check(second.LastEditStamp > first.LastEditStamp && !second.HasLastEdit, "A change with nothing to undo does not count");
            var back = window.GoToLastEditLocation(); await Idle();
            check(ReferenceEquals(back, first) &&firstEditor.CaretOffset == 11, "Go To Last Edit Location activates the tab changed last and puts the caret after the change: " + firstEditor.CaretOffset);

            // Incremental Search.
            first.StartIncrementalSearch();
            check(first.IsIncrementalSearchActive, "Incremental Search starts");
            firstEditor.Select(0, 0); first.StartIncrementalSearch();
            foreach (char c in "BET") { var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, firstEditor.TextArea, c.ToString())) { RoutedEvent = UIElement.PreviewTextInputEvent }; firstEditor.TextArea.RaiseEvent(args); }
            check(first.IncrementalSearchTerm == "BET" && firstEditor.SelectedText == "bet", "Typed characters search case-insensitively as they come: " + firstEditor.SelectedText);
            check(first.ProcessIncrementalSearchKey(Key.Back, ModifierKeys.None) && firstEditor.SelectedText == "be", "Backspace shortens the text and searches again");
            first.ProcessIncrementalSearchKey(Key.Escape, ModifierKeys.None);
            check(!first.IsIncrementalSearchActive && firstEditor.SelectedText == "be", "Esc stops and keeps the match");
            firstEditor.Select(0, 0); first.StartIncrementalSearch();
            foreach (char c in "a") firstEditor.TextArea.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, firstEditor.TextArea, c.ToString())) { RoutedEvent = UIElement.PreviewTextInputEvent });
            int firstA = firstEditor.SelectionStart;
            first.ProcessIncrementalSearchKey(Key.I, ModifierKeys.Control);
            check(firstEditor.SelectionStart > firstA && firstEditor.SelectedText == "a", "Ctrl+I inside the search goes to the next match");
            check(!first.ProcessIncrementalSearchKey(Key.Right, ModifierKeys.None) && !first.IsIncrementalSearchActive, "Another key stops the search and does its own work");
            check(!firstEditor.TextArea.DefaultInputHandler.Editing.InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.I && b.Modifiers == ModifierKeys.Control)
                && !firstEditor.TextArea.CommandBindings.OfType<CommandBinding>().Any(b => b.Command == ICSharpCode.AvalonEdit.AvalonEditCommands.IndentSelection)
                && window.InputBindings.OfType<KeyBinding>().Any(b => b.Key == Key.I && b.Modifiers == ModifierKeys.Control && b.Command == MainWindow.GenerateSqlWithAi), "Ctrl+I in the editor reaches Generate SQL with AI (AvalonEdit's Indent Selection binding is removed)");

            // Clipboard history.
            ClipboardHistory.Clear();
            secondEditor.Text = "select 1;"; secondEditor.Document.Insert(0, " "); secondEditor.Document.Remove(0, 1);
            check(ReferenceEquals(window.GoToLastEditLocation(), second), "An edit in the other tab moves the last edit there"); await Idle();
            secondEditor.Select(0, 0);
            second.ShowClipboardHistoryMenu();
            check(second.ClipboardMenu is null, "With an empty history no menu opens");
            ClipboardHistory.Add("first copy"); ClipboardHistory.Add("second\ncopy"); ClipboardHistory.Add("first copy");
            check(ClipboardHistory.Items.SequenceEqual(["first copy", "second\ncopy"]) && ClipboardHistory.Preview("second\ncopy") == "second⏎copy", "The history keeps each text once, newest first");
            for (int i = 0; i < 25; i++) ClipboardHistory.Add("text " + i);
            check(ClipboardHistory.Items.Count == ClipboardHistory.Capacity && ClipboardHistory.Items[0] == "text 24", "At most 20 entries are kept");
            second.ShowClipboardHistoryMenu(); await Idle();
            check(second.ClipboardMenu is { IsOpen: true } menu && menu.Items.Count == 20 && (string)((MenuItem)menu.Items[0]).Header == "text 24", "Show Clipboard History opens a menu with the entries");
            ((MenuItem)second.ClipboardMenu!.Items[1]).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent)); await Idle();
            check(secondEditor.Text == "text 23select 1;" && secondEditor.CaretOffset == 7, "Choosing one pastes it at the caret: " + secondEditor.Text);
            // Copy and Cut themselves are not run here: they would replace the real Windows clipboard of the machine.
        }
        finally
        {
            ClipboardHistory.Clear();
            foreach (var q in window.QueryDocuments.ToArray()) q.IsDirty = false;
            window.Close();
        }
    }
}
