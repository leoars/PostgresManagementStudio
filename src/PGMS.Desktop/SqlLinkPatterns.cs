using System.Text.RegularExpressions;

namespace PGMS.Desktop;

// Patterns from AvalonEdit 6.3.1 LinkElementGenerator (MIT; see licenses/AvalonEdit.txt).
// Preserve the lexical grammar. Only Index/Length/Value are consumed by the renderer.
internal static class SqlLinkPatterns
{
    internal const string UrlPattern = @"\b(https?://|ftp://|www\.)[\w\d\._/\-~%@()+:?&=#!]*[\w\d/]";
    internal const string MailPattern = @"\b[\w\d\.\-]+\@[\w\d\.\-]+\.[a-z]{2,6}\b";
    internal static bool CouldContainLink(ReadOnlySpan<char> text, bool mail)
        => mail ? text.IndexOf('@') >= 0 : text.IndexOfAny(':', '.') >= 0;
    internal static readonly Regex Url = new(UrlPattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    internal static readonly Regex Mail = new(MailPattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
}
