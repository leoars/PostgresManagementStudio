using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using PGMS.Desktop;

internal static class SqlTextAutomationChecks
{
    internal static int Run(string output)
    {
        string root = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        output = Path.GetFullPath(output);
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Workspace artifacts only.");
        for (string? path = Path.GetDirectoryName(output); path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked paths refused.");
        var results = new List<object>(); int failed = 0;
        void Check(bool ok, string name)
        {
            results.Add(new { name, passed = ok }); if (!ok) failed++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
        }
        void Throws<T>(Action action, string name) where T : Exception
        {
            try { action(); Check(false, name); }
            catch (T) { Check(true, name); }
        }
        try
        {
            var editor = new SqlTextEditor { Text = "SELECT 1" };
            AutomationProperties.SetName(editor, "SQL editor");
            var rootPeer = UIElementAutomationPeer.CreatePeerForElement(editor)!;
            var areaPeer = UIElementAutomationPeer.CreatePeerForElement(editor.TextArea)!;
            var provider = (ITextProvider)rootPeer.GetPattern(PatternInterface.Text)!;
            var value = (IValueProvider)rootPeer.GetPattern(PatternInterface.Value)!;
            Check(ReferenceEquals(provider, areaPeer.GetPattern(PatternInterface.Text)), "root and text area expose the same corrected provider");
            Check(areaPeer.GetName() == "SQL editor", "text area has editor name");
            Check(provider.DocumentRange.GetText(-1) == "SELECT 1", "document text remains available");
            Check(provider.SupportedTextSelection == SupportedTextSelection.Single, "single selection contract");
            ITextRangeProvider At(int offset)
            {
                var range = provider.DocumentRange;
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, offset);
                return range;
            }
            int Offset(ITextRangeProvider range, TextPatternRangeEndpoint endpoint = TextPatternRangeEndpoint.Start)
            {
                var prefix = provider.DocumentRange;
                prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, endpoint);
                return prefix.GetText(-1).Length;
            }
            foreach (TextPatternRangeEndpoint endpoint in Enum.GetValues<TextPatternRangeEndpoint>())
            {
                var atEnd = At(8);
                Check(atEnd.MoveEndpointByUnit(endpoint, TextUnit.Character, 1) == 0 && Offset(atEnd, endpoint) == 8, $"{endpoint} forward at end returns zero");
                var atStart = At(0);
                Check(atStart.MoveEndpointByUnit(endpoint, TextUnit.Character, -1) == 0 && Offset(atStart, endpoint) == 0, $"{endpoint} backward at start returns zero");
                Check(atStart.MoveEndpointByUnit(endpoint, TextUnit.Character, int.MaxValue) == 8 && Offset(atStart, endpoint) == 8, $"{endpoint} overshoot returns actual distance");
                Check(atStart.MoveEndpointByUnit(endpoint, TextUnit.Character, int.MinValue) == -8 && Offset(atStart, endpoint) == 0, $"{endpoint} negative extreme is bounded");
                foreach (TextUnit unit in Enum.GetValues<TextUnit>())
                {
                    var range = At(3); var clone = range.Clone();
                    Check(range.MoveEndpointByUnit(endpoint, unit, 0) == 0 && range.Compare(clone), $"{endpoint} {unit} count zero leaves both endpoints intact");
                }
            }
            var whole = provider.DocumentRange;
            foreach (TextUnit unit in Enum.GetValues<TextUnit>())
            {
                var clone = whole.Clone();
                Check(clone.Move(unit, 0) == 0 && clone.Compare(whole), $"Move {unit} zero does not normalize selection");
            }
            var crossing = provider.DocumentRange;
            crossing.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 8);
            Check(crossing.GetText(-1) == "", "start crossing end collapses range");
            crossing.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -3);
            Check(Offset(crossing) == 5 && crossing.GetText(-1) == "", "end crossing start collapses range");
            var independent = At(2); var copy = independent.Clone();
            independent.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 1);
            Check(Offset(copy) == 2 && Offset(independent) == 3, "clone endpoints move independently");
            var selected = provider.DocumentRange.FindText("LECT", false, false)!;
            selected.Select();
            Check(editor.SelectedText == "LECT" && editor.CaretOffset == 6 && provider.GetSelection()[0].GetText(-1) == "LECT", "selection roundtrip retains text and caret");
            Check(!editor.IsKeyboardFocusWithin, "selection does not force keyboard focus");
            At(2).Select();
            Check(editor.SelectionLength == 0 && editor.CaretOffset == 2 && provider.GetSelection()[0].GetText(-1) == "", "degenerate range selects caret only");
            Throws<InvalidOperationException>(() => selected.AddToSelection(), "single-selection add is refused");
            Throws<InvalidOperationException>(() => selected.RemoveFromSelection(), "single-selection remove is refused");
            At(3).AddToSelection();
            Check(editor.CaretOffset == 3 && editor.SelectionLength == 0, "degenerate AddToSelection moves insertion point");
            At(4).RemoveFromSelection();
            Check(editor.CaretOffset == 4 && editor.SelectionLength == 0, "degenerate RemoveFromSelection moves insertion point");
            Check(whole.GetText(3) == "SEL" && whole.GetText(0) == "", "GetText limits are honored");
            Throws<ArgumentOutOfRangeException>(() => whole.GetText(-2), "invalid GetText limit refused");
            Check(whole.FindText("select", false, true)?.GetText(-1) == "SELECT" && whole.FindText("missing", false, false) is null, "ordinal search matches or returns null");
            Check(ReferenceEquals(whole.GetAttributeValue(-999), AutomationElement.NotSupported), "unknown attributes return UIA NotSupported");
            Check(whole.FindAttribute(-999, true, false) is null, "unknown attributes do not invent a match");
            Throws<ArgumentOutOfRangeException>(() => whole.MoveEndpointByUnit((TextPatternRangeEndpoint)99, TextUnit.Character, 0), "invalid endpoint refused");
            Throws<ArgumentOutOfRangeException>(() => whole.Move((TextUnit)99, 0), "invalid text unit refused");
            var otherEditor = new SqlTextEditor { Text = "SELECT 1" };
            var otherProvider = (ITextProvider)UIElementAutomationPeer.CreatePeerForElement(otherEditor.TextArea)!.GetPattern(PatternInterface.Text)!;
            Throws<ArgumentException>(() => whole.Compare(otherProvider.DocumentRange), "ranges from different editors refused");
            Check(provider.GetVisibleRanges().Length == 0 && whole.GetBoundingRectangles().Length == 0, "unshown editor returns no fabricated visible geometry");
            whole.ScrollIntoView(true); whole.ScrollIntoView(false);
            Throws<ElementNotAvailableException>(() => provider.RangeFromPoint(new Point(0, 0)), "point mapping requires screen layout");
            Check(whole.GetChildren().Length == 0, "SQL text stream has no embedded objects");
            Throws<ElementNotAvailableException>(() => whole.GetEnclosingElement(), "unshown editor does not fabricate an automation root");
            value.SetValue("one\r\ntwo\nthree");
            Check(value.Value == "one\r\ntwo\nthree", "Value writes the actual document");
            foreach (TextUnit unit in new[] { TextUnit.Line, TextUnit.Paragraph })
            {
                var lines = At(0);
                Check(lines.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, int.MaxValue) == 3 && Offset(lines) == editor.Text.Length, $"{unit} crosses CRLF and LF to document edge");
                Check(lines.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, 1) == 0, $"{unit} never moves backwards at final edge");
                Check(lines.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, int.MinValue) == -3 && Offset(lines) == 0, $"{unit} returns actual reverse count");
            }
            var page = At(0);
            Check(page.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Page, 50) == 1 && Offset(page) == editor.Text.Length, "unsupported page falls back to document");
            var lineRange = At(0); lineRange.ExpandToEnclosingUnit(TextUnit.Line);
            Check(lineRange.GetText(-1) == "one\r\n", "line fallback includes paragraph delimiter");
            Check(lineRange.Move(TextUnit.Paragraph, 1) == 1 && lineRange.GetText(-1) == "two\n", "nondegenerate Move selects next paragraph");
            Check(lineRange.Move(TextUnit.Paragraph, 10) == 1 && lineRange.GetText(-1) == "three", "nondegenerate Move stops at final paragraph");
            var last = lineRange.Clone();
            Check(lineRange.Move(TextUnit.Paragraph, 1) == 0 && lineRange.Compare(last), "nondegenerate final paragraph makes no false progress");
            value.SetValue("A\U0001F600\r\nB");
            var unicode = At(0);
            Check(unicode.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 2) == 2 && Offset(unicode) == 3, "character movement respects surrogate pair");
            Check(unicode.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 1) == 1 && Offset(unicode) == 5, "CRLF is one caret movement");
            value.SetValue("alpha beta");
            var word = At(0); word.ExpandToEnclosingUnit(TextUnit.Word);
            Check(word.GetText(-1).StartsWith("alpha", StringComparison.Ordinal), "word expansion exposes first word");
            Check(word.Move(TextUnit.Word, 1) == 1 && word.GetText(-1) == "beta", "word Move advances to next word");
            Check(word.Move(TextUnit.Word, 1) == 0, "last word reports no movement");
            var tracked = provider.DocumentRange.FindText("beta", false, false)!;
            editor.Document.Insert(0, "X ");
            Check(tracked.GetText(-1) == "beta", "range follows insertion before it");
            editor.Document.Remove(Offset(tracked), 4);
            Check(tracked.GetText(-1) == "", "range survives deletion of its text");
            editor.IsReadOnly = true;
            Check(value.IsReadOnly && (bool)provider.DocumentRange.GetAttributeValue(TextPatternIdentifiers.IsReadOnlyAttribute.Id), "read-only state exposed");
            string before = editor.Text;
            Throws<InvalidOperationException>(() => value.SetValue("changed"), "Value cannot bypass read-only");
            Check(editor.Text == before, "read-only refusal preserves document");
            editor.IsReadOnly = false; editor.IsEnabled = false;
            Throws<ElementNotEnabledException>(() => value.SetValue("changed"), "disabled editor refuses Value");
            Throws<ElementNotEnabledException>(() => provider.DocumentRange.Select(), "disabled editor refuses selection");
            Throws<ElementNotEnabledException>(() => rootPeer.SetFocus(), "disabled composite editor refuses explicit UIA focus");
            editor.IsEnabled = true;
            var originalProtection = editor.TextArea.ReadOnlySectionProvider;
            editor.TextArea.ReadOnlySectionProvider = new PartialReadOnly();
            Throws<InvalidOperationException>(() => value.SetValue("changed"), "Value refuses partially protected document");
            Check(editor.Text == before, "partial protection refusal preserves original text");
            Check(ReferenceEquals(provider.DocumentRange.GetAttributeValue(TextPatternIdentifiers.IsReadOnlyAttribute.Id), TextPatternIdentifiers.MixedAttributeValue), "partially protected range exposes mixed read-only attribute");
            editor.TextArea.ReadOnlySectionProvider = originalProtection;
            var stale = provider.DocumentRange;
            editor.Document = new TextDocument("replacement");
            Throws<ElementNotAvailableException>(() => stale.GetText(-1), "range does not silently retarget replaced document");
            Check(provider.DocumentRange.GetText(-1) == "replacement", "provider follows explicit document replacement");
            value.SetValue("");
            foreach (TextUnit unit in Enum.GetValues<TextUnit>())
            {
                var empty = provider.DocumentRange;
                Check(empty.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, int.MaxValue) == 0 && empty.Move(unit, int.MinValue) == 0 && empty.GetText(-1) == "", $"empty document {unit} bounded");
            }
            value.SetValue("a\r\nb");
            var newline = provider.DocumentRange.FindText("\n", false, false)!;
            newline.ExpandToEnclosingUnit(TextUnit.Character);
            Check(newline.GetText(-1) == "\r\n", "expanding at LF includes complete CRLF");
            var traversals = new[] { "", "x", "a\r\nb\n", "a\U0001F600e\u0301 b", "SELECT a,b::int FROM t;" };
            foreach (string text in traversals)
            {
                value.SetValue(text);
                foreach (TextUnit unit in Enum.GetValues<TextUnit>())
                {
                    var cursor = provider.DocumentRange;
                    cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start);
                    bool monotonic = true; int previous = 0, operations = 0;
                    while (true)
                    {
                        int moved = cursor.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, 1);
                        int offset = Offset(cursor);
                        if (moved == 0) { monotonic &= offset == previous && offset == text.Length; break; }
                        monotonic &= moved == 1 && offset > previous && offset <= text.Length;
                        previous = offset;
                        if (++operations > text.Length + 1) { monotonic = false; break; }
                    }
                    Check(monotonic, $"finite monotonic {unit} traversal of synthetic text length {text.Length}");
                }
            }
            value.SetValue(new string('W', 120)); editor.WordWrap = true;
            var view = editor.TextArea.TextView;
            view.Measure(new Size(70, 25)); view.Arrange(new Rect(0, 0, 70, 25));
            var measuredRanges = ((SqlTextAreaAutomationPeer)areaPeer).VisibleRangesFromLayout();
            Check(measuredRanges.Length > 0 && measuredRanges.Sum(r => r.GetText(-1).Length) < editor.Text.Length, "offscreen layout clips wrapped text to viewport instead of whole paragraph");
            Check(provider.GetVisibleRanges().Length == 0, "offscreen layout does not claim a screen-visible range");
            value.SetValue("SELECT 1");
            editor.Select(0, editor.Text.Length); SqlLineComments.Apply(editor, false);
            Check(provider.DocumentRange.GetText(-1) == "--SELECT 1", "existing comment operation remains exposed through provider");
            using (var stream = File.OpenRead("src/PGMS.Desktop/Resources/PostgreSQL.xshd"))
            using (var reader = System.Xml.XmlReader.Create(stream))
                editor.SyntaxHighlighting = ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(reader, ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance);
            HighlightingCacheChecks.Run(editor, Check);
        }
        catch (Exception ex) { Check(false, "Unexpected exception: " + ex); }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(stream, new { total = results.Count, failed, results, limitation = "Direct provider calls only; no shown windows, screen reader, desktop input or database. Geometry on screen and global keyboard incident are not certified." }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"{results.Count - failed}/{results.Count} passed");
        return failed == 0 ? 0 : 1;
    }
    private sealed class PartialReadOnly : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => true;
        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
        {
            if (segment.Length > 1) yield return new SelectionSegment(segment.Offset + 1, segment.EndOffset);
        }
    }
}
