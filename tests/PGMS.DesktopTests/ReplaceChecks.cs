using System.IO;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class ReplaceChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var query = new QueryView();
        var window = new Window { Content = query, Width = 900, Height = 650, ShowInTaskbar = false };
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            var editor = (TextEditor)query.FindName("Editor");
            var bar = (ReplaceBar)query.FindName("ReplacementBar");
            var find = (TextBox)bar.FindName("FindText");
            var replacement = (TextBox)bar.FindName("ReplacementText");
            void Click(string name) => ((Button)bar.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Text = "SELECT alpha, alpha;"; query.IsDirty = false;
            editor.Select(0,0); query.Replace();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(bar.IsVisible && find.IsKeyboardFocused, "M103 Replace opens in the document and focuses Find");
            find.Text = "alpha"; replacement.Text = "beta";
            Click("NextButton");
            check(editor.SelectedText == "alpha" && editor.SelectionStart == 7, "M103 Find Next selects literal text");
            Click("ReplaceButton");
            check(editor.Text == "SELECT beta, alpha;" && query.IsDirty, "M103 Replace changes one selected occurrence and marks document dirty");
            check(editor.SelectedText == "alpha", "M103 replacement selects the next match");
            editor.Undo();
            check(editor.Text == "SELECT alpha, alpha;", "M103 replacement supports native Undo");
            find.Text = "missing"; Click("ReplaceButton");
            check(editor.Text == "SELECT alpha, alpha;" && ((TextBlock)bar.FindName("Feedback")).Text.Contains("No matching"), "M103 missing match leaves document unchanged");
            find.Text = ""; Click("ReplaceButton");
            check(((TextBlock)bar.FindName("Feedback")).Text.Contains("Enter text"), "M103 empty search is rejected");
            editor.Text = "a a a"; editor.Document.UndoStack.ClearAll();
            find.Text = "a"; replacement.Text = "aa"; Click("ReplaceAllButton");
            check(editor.Text == "aa aa aa", "M104 Replace All does not recursively search inserted text");
            editor.Undo();
            check(editor.Text == "a a a" && !editor.CanUndo, "M104 Replace All is a single undo operation");
            editor.Redo(); check(editor.Text == "aa aa aa", "M104 Redo restores all replacements");
            editor.Text = "left--right--"; find.Text = "--"; replacement.Text = ""; Click("ReplaceAllButton");
            check(editor.Text == "leftright", "M104 empty replacement removes matching text");
            editor.Text = "x x"; find.Text = "x"; replacement.Text = "$1\\literal"; Click("ReplaceAllButton");
            check(editor.Text == "$1\\literal $1\\literal", "M104 replacement treats dollar and backslash literally");
            editor.Document.UndoStack.ClearAll(); find.Text = "absent"; Click("ReplaceAllButton");
            check(!editor.CanUndo, "M104 no match creates no undo entry");
            var matchCase = (CheckBox)bar.FindName("MatchCase");
            var wholeWords = (CheckBox)bar.FindName("WholeWords");
            editor.Text = "foo FOO Foo food _foo foo$ foo1";
            find.Text = "foo"; replacement.Text = "bar"; matchCase.IsChecked = false; wholeWords.IsChecked = true;
            Click("ReplaceAllButton");
            check(editor.Text == "bar bar bar food _foo foo$ foo1", "M105 case-insensitive whole words preserve identifier substrings");
            editor.Text = "foo FOO Foo"; matchCase.IsChecked = true; Click("ReplaceAllButton");
            check(editor.Text == "bar FOO Foo", "M105 Match Case restricts replacements");
            editor.Text = "food foo"; editor.Select(0,3); Click("ReplaceButton");
            check(editor.Text == "food foo" && editor.SelectionStart == 5, "M105 selected substring cannot bypass whole-word validation");
            editor.Text = "e\u0301 e e\u0301x"; find.Text = "e"; Click("ReplaceAllButton");
            check(editor.Text == "e\u0301 bar e\u0301x", "M105 combining marks are part of word boundaries");
            wholeWords.IsChecked = false; editor.Text = "aaaa"; find.Text = "aa"; replacement.Text = "b"; Click("ReplaceAllButton");
            check(editor.Text == "bb", "M105 replacements use nonoverlapping original matches");
            var selectionOnly = (CheckBox)bar.FindName("SelectionOnly");
            editor.Text = "x x x x"; editor.Select(2,3); find.Text = "x"; replacement.Text = "long";
            selectionOnly.IsChecked = true; Click("ReplaceAllButton");
            check(editor.Text == "x long long x", "M106 Replace All changes only captured selection");
            editor.Undo(); Click("ReplaceAllButton");
            check(editor.Text == "x x x x" && ((TextBlock)bar.FindName("Feedback")).Text.Contains("Use current selection"), "M106 external edit or Undo invalidates captured offsets");
            editor.Select(2,3); Click("UseSelectionButton"); editor.Select(0,1); Click("ReplaceButton");
            check(editor.Text == "x x x x" && editor.SelectionStart == 2, "M106 matching selection outside scope cannot be replaced");
            Click("ReplaceButton");
            check(editor.Text == "x long x x" && editor.SelectionStart == 7, "M106 individual replacement adjusts captured scope length");
            Click("ReplaceAllButton");
            check(editor.Text == "x long long x", "M106 updated scope excludes trailing document match");
            selectionOnly.IsChecked = false; editor.Select(0,0); selectionOnly.IsChecked = true; Click("ReplaceAllButton");
            check(editor.Text == "x long long x", "M106 empty selection cannot silently replace entire document");
            selectionOnly.IsChecked = false;
            editor.Text = "a\r\nb a\r\nb"; find.Text = "a\r\nb"; replacement.Text = "x\nz"; Click("ReplaceAllButton");
            check(editor.Text == "x\nz x\nz", "M107 multiline replacement preserves literal newline sequences");
            editor.IsReadOnly = true; find.Text = "x"; replacement.Text = "changed"; Click("ReplaceAllButton");
            check(editor.Text == "x\nz x\nz", "M107 read-only editor rejects replacement");
            editor.IsReadOnly = false;
            editor.Text = new string('a',4100); editor.Document.UndoStack.ClearAll(); find.Text = "a"; replacement.Text = new string('b',4096);
            Click("ReplaceAllButton");
            check(editor.Text.Length == 4100 && !editor.CanUndo && ((TextBlock)bar.FindName("Feedback")).Text.Contains("limit"), "M107 oversized expansion is rejected before mutation or Undo entry");
            editor.Text = "SELECT target, target;"; find.Text = "target"; replacement.Text = "updated"; editor.Select(0,0);
            check(query.InputBindings.OfType<KeyBinding>().Any(k => k.Key == Key.H && k.Modifiers == ModifierKeys.Control && k.Command == ApplicationCommands.Replace), "M107 Ctrl H is bound to Replace in query documents");
            ApplicationCommands.Replace.Execute(null, query);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(find.IsKeyboardFocused, "M107 routed Replace command focuses search field");
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(query), 0, Key.F3) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            find.RaiseEvent(key);
            check(key.Handled && editor.SelectedText == "target", "M107 F3 navigates current replacement search");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(query), "C", false);
            check(matchCase.IsChecked == false && bar.IsVisible, "M107 Match Case mnemonic does not collide with Close");
            foreach (var name in new[] { "FindText", "ReplacementText", "NextButton", "ReplaceButton", "ReplaceAllButton", "CloseButton" })
                check(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement((UIElement)bar.FindName(name))?.GetName()), "M107 accessible replacement control " + name);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(query, "m107-replace");
            window.Width = 600; window.Height = 440;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(query, "m107-replace-small");
            var all = (Button)bar.FindName("ReplaceAllButton"); var point = all.TranslatePoint(new Point(), query);
            check(point.X >= 0 && point.X + all.ActualWidth <= query.ActualWidth, "M107 replacement actions fit narrow viewport");
            key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(query), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            find.RaiseEvent(key);
            check(key.Handled && !bar.IsVisible && editor.IsKeyboardFocusWithin, "M107 Escape closes panel and restores editor focus");
            query.Replace(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            query.Find(); check(!bar.IsVisible, "M107 native Find closes replacement panel");
            Click("CloseButton");
            check(!bar.IsVisible && editor.IsKeyboardFocusWithin && !query.HasSession, "M103 closing returns editor focus without connecting or executing SQL");
        }
        finally { window.Close(); await query.DisposeAsync(); }
    }
    private static void Capture(QueryView query, string name)
    {
        query.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(query.ActualWidth), (int)Math.Ceiling(query.ActualHeight), 96,96,PixelFormats.Pbgra32);
        bitmap.Render(query);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/review");
        using var file = File.Create($"artifacts/review/{name}.png"); encoder.Save(file);
    }
}
