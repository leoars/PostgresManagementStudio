using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using PGMS.Desktop;

internal static class DollarHighlightingChecks
{
    internal static async Task RunAsync(QueryView view,TextEditor editor,Action<bool,string> check)
    {
        var highlighter=(IHighlighter)editor.TextArea.TextView.Services.GetService(typeof(IHighlighter))!;
        check(highlighter is SqlDocumentHighlighter,"Real SQL editor installs a document-owned PostgreSQL highlighter");
        foreach(var sample in new[]{
            ("SELECT $$ marker\n' /* $$; SELECT 1;","marker","String","Keyword"),
            ("SELECT $tag$ -- marker /* ' $other$\n$TAG$ $tag$; SELECT 1;","marker","String","Keyword"),
            ("SELECT $é_1$ marker $é_1$; SELECT 1;","marker","String","Keyword"),
            ("SELECT $a$ marker $A$; SELECT 1;","marker","String","String"),
            ("SELECT name$tag$; SELECT 1;","name","","Keyword"),
            ("SELECT $1; SELECT 1;","$1","","Keyword"),
            ("-- $tag$ marker\nSELECT 1;","marker","Comment","Keyword"),
            ("SELECT '$tag$ marker'; SELECT 1;","marker","String","Keyword")
        })
        {
            editor.Text=sample.Item1;
            check((HighlightingChecks.ColorAt(highlighter,editor.Text.IndexOf(sample.Item2,StringComparison.Ordinal))??"")==sample.Item3,"Dollar quoting preserves lexical boundary: "+sample.Item1.Replace("\n","\\n"));
            check(HighlightingChecks.ColorAt(highlighter,editor.Text.LastIndexOf("SELECT",StringComparison.Ordinal))==sample.Item4,"Dollar delimiter controls following SQL state");
        }
        editor.Text="SELECT $a$ body\n$a$; SELECT 1;";
        check(highlighter.GetColorStack(1).Single().Name=="String","Dollar string state persists between lines");
        int closer=editor.Text.LastIndexOf("$a$",StringComparison.Ordinal);editor.Document.Replace(closer,3,"$b$");
        check(HighlightingChecks.ColorAt(highlighter,editor.Text.LastIndexOf("SELECT",StringComparison.Ordinal))=="String","Changing closing tag invalidates following color state");
        editor.Undo();
        check(HighlightingChecks.ColorAt(highlighter,editor.Text.LastIndexOf("SELECT",StringComparison.Ordinal))=="Keyword","Undo restores exact dollar delimiter and following SQL");
        editor.Text="/* outer /* nested */ still comment */\nSELECT E'escaped \\' quote', $body$\n  -- literal body; $other$ is not the closer\n$body$;\nSELECT 'ação 密碼', 42;";
        var window=new Window{Content=view,Width=900,Height=650,ShowInTaskbar=false};window.Show();
        try
        {
            foreach(bool small in new[]{false,true})
            {
                window.Width=small?640:900;window.Height=small?440:650;
                await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);view.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/m864-highlighting-"+(small?"small":"normal")+".png");encoder.Save(file);
                check(editor.ActualWidth>0 && editor.ActualHeight>0,"SQL lexical rendering fits "+(small?"small":"normal")+" view");
            }
        }
        finally{window.Close();window.Content=null;}
        var old=highlighter;editor.Document=new TextDocument("SELECT 2;");
        var replacement=(IHighlighter)editor.TextArea.TextView.Services.GetService(typeof(IHighlighter))!;
        bool disposed=false;try{old.HighlightLine(1);}catch(ObjectDisposedException){disposed=true;}
        check(disposed && replacement is SqlDocumentHighlighter && !ReferenceEquals(old,replacement),"Document replacement disposes old lexical cache and installs independent state");
        check(HighlightingChecks.ColorAt(replacement,0)=="Keyword","Replacement document begins in SQL state");
    }
}
