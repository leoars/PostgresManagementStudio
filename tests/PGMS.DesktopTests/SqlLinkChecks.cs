using System.Reflection;
using System.Text.RegularExpressions;
using PGMS.Desktop;
using ICSharpCode.AvalonEdit.Rendering;

internal static partial class SqlLinkChecks
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        string[] corpus=["", "SELECT 1;", "https://example.invalid/a?q=1#b", "http://a.invalid/.", "ftp://a.invalid/f", "www.example.invalid", "HTTPS://EXAMPLE.invalid", "a@example.invalid", "ação+tag@example.test", "x@a.ab", "x@a.abcdefg", "(https://a.invalid), a@b.test;", "漢字 www.a.test😀", "prefixhttps://a.test", "x\r\nwww.a.test\r\ny@b.test", new string('x',1024)];
        foreach(bool mail in new[]{false,true}) {
            var original=(Regex)typeof(LinkElementGenerator).GetField(mail?"defaultMailRegex":"defaultLinkRegex",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            var linear=mail?SqlLinkPatterns.Mail:SqlLinkPatterns.Url;
            foreach(string text in corpus)
                check(Signature(original,text).SequenceEqual(Signature(linear,text)),"Linear "+(mail?"email":"URL")+" preserves corpus match offsets/length/values");
            var random=new Random(603);string alphabet="abcXYZ019.:/@_- ?&=漢é";
            bool equivalent=true;
            for(int i=0;i<1000;i++) {
                string text=new(Enumerable.Range(0,random.Next(0,180)).Select(_=>alphabet[random.Next(alphabet.Length)]).ToArray());
                equivalent &= Signature(original,text).SequenceEqual(Signature(linear,text));
            }
            check(equivalent,"Linear "+(mail?"email":"URL")+" matches pinned-library regex across1000 deterministic inputs");
            check(!linear.IsMatch(new string('.',262144)),"Linear detector rejects large punctuation-only text");
            check(!linear.IsMatch(new string('x',262144)),"Linear detector rejects large identifier-only text");
            check(corpus.All(t=>!original.IsMatch(t) || SqlLinkPatterns.CouldContainLink(t.AsSpan(),mail)),"Necessary-character prefilter never rejects a matched corpus link");
            check(!SqlLinkPatterns.CouldContainLink(new string('x',262144).AsSpan(),mail),"Prefilter rejects identifier-only line before regex");
            check(SqlLinkPatterns.CouldContainLink((mail?"a@b.test":"https://a.test").AsSpan(),mail),"Prefilter admits a real link candidate");
            check(linear.Options.HasFlag(RegexOptions.NonBacktracking),"Detector uses non-backtracking engine");
        }
        await OptionsAsync(check);
        await RenderingAsync(check);
    }
    private static IEnumerable<string> Signature(Regex regex,string text)=>regex.Matches(text).Select(m=>$"{m.Index}:{m.Length}:{m.Value}");
}
