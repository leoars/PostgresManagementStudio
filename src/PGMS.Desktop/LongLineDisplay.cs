using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

/// <summary>
/// Stops drawing a line after <see cref="Limit"/> characters. AvalonEdit formats a whole document line at once, so a
/// 262,144-character line costs about 240 ms to lay out and again on every keystroke; the cut keeps it near 30 ms.
/// Only the display changes: the document text, Save, Copy, Find and Execute still see the complete line.
/// Clicking the marker shows every line in full for this editor.
/// </summary>
internal sealed class LongLineElementGenerator : VisualLineElementGenerator
{
    internal const int Limit = 10000;
    private bool showFull;

    /// <summary>True after the user asked to see long lines in full in this editor.</summary>
    internal bool ShowFull
    {
        get => showFull;
        set { if (showFull == value) return; showFull = value; Changed?.Invoke(); }
    }

    internal event Action? Changed;

    /// <summary>Offset where the display of the line that contains <paramref name="offset"/> stops, or -1.</summary>
    internal int CutOffset(int offset)
    {
        if (showFull) return -1;
        var document = CurrentContext.Document;
        DocumentLine line = document.GetLineByOffset(offset);
        if (line.Length <= Limit) return -1;
        int cut = line.Offset + Limit;
        // Never leave half of a surrogate pair on the visible side.
        if (char.IsLowSurrogate(document.GetCharAt(cut)) && char.IsHighSurrogate(document.GetCharAt(cut - 1))) cut--;
        return cut;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        int cut = CutOffset(startOffset);
        return cut >= startOffset ? cut : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (CutOffset(offset) != offset) return null;
        DocumentLine line = CurrentContext.Document.GetLineByOffset(offset);
        int hidden = line.EndOffset - offset;
        var properties = new VisualLineElementTextRunProperties(CurrentContext.GlobalTextRunProperties);
        properties.SetForegroundBrush(Brushes.DimGray);
        properties.SetBackgroundBrush(MarkerBackground);
        string text = " … (+" + hidden.ToString("N0", CultureInfo.InvariantCulture) + (hidden == 1 ? " character) " : " characters) ");
        TextLine textLine = FormattedTextElement.PrepareText(TextFormatter.Create(TextOptions.GetTextFormattingMode(CurrentContext.TextView)), text, properties);
        return new LongLineElement(this, textLine, hidden);
    }

    private static readonly Brush MarkerBackground = CreateBackground();
    private static Brush CreateBackground() { var brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)); brush.Freeze(); return brush; }

    private sealed class LongLineElement(LongLineElementGenerator owner, TextLine text, int documentLength) : FormattedTextElement(text, documentLength)
    {
        protected override void OnQueryCursor(QueryCursorEventArgs e) { e.Cursor = Cursors.Hand; e.Handled = true; }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.Handled) return;
            e.Handled = true;
            owner.ShowFull = true;
        }
    }
}
