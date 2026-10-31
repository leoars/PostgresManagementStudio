using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using PGMS.Desktop;

internal static partial class SqlLinkChecks
{
    private static async Task RenderingAsync(Action<bool,string> check)
    {
        await using var view=new QueryView(Guid.NewGuid(),_=>throw new Exception("No clipboard expected"));
        var editor=(TextEditor)view.FindName("Editor");
        const string text="-- https://example.invalid/path,\r\n-- www.example.invalid\r\n-- test@example.test\r\nSELECT 'ação 漢字 😀';";
        editor.Text=text;
        var window=new Window{Content=view,Width=1100,Height=700,ShowInTaskbar=false,Opacity=0};
        try {
            window.Show();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            VisualLineLinkText[] Links(){editor.TextArea.TextView.EnsureVisualLines();return editor.TextArea.TextView.VisualLines.SelectMany(l=>l.Elements).OfType<VisualLineLinkText>().ToArray();}
            var links=Links();
            check(links.Length==3,"Rendered editor retains URL www and email native elements");
            check(links.Count(l=>l.NavigateUri.Scheme=="mailto")==1 && links.Count(l=>l.NavigateUri.Scheme is "http" or "https")==2,"Rendered links retain native URI schemes without opening a browser");
            check(links.All(l=>l.RequireControlModifierForClick),"Rendered native elements retain default Ctrl requirement");
            editor.Options.RequireControlModifierForHyperlinkClick=false;
            editor.TextArea.TextView.Redraw();window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            check(Links().All(l=>!l.RequireControlModifierForClick),"Rendered elements observe changed modifier policy");
            editor.Select(3,5);editor.SelectedText="https";editor.Undo();
            check(editor.Text==text && view.IsDirty,"Link rendering preserves Unicode text and native edit Undo");
            editor.Select(text.IndexOf("SELECT",StringComparison.Ordinal),6);
            check(editor.SelectedText=="SELECT","Links do not change selection offsets");
            check(editor.SyntaxHighlighting is not null && editor.FontFamily.Source=="Consolas","SQL highlighting and editor typography remain configured");
            foreach(var size in new[]{(1100d,700d,"normal"),(600d,420d,"minimum")}) {
                window.Width=size.Item1;window.Height=size.Item2;window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                check(view.ActualWidth>0 && editor.ActualWidth>0,"Editor remains arranged at "+size.Item3);
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(view);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");
                using var stream=File.Create("artifacts/review/m621-sql-links-"+size.Item3+".png");encoder.Save(stream);
            }
        }finally {window.Close();}
    }
}
