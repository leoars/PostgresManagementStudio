using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

/// <summary>Errors in the editor (PGMS-077, M1081): the position the server reported for the last error is underlined with a red wavy line,
/// counted in the editor bar and reached by Go To Next/Previous Issue in File (Alt+PgDn/Alt+PgUp). The editor does not analyse SQL by itself,
/// so only errors of an execution, plan request or unbound parameter appear; editing the text clears them, as Go to Error already did.</summary>
public partial class QueryView
{
    private IssueRenderer? issueRenderer;
    /// <summary>The underlined range of the current issue, or null.</summary>
    internal (int Start, int Length)? IssueRange { get; private set; }
    internal bool HasIssue => IssueRange is not null;

    private void ConfigureIssues()
    {
        issueRenderer = new IssueRenderer(this);
        Editor.TextArea.TextView.BackgroundRenderers.Add(issueRenderer);
        ErrorLocation.IsEnabledChanged += (_, _) => UpdateIssue();
        UpdateIssue();
    }

    private void UpdateIssue()
    {
        var document = Editor.Document;
        if (ErrorLocation.IsEnabled && errorOffset is int offset && errorRevision == EditRevision && offset <= document.TextLength && document.TextLength > 0)
        {
            int start = Math.Min(offset, document.TextLength - 1);
            if (start == offset && start < document.TextLength && char.IsWhiteSpace(document.GetCharAt(start)) && start > 0) start--;
            static bool Word(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
            int end = start + 1;
            if (Word(document.GetCharAt(start))) while (end < document.TextLength && Word(document.GetCharAt(end))) end++;
            IssueRange = (start, end - start);
        }
        else IssueRange = null;
        IssuesButton.Visibility = IssueRange is null ? Visibility.Collapsed : Visibility.Visible;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Go To Next/Previous Issue in File: there is at most one issue, so both go to it.</summary>
    internal void GoToIssue()
    {
        if (IssueRange is not { } range) { QueryStatus.Text = "No issues found."; return; }
        Editor.Select(range.Start, 0); Editor.CaretOffset = range.Start;
        var location = Editor.Document.GetLocation(range.Start);
        Editor.ScrollTo(location.Line, location.Column); Editor.Focus();
    }

    /// <summary>Tests report a server error position without a connection.</summary>
    internal void ReportIssueForTests(string sql, int position, int selectionStart) { SetErrorPosition(sql, position, selectionStart, EditRevision); }

    private void IssuesClicked(object sender, RoutedEventArgs e) => GoToIssue();

    private sealed class IssueRenderer(QueryView view) : IBackgroundRenderer
    {
        private static readonly Pen Wave = CreatePen();
        private static Pen CreatePen() { var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)), 1); pen.Freeze(); return pen; }
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (view.IssueRange is not { } range || range.Start + range.Length > textView.Document.TextLength) return;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = range.Start, Length = range.Length }))
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    double y = rect.Bottom - 1, x = rect.Left;
                    context.BeginFigure(new Point(x, y), false, false);
                    for (bool up = true; x < rect.Right; up = !up) { x = Math.Min(x + 2, rect.Right); context.LineTo(new Point(x, up ? y - 2 : y), true, false); }
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(null, Wave, geometry);
            }
        }
    }
}
