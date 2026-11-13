using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class SqlEditingCommandChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var query=new QueryView();var editor=(TextEditor)query.FindName("Editor");var host=new Window{Content=query,Width=900,Height=650,ShowInTaskbar=false};host.Show();
        try
        {
            editor.Text="SELECT 'ação';\nSELECT 2;";editor.SelectAll();editor.Focus();await host.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            SqlEditorCommands.Comment.Execute(null,query);check(editor.Text=="--SELECT 'ação';\n--SELECT 2;" && query.IsDirty && !query.HasSession,"Routed comment edits exactly once without connecting or executing SQL");
            SqlEditorCommands.Uncomment.Execute(null,query);check(editor.Text=="SELECT 'ação';\nSELECT 2;","Routed uncomment restores both lines");
            check(query.ProcessCommentKey(Key.K,ModifierKeys.Control,1000) && query.ProcessCommentKey(Key.C,ModifierKeys.Control,1100) && editor.Text.StartsWith("--"),"Control K then Control C comments focused editor");
            check(query.ProcessCommentKey(Key.K,ModifierKeys.Control,1200) && query.ProcessCommentKey(Key.U,ModifierKeys.Control,1300) && editor.Text.StartsWith("SELECT"),"Control K then Control U uncomments focused editor");
            query.ProcessCommentKey(Key.K,ModifierKeys.Control,2000);check(!query.ProcessCommentKey(Key.C,ModifierKeys.Control,5001) && editor.Text.StartsWith("SELECT"),"Expired prefix leaves ordinary copy shortcut untouched");
            query.ProcessCommentKey(Key.K,ModifierKeys.Control,6000);check(!query.ProcessCommentKey(Key.A,ModifierKeys.None,6100) && !query.ProcessCommentKey(Key.C,ModifierKeys.Control,6200),"Unrelated key cancels prefix without swallowing input");
            query.ProcessCommentKey(Key.K,ModifierKeys.Control,7000);check(query.ProcessCommentKey(Key.Escape,ModifierKeys.None,7100) && !query.ProcessCommentKey(Key.C,ModifierKeys.Control,7200),"Escape clears comment chord");
            query.ProcessCommentKey(Key.K,ModifierKeys.Control,8000);query.Replace();await host.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(!query.ProcessCommentKey(Key.C,ModifierKeys.Control,8100) && editor.Text.StartsWith("SELECT"),"Chord cannot act from Replace text field");
            editor.IsReadOnly=true;check(!SqlEditorCommands.Comment.CanExecute(null,query),"Comment command is disabled for readonly editor");editor.IsReadOnly=false;
        }
        finally{host.Close();host.Content=null;await query.DisposeAsync();}
        var shell=new MainWindow{ShowInTaskbar=false};shell.Show();
        QueryView? active=null;
        try
        {
            await shell.RecoveryCompletion;ApplicationCommands.New.Execute(null,shell);
            active=(QueryView)((DockingManager)shell.FindName("DockManager")).ActiveContent;var text=(TextEditor)active.FindName("Editor");text.Text="SELECT 3;";
            var wrap=(MenuItem)shell.FindName("WordWrapMenu");wrap.IsChecked=true;wrap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));check(active.WordWrap,"View menu wraps active query");wrap.IsChecked=false;wrap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));check(!active.WordWrap,"View menu restores unwrapped active query");
            var whitespace=(MenuItem)shell.FindName("WhiteSpaceMenu");string beforeWhite=text.Text;active.IsDirty=false;long revision=active.EditRevision;
            whitespace.IsChecked=true;whitespace.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(active.ShowWhiteSpace && text.Options.ShowTabs && text.Options.ShowSpaces,"View menu enables spaces and tabs in active query");
            check(!active.IsDirty && active.EditRevision==revision && text.Text==beforeWhite,"Whitespace display does not modify SQL or dirty state");
            whitespace.IsChecked=false;whitespace.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));check(!active.ShowWhiteSpace,"View menu restores normal whitespace rendering");
            var comment=(MenuItem)shell.FindName("CommentSelectionMenu");var uncomment=(MenuItem)shell.FindName("UncommentSelectionMenu");
            check(comment.Command==SqlEditorCommands.Comment && comment.InputGestureText=="Ctrl+K, Ctrl+C" && uncomment.Command==SqlEditorCommands.Uncomment,"Edit menu exposes real comment commands and shortcut labels");
            SqlEditorCommands.Comment.Execute(null,shell);check(text.Text=="--SELECT 3;","Shell command targets the active query exactly once");
            SqlEditorCommands.Uncomment.Execute(null,shell);check(text.Text=="SELECT 3;","Shell uncomment targets active query");
            var edit=(MenuItem)comment.Parent;edit.IsSubmenuOpen=true;
            await shell.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var panel=(FrameworkElement)VisualTreeHelper.GetParent(comment);panel.UpdateLayout();
            check(comment.ActualWidth>0 && uncomment.ActualWidth>0,"Native Edit menu lays out both comment actions");
            check(comment.IsEnabled && uncomment.IsEnabled,"Native comment actions remain enabled for active query");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32);var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var bounds=new Rect(0,0,panel.ActualWidth,panel.ActualHeight);dc.DrawRectangle(SystemColors.MenuBrush,null,bounds);dc.DrawRectangle(new VisualBrush(panel),null,bounds);}bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using(var file=File.Create("artifacts/review/m867-edit-menu.png"))encoder.Save(file);edit.IsSubmenuOpen=false;
        }
        finally{if(active is not null)active.IsDirty=false;shell.Close();await shell.CloseCompletion;}
    }
}

