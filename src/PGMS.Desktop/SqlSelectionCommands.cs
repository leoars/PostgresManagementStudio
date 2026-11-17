using ICSharpCode.AvalonEdit;

namespace PGMS.Desktop;

/// <summary>Selection and caret commands of Edit ▸ Advanced (PGMS-078, M1103), with the SSMS 22.11 shortcuts: Expand Selection (Shift+Alt+=),
/// Expand Selection to Containing Block (Shift+Alt+]), Expand Selection to Line (Shift+Alt+E) and Next/Previous subword (Ctrl+Alt+Right/Left).
/// Strings, quoted identifiers, dollar quotes and comments are skipped when parentheses and statements are matched.</summary>
internal static class SqlSelectionCommands
{
    internal readonly record struct Span(int Start, int End)
    {
        public int Length => End - Start;
        public bool Covers(int start, int end) => Start <= start && End >= end;
    }

    /// <summary>Parentheses pairs, quoted spans and statement separators of <paramref name="text"/>.</summary>
    internal static (List<Span> Parentheses, List<Span> Quoted, List<int> Semicolons) Scan(string text)
    {
        var parens = new List<Span>(); var quoted = new List<Span>(); var semicolons = new List<int>(); var open = new Stack<int>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int depth = 1; i += 2;
                while (i < text.Length && depth > 0) { if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { depth++; i += 2; } else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { depth--; i += 2; } else i++; }
                i--; continue;
            }
            if (c is '\'' or '"')
            {
                int start = i++;
                while (i < text.Length) { if (text[i] == c) { if (i + 1 < text.Length && text[i + 1] == c) { i += 2; continue; } break; } i++; }
                quoted.Add(new Span(start, Math.Min(i + 1, text.Length))); continue;
            }
            if (c == '$' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_'))
            {
                int close = text.IndexOf('$', i + 1);
                if (close > i && text.AsSpan(i + 1, close - i - 1).ToString().All(ch => char.IsLetterOrDigit(ch) || ch == '_') && !(close > i + 1 && char.IsDigit(text[i + 1])))
                {
                    string tag = text[i..(close + 1)];
                    int end = text.IndexOf(tag, close + 1, StringComparison.Ordinal);
                    int stop = end < 0 ? text.Length : end + tag.Length;
                    quoted.Add(new Span(i, stop)); i = stop - 1; continue;
                }
            }
            if (c == '(') open.Push(i);
            else if (c == ')' && open.Count > 0) parens.Add(new Span(open.Pop(), i + 1));
            else if (c == ';') semicolons.Add(i);
        }
        return (parens, quoted, semicolons);
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The statement around <paramref name="offset"/>: from after the previous semicolon to the next one (inclusive), blanks trimmed.</summary>
    private static Span Statement(string text, List<int> semicolons, int start, int end)
    {
        int from = 0, to = text.Length;
        foreach (int s in semicolons) { if (s < start) from = s + 1; else if (s >= end - 1 && s >= start) { to = s + 1; break; } }
        while (from < to && char.IsWhiteSpace(text[from])) from++;
        while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
        return new Span(from, to);
    }

    private static string? Select(TextEditor editor, Span? span, string what)
    {
        if (span is not { } s) return "Nothing larger to select.";
        editor.Select(s.Start, s.Length);
        editor.TextArea.Caret.BringCaretToView();
        return "Selected the " + what + ".";
    }

    /// <summary>Expand Selection: word, quoted text, inside the parentheses, the parentheses, the statement, the document — the smallest that is larger than the selection.</summary>
    public static string? ExpandSelection(TextEditor editor)
    {
        string text = editor.Text; int start = editor.SelectionStart, end = start + editor.SelectionLength;
        var (parens, quoted, semicolons) = Scan(text);
        var candidates = new List<(Span Span, string What)>();
        int ws = start, we = end;
        while (ws > 0 && IsWord(text[ws - 1])) ws--;
        while (we < text.Length && IsWord(text[we])) we++;
        if (we > ws && text.AsSpan(start, end - start).ToString().All(IsWord)) candidates.Add((new Span(ws, we), "word"));
        foreach (var q in quoted) { candidates.Add((new Span(Math.Min(q.Start + 1, q.End), Math.Max(q.End - 1, q.Start + 1)), "quoted text")); candidates.Add((q, "quoted text with its quotes")); }
        foreach (var p in parens) { candidates.Add((new Span(p.Start + 1, p.End - 1), "text inside the parentheses")); candidates.Add((p, "parentheses")); }
        candidates.Add((Statement(text, semicolons, start, end), "statement"));
        candidates.Add((new Span(0, text.Length), "whole document"));
        var best = candidates.Where(c => c.Span.Covers(start, end) && c.Span.Length > end - start).OrderBy(c => c.Span.Length).FirstOrDefault();
        return best.What is null ? Select(editor, null, "") : Select(editor, best.Span, best.What);
    }

    /// <summary>Expand Selection to Containing Block: the innermost parentheses around the selection, else its statement.</summary>
    public static string? ExpandToContainingBlock(TextEditor editor)
    {
        string text = editor.Text; int start = editor.SelectionStart, end = start + editor.SelectionLength;
        var (parens, _, semicolons) = Scan(text);
        var block = parens.Where(p => p.Covers(start, end) && p.Length > end - start).OrderBy(p => p.Length).Cast<Span?>().FirstOrDefault();
        if (block is not null) return Select(editor, block, "block in parentheses");
        var statement = Statement(text, semicolons, start, end);
        return Select(editor, statement.Covers(start, end) && statement.Length > end - start ? statement : null, "statement");
    }

    /// <summary>Expand Selection to Line: whole lines from the first to the last selected; when they already are, one more line below.</summary>
    public static string? ExpandToLine(TextEditor editor)
    {
        var document = editor.Document; int start = editor.SelectionStart, end = start + editor.SelectionLength;
        var first = document.GetLineByOffset(start);
        // A selection that ends right at the start of a line does not include that line.
        var last = document.GetLineByOffset(end > start && end == document.GetLineByOffset(end).Offset ? end - 1 : end);
        int from = first.Offset, to = last.EndOffset + last.DelimiterLength;
        if (from == start && to == end && last.NextLine is { } next) to = next.EndOffset + next.DelimiterLength;
        editor.Select(from, to - from);
        return $"Selected {document.GetLineByOffset(Math.Max(from, to - 1)).LineNumber - first.LineNumber + 1:N0} line(s).";
    }

    private enum CharClass { Upper, Lower, Digit, Separator, Other }
    private static CharClass Classify(char c) => char.IsUpper(c) ? CharClass.Upper : char.IsLetter(c) ? CharClass.Lower : char.IsDigit(c) ? CharClass.Digit : c is '_' || char.IsWhiteSpace(c) ? CharClass.Separator : CharClass.Other;

    /// <summary>The next subword boundary: inside order_id, OrderId or item2, each part is a stop; underscores and blanks are skipped.</summary>
    internal static int NextSubword(string text, int offset)
    {
        int i = offset;
        if (i >= text.Length) return text.Length;
        while (i < text.Length && Classify(text[i]) == CharClass.Separator) i++;
        if (i >= text.Length) return i;
        var kind = Classify(text[i]);
        if (kind == CharClass.Other) return i + 1;
        if (kind == CharClass.Upper)
        {
            int j = i; while (j < text.Length && Classify(text[j]) == CharClass.Upper) j++;
            if (j < text.Length && Classify(text[j]) == CharClass.Lower) { if (j - i > 1) return j - 1; i = j; kind = CharClass.Lower; }
            else return j;
        }
        while (i < text.Length && Classify(text[i]) == kind) i++;
        return i;
    }

    internal static int PreviousSubword(string text, int offset)
    {
        int i = offset;
        while (i > 0 && Classify(text[i - 1]) == CharClass.Separator) i--;
        if (i == 0) return 0;
        var kind = Classify(text[i - 1]);
        if (kind == CharClass.Other) return i - 1;
        if (kind == CharClass.Lower)
        {
            while (i > 0 && Classify(text[i - 1]) == CharClass.Lower) i--;
            if (i > 0 && Classify(text[i - 1]) == CharClass.Upper) i--;
            return i;
        }
        if (kind == CharClass.Upper)
        {
            int j = i; while (j > 0 && Classify(text[j - 1]) == CharClass.Upper) j--;
            return j;
        }
        while (i > 0 && Classify(text[i - 1]) == kind) i--;
        return i;
    }

    /// <summary>Moves the caret to the next or previous subword; with <paramref name="extend"/> the selection grows from its anchor.</summary>
    public static string? MoveSubword(TextEditor editor, bool next, bool extend)
    {
        int caret = editor.CaretOffset;
        int target = next ? NextSubword(editor.Text, caret) : PreviousSubword(editor.Text, caret);
        if (extend)
        {
            int anchor = editor.SelectionLength == 0 ? caret : editor.SelectionStart == caret ? editor.SelectionStart + editor.SelectionLength : editor.SelectionStart;
            editor.Select(Math.Min(anchor, target), Math.Abs(target - anchor));
        }
        else editor.Select(target, 0);
        editor.CaretOffset = target;
        editor.TextArea.Caret.BringCaretToView();
        return null;
    }
}
