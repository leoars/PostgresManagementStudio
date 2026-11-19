using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;

namespace PGMS.Desktop;

/// <summary>Texts copied or cut in the SQL editors of this PGMS session, newest first (Edit ▸ Show Clipboard History, Ctrl+Shift+V; PGMS-078, M1103).
/// Kept only in memory, at most 20, never written to disk.</summary>
public static class ClipboardHistory
{
    public const int Capacity = 20;
    private static readonly List<string> items = [];
    public static IReadOnlyList<string> Items => items;
    public static void Add(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        items.Remove(text);
        items.Insert(0, text);
        if (items.Count > Capacity) items.RemoveRange(Capacity, items.Count - Capacity);
    }
    public static void Clear() => items.Clear();
    /// <summary>One line of at most 60 characters for the menu.</summary>
    public static string Preview(string text)
    {
        string line = text.Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace('\n', '⏎').Replace('\r', '⏎').Replace('\t', ' ');
        return line.Length <= 60 ? line : line[..57] + "…";
    }
}

/// <summary>Incremental Search, clipboard history and the last edit location (PGMS-078, M1103).</summary>
public partial class QueryView
{
    private static long editClock;
    private bool incrementalActive;
    private int incrementalStart;
    private string incrementalTerm = "";
    private TextDocument? trackedDocument;

    /// <summary>Order of the last edit across all tabs (0 = never edited); Go To Last Edit Location picks the highest.</summary>
    public long LastEditStamp { get; private set; }
    /// <summary>Offset just after the last change made in this tab.</summary>
    public int LastEditOffset { get; private set; }
    public bool IsIncrementalSearchActive => incrementalActive;
    public string IncrementalSearchTerm => incrementalTerm;
    /// <summary>The menu opened by Show Clipboard History, while it is open.</summary>
    public ContextMenu? ClipboardMenu { get; private set; }

    private void ConfigureConveniences()
    {
        CommandManager.AddPreviewExecutedHandler(Editor.TextArea, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut) return;
            // An empty selection copies the whole line in AvalonEdit; keep the same text.
            var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
            ClipboardHistory.Add(Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Document.GetText(line.Offset, line.TotalLength));
        });
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.IncrementalSearch, (_, e) => { StartIncrementalSearch(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.ShowClipboardHistory, (_, e) => { ShowClipboardHistoryMenu(); e.Handled = true; }, (_, e) => { e.CanExecute = !Editor.IsReadOnly; e.Handled = true; }));
        Editor.TextArea.PreviewTextInput += (_, e) =>
        {
            if (!incrementalActive || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            incrementalTerm += e.Text;
            FindIncremental(incrementalStart);
            e.Handled = true;
        };
        Editor.LostKeyboardFocus += (_, _) => { if (incrementalActive && !Editor.IsKeyboardFocusWithin) EndIncrementalSearch(); };
        TrackDocument();
        Editor.DocumentChanged += (_, _) => TrackDocument();
        Editor.TextArea.Caret.PositionChanged += (_, _) => NoticeCaretMove();
    }

    /// <summary>Lines the caret must move in one step, without an edit, for View ▸ Navigate Backward to remember the place (PGMS-082, M1121).</summary>
    internal const int NavigationJumpLines = 10;
    private int caretBefore;
    private object? versionBefore;
    /// <summary>A large caret jump (Go To Line, Find, a click far away, Ctrl+End…): the offset left and the offset reached.</summary>
    public event Action<QueryView, int, int>? CaretJumped;

    private void NoticeCaretMove()
    {
        var document = Editor.Document;
        int now = Editor.CaretOffset;
        var version = document.Version;
        int from = Math.Min(caretBefore, document.TextLength);
        bool edited = !ReferenceEquals(version, versionBefore);
        caretBefore = now; versionBefore = version;
        if (edited || from == now) return;
        if (Math.Abs(document.GetLineByOffset(now).LineNumber - document.GetLineByOffset(from).LineNumber) >= NavigationJumpLines) CaretJumped?.Invoke(this, from, now);
    }

    private void TrackDocument()
    {
        if (trackedDocument is not null) { trackedDocument.Changed -= DocumentChangedForLastEdit; trackedDocument.UpdateFinished -= DocumentUpdateFinished; }
        trackedDocument = Editor.Document;
        if (trackedDocument is not null) { trackedDocument.Changed += DocumentChangedForLastEdit; trackedDocument.UpdateFinished += DocumentUpdateFinished; }
    }

    /// <summary>After an edit the caret is where the edit left it; the next move is measured from there.</summary>
    private void DocumentUpdateFinished(object? sender, EventArgs e) { caretBefore = Editor.CaretOffset; versionBefore = Editor.Document.Version; }

    private void DocumentChangedForLastEdit(object? sender, DocumentChangeEventArgs e)
    {
        LastEditOffset = e.Offset + e.InsertionLength;
        LastEditStamp = ++editClock;
    }

    /// <summary>True when the last change of this tab can still be gone back to: something is in its undo history (a file just opened is not an edit).</summary>
    public bool HasLastEdit => LastEditStamp > 0 && Editor.Document.UndoStack.CanUndo;

    /// <summary>Puts the caret where the last change ended.</summary>
    public void GoToLastEdit()
    {
        int offset = Math.Clamp(LastEditOffset, 0, Editor.Document.TextLength);
        Editor.Select(offset, 0);
        Editor.TextArea.Caret.BringCaretToView();
        Editor.Focus();
    }

    /// <summary>Edit ▸ Advanced ▸ Incremental Search: typed characters search forward from the caret, case-insensitive; Backspace shortens the text,
    /// Ctrl+I or F3 goes to the next match, Enter or Esc stops and keeps the match selected, any other key stops and acts. Ctrl+I outside it is still Generate SQL with AI.</summary>
    public void StartIncrementalSearch()
    {
        incrementalActive = true;
        incrementalStart = Editor.SelectionStart;
        incrementalTerm = "";
        QueryStatus.Text = "Incremental Search: ";
        Editor.TextArea.Focus();
    }

    private void EndIncrementalSearch()
    {
        incrementalActive = false;
        QueryStatus.Text = incrementalTerm.Length == 0 ? "Incremental Search ended." : $"Incremental Search ended: {incrementalTerm}";
    }

    private bool FindIncremental(int from)
    {
        string text = Editor.Text;
        if (incrementalTerm.Length == 0) { Editor.Select(incrementalStart, 0); QueryStatus.Text = "Incremental Search: "; return true; }
        int at = from <= text.Length ? text.IndexOf(incrementalTerm, from, StringComparison.CurrentCultureIgnoreCase) : -1;
        if (at < 0) at = text.IndexOf(incrementalTerm, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0) { QueryStatus.Text = $"Incremental Search: {incrementalTerm} (not found)"; return false; }
        Editor.Select(at, incrementalTerm.Length);
        Editor.TextArea.Caret.BringCaretToView();
        QueryStatus.Text = $"Incremental Search: {incrementalTerm}";
        return true;
    }

    internal bool ProcessIncrementalSearchKey(Key key, ModifierKeys modifiers)
    {
        if (!incrementalActive) return false;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.CapsLock) return false;
        if (key == Key.Back && modifiers == ModifierKeys.None)
        {
            if (incrementalTerm.Length > 0) incrementalTerm = incrementalTerm[..^1];
            FindIncremental(incrementalStart);
            return true;
        }
        if ((key == Key.I && modifiers == ModifierKeys.Control) || (key == Key.F3 && modifiers == ModifierKeys.None))
        {
            if (incrementalTerm.Length > 0) FindIncremental(Editor.SelectionStart + 1);
            return true;
        }
        if (key is Key.Escape or Key.Enter && modifiers == ModifierKeys.None) { EndIncrementalSearch(); return true; }
        // Letters, digits and punctuation arrive as text input; anything else ends the search and does its own work.
        bool typing = modifiers is ModifierKeys.None or ModifierKeys.Shift && key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.Divide or >= Key.Oem1 and <= Key.Oem102 or Key.Space;
        if (!typing) EndIncrementalSearch();
        return false;
    }

    /// <summary>Show Clipboard History: a menu at the caret with what was copied or cut in the editors; choosing one pastes it in place of the selection.</summary>
    public void ShowClipboardHistoryMenu()
    {
        if (Editor.IsReadOnly) return;
        if (ClipboardHistory.Items.Count == 0) { QueryStatus.Text = "Clipboard history is empty: copy or cut text in a SQL editor first."; return; }
        var menu = new ContextMenu { PlacementTarget = Editor.TextArea, Placement = PlacementMode.Relative };
        var caret = Editor.TextArea.Caret.CalculateCaretRectangle();
        menu.HorizontalOffset = caret.Left - Editor.TextArea.TextView.ScrollOffset.X + Editor.TextArea.LeftMargins.Sum(m => m is FrameworkElement f ? f.ActualWidth : 0);
        menu.VerticalOffset = caret.Bottom - Editor.TextArea.TextView.ScrollOffset.Y;
        for (int i = 0; i < ClipboardHistory.Items.Count; i++)
        {
            int index = i;
            var item = new MenuItem { Header = ClipboardHistory.Preview(ClipboardHistory.Items[i]), ToolTip = ClipboardHistory.Items[i].Length > 2000 ? ClipboardHistory.Items[i][..2000] + "…" : ClipboardHistory.Items[i] };
            item.Click += (_, _) => PasteFromHistory(index);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => { if (ReferenceEquals(ClipboardMenu, menu)) ClipboardMenu = null; };
        ClipboardMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Pastes the history entry <paramref name="index"/> (0 = newest) in place of the selection.</summary>
    public void PasteFromHistory(int index)
    {
        if (Editor.IsReadOnly || index < 0 || index >= ClipboardHistory.Items.Count) return;
        string text = ClipboardHistory.Items[index];
        int at = Editor.SelectionStart;
        Editor.Document.Replace(at, Editor.SelectionLength, text);
        Editor.Select(at + text.Length, 0);
        if (ClipboardMenu is { } menu) menu.IsOpen = false;
        UpdateEditorInfo();
        Editor.Focus();
    }
}
