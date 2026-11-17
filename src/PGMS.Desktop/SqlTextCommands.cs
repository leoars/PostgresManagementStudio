using System.Globalization;
using System.Text;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace PGMS.Desktop;

/// <summary>Edit ▸ Advanced commands of the SQL editor (PGMS-056), modelled on SSMS 22.11 as piloted in M1075.
/// Each command is one Undo step and returns the status text for the query tab.</summary>
internal static class SqlTextCommands
{
    private static readonly StringComparer SortComparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);

    /// <summary>Lines touched by the selection; a selection ending at column 1 does not include that line (as in SSMS).</summary>
    internal static (int First, int Last) LineRange(TextEditor editor)
    {
        var document = editor.Document;
        int start = editor.SelectionStart, end = start + editor.SelectionLength;
        int first = document.GetLineByOffset(start).LineNumber;
        var endLine = document.GetLineByOffset(end);
        int last = endLine.LineNumber;
        if (editor.SelectionLength > 0 && end == endLine.Offset && last > first) last--;
        if (last - first + 1 > SqlLineComments.MaximumLines) throw new ArgumentException("Select at most 10,000 lines for this command.");
        return (first, last);
    }

    private static void EnsureWritable(TextEditor editor)
    {
        if (editor.IsReadOnly) throw new InvalidOperationException("The SQL editor is read-only.");
    }

    private static bool IsBlank(char c) => c is ' ' or '\t';
    private static int Leading(TextDocument document, DocumentLine line)
    {
        int offset = line.Offset;
        while (offset < line.EndOffset && IsBlank(document.GetCharAt(offset))) offset++;
        return offset - line.Offset;
    }
    private static bool IsBlankLine(TextDocument document, DocumentLine line) => Leading(document, line) == line.Length;

    private static TextAnchor Anchor(TextDocument document, int offset)
    {
        var anchor = document.CreateAnchor(offset); anchor.MovementType = AnchorMovementType.AfterInsertion; anchor.SurviveDeletion = true; return anchor;
    }

    /// <summary>Applies offset/remove/insert changes back to front in one Undo step and keeps the caret, or selects whole lines.</summary>
    private static int ApplyLineChanges(TextEditor editor, int first, int last, List<(int Offset, int Remove, string Insert)> changes)
    {
        if (changes.Count == 0) return 0;
        var document = editor.Document;
        bool selected = editor.SelectionLength > 0;
        var caret = Anchor(document, editor.CaretOffset);
        using (document.RunUpdate())
            foreach (var change in changes.OrderByDescending(c => c.Offset)) document.Replace(change.Offset, change.Remove, change.Insert);
        if (selected)
        {
            int start = document.GetLineByNumber(first).Offset;
            editor.Select(start, document.GetLineByNumber(last).EndOffset - start);
        }
        else { editor.Select(caret.Offset, 0); editor.CaretOffset = caret.Offset; }
        return changes.Count;
    }

    /// <summary>Ctrl+/: comments every line when any non-blank line lacks a leading <c>--</c>; otherwise removes the <c>--</c>.</summary>
    internal static string ToggleLineComment(TextEditor editor)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        var (first, last) = LineRange(editor);
        var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber).ToList();
        var content = lines.Where(l => !IsBlankLine(document, l)).ToList();
        if (content.Count == 0)
        {
            if (first != last) return "The selected lines are blank.";
            content = lines;
        }
        bool commented = content.All(l => { int at = l.Offset + Leading(document, l); return at + 1 < l.EndOffset && document.GetCharAt(at) == '-' && document.GetCharAt(at + 1) == '-'; });
        var changes = new List<(int, int, string)>();
        if (commented) foreach (var line in content) changes.Add((line.Offset + Leading(document, line), 2, ""));
        else { int column = content.Min(l => Leading(document, l)); foreach (var line in content) changes.Add((line.Offset + column, 0, "--")); }
        int changed = ApplyLineChanges(editor, first, last, changes);
        return $"{(commented ? "Uncommented" : "Commented")} {changed:N0} line(s).";
    }

    /// <summary>Ctrl+Shift+/: wraps the selection (or the current line) in <c>/* */</c>, or unwraps it when already wrapped.</summary>
    internal static string ToggleBlockComment(TextEditor editor)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        int start, end;
        if (editor.SelectionLength > 0) { start = editor.SelectionStart; end = start + editor.SelectionLength; }
        else
        {
            var line = document.GetLineByOffset(editor.CaretOffset);
            start = line.Offset + Leading(document, line); end = line.EndOffset;
            while (end > start && IsBlank(document.GetCharAt(end - 1))) end--;
            if (start == end) { document.Insert(editor.CaretOffset, "/**/"); editor.CaretOffset -= 2; return "Inserted an empty block comment."; }
        }
        string text = document.GetText(start, end - start);
        if (text.Length >= 4 && text.StartsWith("/*", StringComparison.Ordinal) && text.EndsWith("*/", StringComparison.Ordinal))
        {
            string inner = text[2..^2];
            document.Replace(start, end - start, inner); editor.Select(start, inner.Length);
            return "Removed the block comment.";
        }
        if (start >= 2 && end + 2 <= document.TextLength && document.GetText(start - 2, 2) == "/*" && document.GetText(end, 2) == "*/")
        {
            using (document.RunUpdate()) { document.Remove(end, 2); document.Remove(start - 2, 2); }
            editor.Select(start - 2, text.Length);
            return "Removed the block comment.";
        }
        document.Replace(start, end - start, "/*" + text + "*/"); editor.Select(start, text.Length + 4);
        return "Added a block comment.";
    }

    /// <summary>Ctrl+Shift+U / Ctrl+Shift+L: changes the selection, or the character right of the caret when nothing is selected.</summary>
    internal static string ChangeCase(TextEditor editor, bool upper)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        int start = editor.SelectionStart, length = editor.SelectionLength;
        if (length == 0)
        {
            var line = document.GetLineByOffset(start);
            if (start >= line.EndOffset) return "No character right of the caret.";
            length = char.IsHighSurrogate(document.GetCharAt(start)) && start + 1 < line.EndOffset ? 2 : 1;
        }
        if (length > SqlFileStore.MaximumBytes) throw new ArgumentException("Select at most 16 MiB of text to change its case.");
        string text = document.GetText(start, length);
        string changed = upper ? text.ToUpperInvariant() : text.ToLowerInvariant();
        if (changed == text) return upper ? "Already uppercase." : "Already lowercase.";
        int caret = editor.CaretOffset; bool selected = editor.SelectionLength > 0; bool caretAtStart = selected && caret == start;
        document.Replace(start, length, changed);
        if (selected) Reselect(editor, start, start + changed.Length, caretAtStart);
        else { editor.Select(caret, 0); editor.CaretOffset = caret; }
        return upper ? "Made uppercase." : "Made lowercase.";
    }

    private static void Reselect(TextEditor editor, int start, int end, bool caretAtStart)
    {
        editor.TextArea.Selection = Selection.Create(editor.TextArea, start, end);
        editor.TextArea.Caret.Offset = caretAtStart ? start : end;
    }

    /// <summary>Alt+Up / Alt+Down: moves the current line, or the lines touched by the selection, past the neighbouring line.</summary>
    internal static string? MoveLines(TextEditor editor, bool up)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        var (first, last) = LineRange(editor);
        if (up ? first == 1 : last == document.LineCount) return null;
        var firstLine = document.GetLineByNumber(first); var lastLine = document.GetLineByNumber(last);
        int blockStart = firstLine.Offset;
        string block = document.GetText(blockStart, lastLine.EndOffset - blockStart);
        int selectionStart = editor.SelectionStart - blockStart, selectionLength = editor.SelectionLength, caret = editor.CaretOffset - blockStart;
        bool caretAtStart = selectionLength > 0 && editor.CaretOffset == editor.SelectionStart;
        int newStart;
        if (up)
        {
            var neighbour = document.GetLineByNumber(first - 1);
            string text = document.GetText(neighbour.Offset, neighbour.Length), delimiter = document.GetText(neighbour.EndOffset, neighbour.DelimiterLength);
            document.Replace(neighbour.Offset, lastLine.EndOffset - neighbour.Offset, block + delimiter + text);
            newStart = neighbour.Offset;
        }
        else
        {
            var neighbour = document.GetLineByNumber(last + 1);
            string text = document.GetText(neighbour.Offset, neighbour.Length), delimiter = document.GetText(lastLine.EndOffset, lastLine.DelimiterLength);
            document.Replace(blockStart, neighbour.EndOffset - blockStart, text + delimiter + block);
            newStart = blockStart + text.Length + delimiter.Length;
        }
        if (selectionLength > 0) Reselect(editor, newStart + selectionStart, newStart + selectionStart + selectionLength, caretAtStart);
        else { editor.Select(newStart + caret, 0); editor.CaretOffset = newStart + caret; }
        int count = last - first + 1;
        return $"Moved {count:N0} line(s) {(up ? "up" : "down")}.";
    }

    /// <summary>Edit ▸ Duplicate: copies the selection right after it, or the current line below it.</summary>
    internal static string Duplicate(TextEditor editor)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        if (editor.SelectionLength > 0)
        {
            int end = editor.SelectionStart + editor.SelectionLength;
            string text = editor.SelectedText;
            document.Insert(end, text); editor.Select(end, text.Length);
            return "Duplicated the selection.";
        }
        var line = document.GetLineByOffset(editor.CaretOffset);
        string lineText = document.GetText(line.Offset, line.Length);
        string newline = line.DelimiterLength > 0 ? document.GetText(line.EndOffset, line.DelimiterLength) : TextUtilities.GetNewLineFromDocument(document, line.LineNumber);
        int caret = editor.CaretOffset;
        document.Insert(line.EndOffset, newline + lineText);
        editor.Select(caret + lineText.Length + newline.Length, 0); editor.CaretOffset = caret + lineText.Length + newline.Length;
        return "Duplicated the current line.";
    }

    /// <summary>Sort Lines: orders the selected lines without regard to case; the line breaks stay where they were.</summary>
    internal static string SortLines(TextEditor editor)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        var (first, last) = LineRange(editor);
        if (first == last) return "Select two or more lines to sort.";
        var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber).ToList();
        var texts = lines.Select(l => document.GetText(l.Offset, l.Length)).ToList();
        var sorted = texts.OrderBy(t => t, SortComparer).ToList();
        if (sorted.SequenceEqual(texts, StringComparer.Ordinal)) return "The lines are already sorted.";
        var builder = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            builder.Append(sorted[i]);
            if (i < lines.Count - 1) builder.Append(document.GetText(lines[i].EndOffset, lines[i].DelimiterLength));
        }
        int start = lines[0].Offset;
        document.Replace(start, lines[^1].EndOffset - start, builder.ToString());
        editor.Select(start, document.GetLineByNumber(last).EndOffset - start);
        return $"Sorted {lines.Count:N0} line(s).";
    }

    /// <summary>Join Lines: joins the selected lines (or the current line and the next) with one space, dropping the indentation of each joined line.</summary>
    internal static string? JoinLines(TextEditor editor)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        var (first, last) = LineRange(editor);
        if (first == last) { if (last == document.LineCount) return "There is no next line to join."; last++; }
        var builder = new StringBuilder();
        var firstLine = document.GetLineByNumber(first);
        builder.Append(document.GetText(firstLine.Offset, firstLine.Length));
        int joinPoint = 0;
        for (int number = first + 1; number <= last; number++)
        {
            while (builder.Length > 0 && IsBlank(builder[^1])) builder.Length--;
            var line = document.GetLineByNumber(number);
            string text = document.GetText(line.Offset, line.Length).TrimStart(' ', '\t');
            if (builder.Length > 0 && text.Length > 0) builder.Append(' ');
            if (number == first + 1) joinPoint = builder.Length;
            builder.Append(text);
        }
        bool selected = editor.SelectionLength > 0;
        int start = firstLine.Offset;
        document.Replace(start, document.GetLineByNumber(last).EndOffset - start, builder.ToString());
        if (selected) editor.Select(start, builder.Length);
        else { editor.Select(start + joinPoint, 0); editor.CaretOffset = start + joinPoint; }
        return $"Joined {last - first + 1:N0} line(s).";
    }

    /// <summary>Ctrl+K, Ctrl+\: removes spaces and tabs around the caret or inside the selection; a run between two words keeps one space.</summary>
    internal static string DeleteHorizontalWhiteSpace(TextEditor editor)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        var changes = new List<(int Offset, int Remove, string Insert)>();
        void Run(int runStart, int runEnd)
        {
            var line = document.GetLineByOffset(runStart);
            bool between = runStart > line.Offset && runEnd < line.EndOffset;
            string replacement = between ? " " : "";
            if (document.GetText(runStart, runEnd - runStart) != replacement) changes.Add((runStart, runEnd - runStart, replacement));
        }
        if (editor.SelectionLength == 0)
        {
            int caret = editor.CaretOffset; var line = document.GetLineByOffset(caret);
            int runStart = caret, runEnd = caret;
            while (runStart > line.Offset && IsBlank(document.GetCharAt(runStart - 1))) runStart--;
            while (runEnd < line.EndOffset && IsBlank(document.GetCharAt(runEnd))) runEnd++;
            if (runStart < runEnd) Run(runStart, runEnd);
        }
        else
        {
            LineRange(editor);
            int offset = editor.SelectionStart, end = offset + editor.SelectionLength;
            while (offset < end)
            {
                if (!IsBlank(document.GetCharAt(offset))) { offset++; continue; }
                int runEnd = offset; while (runEnd < end && IsBlank(document.GetCharAt(runEnd))) runEnd++;
                Run(offset, runEnd); offset = runEnd;
            }
        }
        if (changes.Count == 0) return "No white space to delete.";
        var caretAnchor = Anchor(document, editor.CaretOffset);
        int selectionStart = editor.SelectionStart; bool selected = editor.SelectionLength > 0;
        var endAnchor = Anchor(document, editor.SelectionStart + editor.SelectionLength);
        using (document.RunUpdate()) foreach (var change in changes.OrderByDescending(c => c.Offset)) document.Replace(change.Offset, change.Remove, change.Insert);
        if (selected) editor.Select(selectionStart, endAnchor.Offset - selectionStart);
        else { editor.Select(caretAnchor.Offset, 0); editor.CaretOffset = caretAnchor.Offset; }
        return $"Deleted white space in {changes.Count:N0} place(s).";
    }

    private static int VisualColumn(string indentation, int tabSize)
    {
        int column = 0;
        foreach (char c in indentation) column = c == '\t' ? (column / tabSize + 1) * tabSize : column + 1;
        return column;
    }

    /// <summary>Tabify / Untabify Selected Lines: rewrites only the leading indentation, with the editor's tab size.</summary>
    internal static string Tabify(TextEditor editor, bool toTabs)
    {
        EnsureWritable(editor);
        var document = editor.Document; int tabSize = Math.Max(1, editor.Options.IndentationSize);
        var (first, last) = LineRange(editor);
        var changes = new List<(int, int, string)>();
        for (int number = first; number <= last; number++)
        {
            var line = document.GetLineByNumber(number); int length = Leading(document, line);
            string current = document.GetText(line.Offset, length); int column = VisualColumn(current, tabSize);
            string wanted = toTabs ? new string('\t', column / tabSize) + new string(' ', column % tabSize) : new string(' ', column);
            if (wanted != current) changes.Add((line.Offset, length, wanted));
        }
        int changed = ApplyLineChanges(editor, first, last, changes);
        return changed == 0 ? "The indentation already uses " + (toTabs ? "tabs." : "spaces.") : $"{(toTabs ? "Tabified" : "Untabified")} {changed:N0} line(s).";
    }

    /// <summary>Increase / Decrease Line Indent: one indentation level per line (the editor's indentation string; blank lines skipped in a block).</summary>
    internal static string ChangeIndent(TextEditor editor, bool increase)
    {
        EnsureWritable(editor);
        var document = editor.Document; int tabSize = Math.Max(1, editor.Options.IndentationSize);
        var (first, last) = LineRange(editor);
        var changes = new List<(int, int, string)>();
        for (int number = first; number <= last; number++)
        {
            var line = document.GetLineByNumber(number);
            if (increase)
            {
                if (first != last && IsBlankLine(document, line)) continue;
                changes.Add((line.Offset, 0, editor.Options.IndentationString));
            }
            else
            {
                int remove = 0;
                if (line.Length > 0 && document.GetCharAt(line.Offset) == '\t') remove = 1;
                else while (remove < tabSize && remove < line.Length && document.GetCharAt(line.Offset + remove) == ' ') remove++;
                if (remove > 0) changes.Add((line.Offset, remove, ""));
            }
        }
        int changed = ApplyLineChanges(editor, first, last, changes);
        return changed == 0 ? "No indentation to remove." : $"{(increase ? "Indented" : "Outdented")} {changed:N0} line(s).";
    }

    /// <summary>Insert File As Text: replaces the selection (or inserts at the caret) with the file text, line breaks matched to the document; the caret stays at the start of the inserted text.</summary>
    internal static string InsertText(TextEditor editor, string text, string fileName)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        int start = editor.SelectionStart;
        string newline = TextUtilities.GetNewLineFromDocument(document, document.GetLineByOffset(start).LineNumber);
        string normalized = TextUtilities.NormalizeNewLines(text, newline);
        document.Replace(start, editor.SelectionLength, normalized);
        editor.Select(start, 0); editor.CaretOffset = start;
        return $"Inserted {normalized.Length:N0} character(s) from {fileName}.";
    }

    /// <summary>Replaces several ranges back to front in one Undo step, keeping the caret and the selection anchors.</summary>
    private static void ApplyDocumentChanges(TextEditor editor, List<(int Offset, int Remove, string Insert)> changes)
    {
        var document = editor.Document;
        bool selected = editor.SelectionLength > 0;
        var caret = Anchor(document, editor.CaretOffset);
        var selectionStart = Anchor(document, editor.SelectionStart);
        var selectionEnd = Anchor(document, editor.SelectionStart + editor.SelectionLength);
        using (document.RunUpdate())
            foreach (var change in changes.OrderByDescending(c => c.Offset)) document.Replace(change.Offset, change.Remove, change.Insert);
        if (selected) editor.Select(selectionStart.Offset, Math.Max(0, selectionEnd.Offset - selectionStart.Offset));
        else editor.Select(caret.Offset, 0);
        editor.CaretOffset = selected ? editor.SelectionStart + editor.SelectionLength : caret.Offset;
    }

    /// <summary>The line break of the document as SSMS shows it (CRLF, LF or CR); a document without breaks uses the Windows one.</summary>
    internal static string LineEndingName(TextDocument document)
    {
        var line = document.GetLineByNumber(1);
        if (line.DelimiterLength == 0) return "CRLF";
        string delimiter = document.GetText(line.EndOffset, line.DelimiterLength);
        return delimiter switch { "\r\n" => "CRLF", "\n" => "LF", _ => "CR" };
    }

    /// <summary>Edit ▸ Advanced ▸ Set End of Line Sequence: converts every line break of the document (as in SSMS 22.11, M1078).</summary>
    internal static string SetLineEndings(TextEditor editor, string newline)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        var changes = new List<(int, int, string)>();
        foreach (var line in document.Lines)
            if (line.DelimiterLength > 0 && document.GetText(line.EndOffset, line.DelimiterLength) != newline) changes.Add((line.EndOffset, line.DelimiterLength, newline));
        string name = newline switch { "\r\n" => "CRLF", "\n" => "LF", _ => "CR" };
        if (changes.Count == 0) return $"Line breaks already use {name}.";
        ApplyDocumentChanges(editor, changes);
        return $"Converted {changes.Count:N0} line break(s) to {name}.";
    }

    /// <summary>Edit ▸ Advanced ▸ Set Indentation: Spaces or Tabs for new indentation, converting the leading indentation of every line (as in SSMS 22.11, M1078).</summary>
    internal static string SetIndentation(TextEditor editor, bool spaces)
    {
        EnsureWritable(editor);
        var document = editor.Document; int tabSize = Math.Max(1, editor.Options.IndentationSize);
        var changes = new List<(int, int, string)>();
        foreach (var line in document.Lines)
        {
            int length = Leading(document, line);
            if (length == 0) continue;
            string current = document.GetText(line.Offset, length); int column = VisualColumn(current, tabSize);
            string wanted = spaces ? new string(' ', column) : new string('\t', column / tabSize) + new string(' ', column % tabSize);
            if (wanted != current) changes.Add((line.Offset, length, wanted));
        }
        editor.Options.ConvertTabsToSpaces = spaces;
        if (changes.Count > 0) ApplyDocumentChanges(editor, changes);
        return changes.Count == 0 ? $"Indentation set to {(spaces ? "spaces" : "tabs")}." : $"Indentation set to {(spaces ? "spaces" : "tabs")}; converted {changes.Count:N0} line(s).";
    }

    /// <summary>Edit ▸ Advanced ▸ Format SQL (Ctrl+K, Ctrl+Q): the selection, or the whole document without one, in one Undo step.
    /// When the SQL cannot be read nothing changes (as in SSMS 22.11, piloted in M1077).</summary>
    internal static string FormatSql(TextEditor editor)
    {
        EnsureWritable(editor);
        var document = editor.Document;
        bool selection = editor.SelectionLength > 0;
        int start = selection ? editor.SelectionStart : 0, length = selection ? editor.SelectionLength : document.TextLength;
        string text = document.GetText(start, length);
        if (text.Trim().Length == 0) return "There is no SQL to format.";
        string newline = TextUtilities.GetNewLineFromDocument(document, document.GetLineByOffset(start).LineNumber);
        string formatted;
        try { formatted = SqlFormatter.Format(text, newline); }
        catch (FormatException ex) { return $"SQL formatting could not be completed because the SQL could not be fully read: {ex.Message}. Nothing was changed."; }
        if (formatted == text) return "The SQL is already formatted.";
        document.Replace(start, length, formatted);
        if (selection) editor.Select(start, formatted.Length);
        else { editor.Select(start + formatted.Length, 0); editor.CaretOffset = start + formatted.Length; }
        return $"Formatted {(selection ? "the selection" : "the document")}.";
    }
}
