using System.Text;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;

namespace PGMS.Desktop;

public partial class ReplaceBar : UserControl
{
    private TextEditor? editor;
    private int scopeStart, scopeEnd;
    private bool scopeValid, internalEdit;
    private int RangeStart => SelectionOnly.IsChecked == true ? scopeStart : 0;
    private int RangeEnd => SelectionOnly.IsChecked == true ? scopeEnd : editor!.Document.TextLength;
    public ReplaceBar() => InitializeComponent();

    public void Open(TextEditor target)
    {
        if (editor != target)
        {
            if (editor is not null) editor.TextChanged -= DocumentChanged;
            editor = target; editor.TextChanged += DocumentChanged;
        }
        SelectionOnly.IsChecked = false;
        Visibility = Visibility.Visible;
        Feedback.Text = "Literal text in the current document. Changes do not execute SQL.";
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        { if (IsVisible) { FindText.Focus(); FindText.SelectAll(); } });
    }

    private bool Ready()
    {
        if (editor is null) return false;
        if (SelectionOnly.IsChecked == true && !scopeValid)
        { Feedback.Text = "Select text in the editor, then choose Use current selection."; return false; }
        if (FindText.Text.Length == 0) { Feedback.Text = "Enter text to find."; return false; }
        return true;
    }

    private StringComparison Comparison => MatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private int FindMatch(string text, string needle, int start, int end)
    {
        while (start <= end - needle.Length)
        {
            int found = text.IndexOf(needle, start, end - start, Comparison);
            if (found < 0) return -1;
            if (HasWordBoundaries(text, found, needle.Length)) return found;
            start = found + 1;
        }
        return -1;
    }

    private bool HasWordBoundaries(string text, int start, int length) => WholeWords.IsChecked != true ||
        (!IsWordCharacter(text, start - 1) && !IsWordCharacter(text, start + length));

    private static bool IsWordCharacter(string text, int index)
    {
        if (index < 0 || index >= text.Length) return false;
        if (text[index] is '_' or '$') return true;
        if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1])) index--;
        return CharUnicodeInfo.GetUnicodeCategory(text, index) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation;
    }

    public bool FindNext()
    {
        if (!Ready()) return false;
        string text = editor!.Text;
        int start = Math.Clamp(editor.SelectionStart + editor.SelectionLength, RangeStart, RangeEnd);
        int found = FindMatch(text, FindText.Text, start, RangeEnd);
        bool wrapped = found < 0;
        if (wrapped) found = FindMatch(text, FindText.Text, RangeStart, RangeEnd);
        if (found < 0) { Feedback.Text = "No matching text found."; return false; }
        editor.Select(found, FindText.Text.Length);
        var location = editor.Document.GetLocation(found);
        editor.ScrollTo(location.Line, location.Column);
        Feedback.Text = wrapped ? "Match selected. Search wrapped to the beginning." : "Match selected.";
        return true;
    }

    private void Next(object sender, RoutedEventArgs e) => FindNext();
    private void ReplaceOne(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        if (editor!.IsReadOnly) { Feedback.Text = "The document is read-only."; return; }
        if (editor.SelectionStart < RangeStart || editor.SelectionStart + editor.SelectionLength > RangeEnd || !string.Equals(editor.SelectedText, FindText.Text, Comparison) || !HasWordBoundaries(editor.Text, editor.SelectionStart, editor.SelectionLength))
        { FindNext(); return; }
        if (editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection)
        { Feedback.Text = "Use a continuous selection for Replace."; return; }
        int offset = editor.SelectionStart;
        ApplyEdit(offset, editor.SelectionLength, ReplacementText.Text);
        editor.Select(offset, ReplacementText.Text.Length);
        FindNext();
        Feedback.Text = "Replaced 1 occurrence. " + Feedback.Text;
    }

    private void ReplaceAll(object sender, RoutedEventArgs e)
    {
        if (!Ready()) return;
        if (editor!.IsReadOnly) { Feedback.Text = "The document is read-only."; return; }
        string original = editor.Text;
        string needle = FindText.Text, replacement = ReplacementText.Text;
        var result = new StringBuilder();
        int from = RangeStart, until = RangeEnd;
        int position = from, count = 0;
        const int maxCharacters = 16 * 1024 * 1024;
        while (true)
        {
            int match = FindMatch(original, needle, position, until);
            int end = match < 0 ? until : match;
            if ((long)result.Length + end - position + (match < 0 ? 0 : replacement.Length) > maxCharacters)
            { Feedback.Text = "Replacement exceeds the 16 million character limit. No changes made."; return; }
            result.Append(original, position, end - position);
            if (match < 0) break;
            result.Append(replacement); count++;
            position = match + needle.Length;
        }
        if (count == 0) { Feedback.Text = "No matching text found. No changes made."; return; }
        string updated = result.ToString();
        if (updated == original.Substring(from, until - from)) { Feedback.Text = "Matching text is already identical to the replacement. No changes made."; return; }
        ApplyEdit(from, until - from, updated);
        editor.Select(from, updated.Length);
        Feedback.Text = $"Replaced {count:N0} occurrence(s). Undo restores all changes together.";
    }

    private void ApplyEdit(int offset, int length, string replacement)
    {
        internalEdit = true;
        try { editor!.Document.Replace(offset, length, replacement); }
        finally { internalEdit = false; }
        if (SelectionOnly.IsChecked == true) scopeEnd += replacement.Length - length;
    }

    private void DocumentChanged(object? sender, EventArgs e)
    {
        if (!internalEdit && SelectionOnly.IsChecked == true)
        {
            scopeValid = false;
            Feedback.Text = "Document changed. Select text again and choose Use current selection.";
        }
    }

    private void ScopeChanged(object sender, RoutedEventArgs e)
    {
        UseSelectionButton.IsEnabled = SelectionOnly.IsChecked == true;
        if (SelectionOnly.IsChecked == true) CaptureSelection(sender, e);
        else { scopeValid = false; Feedback.Text = "Scope: current document."; }
    }

    private void CaptureSelection(object sender, RoutedEventArgs e)
    {
        scopeValid = editor is not null && editor.SelectionLength > 0 && editor.TextArea.Selection is not ICSharpCode.AvalonEdit.Editing.RectangleSelection;
        if (!scopeValid) { Feedback.Text = "Select a continuous, nonempty range in the editor first."; return; }
        scopeStart = editor!.SelectionStart; scopeEnd = scopeStart + editor.SelectionLength;
        Feedback.Text = $"Scope captured: {scopeEnd - scopeStart:N0} selected character(s).";
    }

    private void Close(object sender, RoutedEventArgs e) => Hide();
    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        editor?.Focus();
    }
}
