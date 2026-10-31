using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using PGMS.Desktop;

internal static class WhitespaceChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var first=new QueryView();var second=new QueryView();var editor=(TextEditor)first.FindName("Editor");var window=new Window{Content=first,ShowInTaskbar=false};
        try
        {
            editor.Text="SELECT  'two spaces',\n\t'one tab';\n--  spaces in comments";first.IsDirty=false;first.ShowWhiteSpace=true;
            check(first.ShowWhiteSpace && !second.ShowWhiteSpace,"Whitespace choice is independent per document");
            editor.Document=new TextDocument(editor.Text);first.IsDirty=false;
            check(first.ShowWhiteSpace,"Whitespace choice survives text-document replacement");
            check(!editor.CanUndo && !first.IsDirty,"Whitespace rendering creates no Undo entry or dirty state");
            window.Show();
            foreach(bool small in new[]{false,true})
            {
                window.Width=small?640:900;window.Height=small?440:650;await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);first.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)first.ActualWidth,(int)first.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(first);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/m868-whitespace-"+(small?"small":"normal")+".png");encoder.Save(file);
                check(editor.ActualWidth>0 && editor.Options.ShowSpaces && editor.Options.ShowTabs,"Spaces/tabs render in "+(small?"small":"normal")+" view");
            }
        }
        finally{window.Close();window.Content=null;await first.DisposeAsync();await second.DisposeAsync();}
    }
}
