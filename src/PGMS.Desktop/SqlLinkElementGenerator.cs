using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Rendering;

namespace PGMS.Desktop;

internal sealed class SqlLinkElementGenerator(bool mail) : LinkElementGenerator(mail ? SqlLinkPatterns.Mail : SqlLinkPatterns.Url)
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        int endOffset = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        var segment = CurrentContext.GetText(startOffset, endOffset - startOffset);
        if (!SqlLinkPatterns.CouldContainLink(segment.Text.AsSpan(segment.Offset, segment.Count), mail)) return -1;
        return base.GetFirstInterestedOffset(startOffset);
    }

    protected override Uri? GetUriFromMatch(Match match)
    {
        if (!mail) return base.GetUriFromMatch(match);
        string target = "mailto:" + match.Value;
        return Uri.IsWellFormedUriString(target, UriKind.Absolute) ? new Uri(target) : null;
    }
}
