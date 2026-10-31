using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using PGMS.Desktop;

internal static class HighlightingChecks
{
    internal static string? ColorAt(IHighlighter highlighter,int offset)
    {
        var line=highlighter.Document.GetLineByOffset(offset);
        return highlighter.HighlightLine(line.LineNumber).Sections.LastOrDefault(s=>offset>=s.Offset && offset<s.Offset+s.Length)?.Color.Name;
    }
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");
        try
        {
            editor.Text="/* outer /* inner */ SELECT\nstill comment */ SELECT 1;";
            using var highlighter=new SqlDocumentHighlighter(editor.Document,editor.SyntaxHighlighting);
            check(ColorAt(highlighter,editor.Text.IndexOf("SELECT",StringComparison.Ordinal))=="Comment","Nested block comment does not terminate at inner delimiter");
            check(ColorAt(highlighter,editor.Text.LastIndexOf("SELECT",StringComparison.Ordinal))=="Keyword","Keyword coloring resumes after outer block comment");
            editor.Text="/* one\n/* two\n/* three */ still */ more\nend */ SELECT 1;";
            check(ColorAt(highlighter,editor.Text.IndexOf("more",StringComparison.Ordinal))=="Comment","Three levels of multiline block comments retain outer state");
            check(ColorAt(highlighter,editor.Text.IndexOf("SELECT",StringComparison.Ordinal))=="Keyword","Multiline nested comments return to SQL after all closers");
            editor.Text="-- /* not a block\nSELECT '/* literal */';";
            check(ColorAt(highlighter,editor.Text.IndexOf("SELECT",StringComparison.Ordinal))=="Keyword","Line comment nesting markers do not leak across newline");
            check(ColorAt(highlighter,editor.Text.IndexOf("literal",StringComparison.Ordinal))=="String","Comment markers in a string stay literal");
            editor.Text="/* unfinished /* inner */\nSELECT";
            check(ColorAt(highlighter,editor.Text.LastIndexOf("SELECT",StringComparison.Ordinal))=="Comment","Unclosed outer comment continues at end of document");
            editor.Text="/* outer /* inner */ SELECT */ SELECT";
            int inner=editor.Text.IndexOf("/* inner",StringComparison.Ordinal);editor.Document.Remove(inner,2);
            check(ColorAt(highlighter,editor.Text.IndexOf("SELECT",StringComparison.Ordinal))=="Keyword","Removing nested opener updates subsequent highlighting");
            editor.Undo();
            check(ColorAt(highlighter,editor.Text.IndexOf("SELECT",StringComparison.Ordinal))=="Comment","Undo restores nested comment state without rewriting SQL");
            foreach(var sample in new[]{
                ("SELECT E'escaped \\' marker'; SELECT 1;","marker","String"),
                ("SELECT e'escaped \\' marker'; SELECT 1;","marker","String"),
                ("SELECT E'escaped \\\\'; SELECT 1;","SELECT 1","Keyword"),
                ("SELECT E'first\n\\' marker'; SELECT 1;","marker","String"),
                ("SELECT E'a'' marker'; SELECT 1;","marker","String"),
                ("SELECT 'a'' marker'; SELECT 1;","marker","String"),
                ("SELECT 'ordinary \\'; SELECT 1;","SELECT 1","Keyword"),
                ("SELECT \"quoted\"\" marker\"; SELECT 1;","marker","String"),
                ("""SELECT "quoted \"; SELECT 1;""","SELECT 1","Keyword"),
                ("SELECT E'-- /* marker'; SELECT 1;","marker","String")
            })
            {
                editor.Text=sample.Item1;
                check(ColorAt(highlighter,editor.Text.IndexOf(sample.Item2,StringComparison.Ordinal))==sample.Item3,"PostgreSQL quote/escape boundary: "+sample.Item1.Replace("\n","\\n"));
                check(ColorAt(highlighter,editor.Text.LastIndexOf("SELECT",StringComparison.Ordinal))=="Keyword","SQL coloring resumes after quoted content");
            }
            await SqlLineCommentChecks.RunAsync(check);
            await SqlEditingCommandChecks.RunAsync(check);
            await WhitespaceChecks.RunAsync(check);
            await NavigationChecks.RunAsync(check);
            HighlightingCacheChecks.Run(editor,check);
            await DollarHighlightingChecks.RunAsync(view,editor,check);
        }
        finally{await view.DisposeAsync();}
    }
}
