using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Documents;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

internal sealed class SqlTextRange : ITextRangeProvider
{
    private readonly SqlTextAreaAutomationPeer peer;
    private readonly TextDocument document;
    private TextAnchor start = null!, end = null!;
    internal SqlTextRange(SqlTextAreaAutomationPeer peer, TextDocument document, int start, int end)
    {
        this.peer = peer; this.document = document; Set(start, end);
    }
    private void Check()
    {
        if (!ReferenceEquals(peer.Document, document)) throw new ElementNotAvailableException("The editor document has been replaced.");
    }
    private int Start => Math.Min(start.Offset, end.Offset);
    private int End => Math.Max(start.Offset, end.Offset);
    private void Set(int first, int last)
    {
        start = document.CreateAnchor(first); start.SurviveDeletion = true; start.MovementType = AnchorMovementType.AfterInsertion;
        if (first == last) { end = start; return; }
        end = document.CreateAnchor(last); end.SurviveDeletion = true; end.MovementType = AnchorMovementType.BeforeInsertion;
    }
    private SqlTextRange Other(ITextRangeProvider range)
    {
        Check();
        if (range is not SqlTextRange other || other.peer != peer || other.document != document) throw new ArgumentException("Ranges must belong to the same editor document.", nameof(range));
        other.Check(); return other;
    }
    private int Endpoint(TextPatternRangeEndpoint endpoint) => endpoint switch
    {
        TextPatternRangeEndpoint.Start => Start, TextPatternRangeEndpoint.End => End,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
    };
    private void SetEndpoint(TextPatternRangeEndpoint endpoint, int offset)
    {
        if (endpoint == TextPatternRangeEndpoint.Start) Set(offset, Math.Max(offset, End));
        else if (endpoint == TextPatternRangeEndpoint.End) Set(Math.Min(Start, offset), offset);
        else throw new ArgumentOutOfRangeException(nameof(endpoint));
    }
    // Syntax formatting, visual wrapping and pages are not text units of this
    // provider. UIA requires fallback to the next supported larger unit.
    private static TextUnit Unit(TextUnit unit) => unit switch
    {
        TextUnit.Character => TextUnit.Character,
        TextUnit.Format or TextUnit.Word => TextUnit.Word,
        TextUnit.Line or TextUnit.Paragraph => TextUnit.Paragraph,
        TextUnit.Page or TextUnit.Document => TextUnit.Document,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };
    private int Next(int offset, TextUnit unit, int direction)
    {
        if (direction > 0 && offset == document.TextLength || direction < 0 && offset == 0) return offset;
        if (unit == TextUnit.Document) return direction > 0 ? document.TextLength : 0;
        if (unit == TextUnit.Paragraph)
        {
            var line = document.GetLineByOffset(offset);
            return direction > 0 ? line.NextLine?.Offset ?? document.TextLength
                : offset > line.Offset ? line.Offset : line.PreviousLine?.Offset ?? 0;
        }
        if (unit == TextUnit.Character)
        {
            if (direction > 0 && document.GetCharAt(offset) == '\r' && offset + 1 < document.TextLength && document.GetCharAt(offset + 1) == '\n') return offset + 2;
            if (direction < 0 && offset >= 2 && document.GetCharAt(offset - 1) == '\n' && document.GetCharAt(offset - 2) == '\r') return offset - 2;
        }
        int next = TextUtilities.GetNextCaretPosition(document, offset,
            direction > 0 ? LogicalDirection.Forward : LogicalDirection.Backward,
            unit == TextUnit.Character ? CaretPositioningMode.Normal : CaretPositioningMode.WordStart);
        return next < 0 ? direction > 0 ? document.TextLength : 0 : next;
    }
    private (int First, int Last) Enclosing(int offset, TextUnit unit)
    {
        if (unit == TextUnit.Document) return (0, document.TextLength);
        if (unit == TextUnit.Paragraph)
        {
            var line = document.GetLineByOffset(offset);
            return (line.Offset, line.Offset + line.TotalLength);
        }
        if (document.TextLength == 0) return (0, 0);
        int position = offset == document.TextLength ? Next(offset, unit, -1) : offset;
        int first = TextUtilities.GetNextCaretPosition(document, position + 1, LogicalDirection.Backward,
            unit == TextUnit.Character ? CaretPositioningMode.Normal : CaretPositioningMode.WordStart);
        first = Math.Max(0, first);
        if (unit == TextUnit.Character && first > 0 && first < document.TextLength && document.GetCharAt(first) == '\n' && document.GetCharAt(first - 1) == '\r') first--;
        return (first, Next(first, unit, 1));
    }
    public ITextRangeProvider Clone() { Check(); return new SqlTextRange(peer, document, Start, End); }
    public bool Compare(ITextRangeProvider range) { var other = Other(range); return Start == other.Start && End == other.End; }
    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
        => EndpointAfterCheck(endpoint).CompareTo(Other(targetRange).Endpoint(targetEndpoint));
    private int EndpointAfterCheck(TextPatternRangeEndpoint endpoint) { Check(); return Endpoint(endpoint); }
    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var other = Other(targetRange); _ = Endpoint(endpoint); SetEndpoint(endpoint, other.Endpoint(targetEndpoint));
    }
    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        Check(); int offset = Endpoint(endpoint); unit = Unit(unit);
        if (count == 0) return 0;
        int direction = Math.Sign(count), moved = 0;
        long requested = Math.Abs((long)count);
        while (Math.Abs((long)moved) < requested)
        {
            int next = Next(offset, unit, direction);
            if (next == offset) break;
            offset = next; moved += direction;
        }
        SetEndpoint(endpoint, offset); return moved;
    }
    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        Check(); var bounds = Enclosing(Start, Unit(unit)); Set(bounds.First, bounds.Last);
    }
    public int Move(TextUnit unit, int count)
    {
        Check(); unit = Unit(unit);
        if (count == 0) return 0;
        bool empty = Start == End;
        int position = Start, direction = Math.Sign(count), moved = 0;
        var bounds = empty ? (First: position, Last: position) : Enclosing(position, unit);
        position = bounds.First;
        while (Math.Abs((long)moved) < Math.Abs((long)count))
        {
            int next = Next(position, unit, direction);
            if (next == position || !empty && next == document.TextLength) break;
            var nextBounds = empty ? (First: next, Last: next) : Enclosing(next, unit);
            if (nextBounds.First == position) break;
            bounds = nextBounds; position = bounds.First; moved += direction;
        }
        // Do not normalize or expand a range when no movement was possible.
        if (moved != 0) Set(bounds.First, bounds.Last);
        return moved;
    }
    public string GetText(int maxLength)
    {
        Check(); if (maxLength < -1) throw new ArgumentOutOfRangeException(nameof(maxLength));
        return document.GetText(Start, maxLength == -1 ? End - Start : Math.Min(End - Start, maxLength));
    }
    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        Check(); ArgumentException.ThrowIfNullOrEmpty(text);
        string content = GetText(-1);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int offset = backward ? content.LastIndexOf(text, comparison) : content.IndexOf(text, comparison);
        return offset < 0 ? null : new SqlTextRange(peer, document, Start + offset, Start + offset + text.Length);
    }
    public object GetAttributeValue(int attributeId)
    {
        Check();
        if (attributeId == TextPatternIdentifiers.IsReadOnlyAttribute.Id)
        {
            if (peer.IsReadOnly) return true;
            if (Start == End) return !peer.Area.ReadOnlySectionProvider.CanInsert(Start);
            int writable = peer.Area.ReadOnlySectionProvider.GetDeletableSegments(new SelectionSegment(Start, End)).Sum(s => s.Length);
            return writable == 0 ? true : writable == End - Start ? false : TextPatternIdentifiers.MixedAttributeValue;
        }
        if (attributeId == TextPatternIdentifiers.IsHiddenAttribute.Id) return !peer.Area.IsVisible;
        return AutomationElement.NotSupported;
    }
    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward)
    {
        object actual = GetAttributeValue(attributeId);
        return actual != AutomationElement.NotSupported && Equals(actual, value) ? Clone() : null;
    }
    public void Select()
    {
        Check(); if (!peer.Enabled) throw new ElementNotEnabledException();
        int first = Start, last = End;
        peer.Area.Caret.Offset = last;
        peer.Area.Selection = Selection.Create(peer.Area, first, last);
    }
    public void AddToSelection() { Check(); if (Start == End) { Select(); return; } throw new InvalidOperationException("The SQL editor supports a single selection."); }
    public void RemoveFromSelection() { Check(); if (Start == End) { Select(); return; } throw new InvalidOperationException("The SQL editor supports a single selection."); }
    public IRawElementProviderSimple GetEnclosingElement() { Check(); return peer.Provider; }
    public IRawElementProviderSimple[] GetChildren() { Check(); return []; }
    public double[] GetBoundingRectangles()
    {
        Check(); if (!peer.HasScreenLayout) return [];
        var view = peer.Area.TextView;
        var viewport = new Rect(view.RenderSize);
        var result = new List<double>();
        foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(view, new SelectionSegment(Start, End)))
        {
            var clipped = Rect.Intersect(viewport, rectangle);
            if (clipped.IsEmpty) continue;
            Point topLeft = view.PointToScreen(clipped.TopLeft), bottomRight = view.PointToScreen(clipped.BottomRight);
            result.AddRange([topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y]);
        }
        return result.ToArray();
    }
    public void ScrollIntoView(bool alignToTop)
    {
        Check(); if (!peer.HasScreenLayout) return;
        if (peer.Area.GetService(typeof(TextEditor)) is not TextEditor editor) return;
        var location = document.GetLocation(alignToTop ? Start : End);
        editor.ScrollTo(location.Line, location.Column, alignToTop ? VisualYPosition.LineTop : VisualYPosition.LineBottom,
            alignToTop ? 0 : peer.Area.TextView.ActualHeight, 0);
    }
}
