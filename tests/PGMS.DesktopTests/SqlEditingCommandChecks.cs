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
            editor.IsReadOnly=true;check(!SqlEditorCommands.Comment.CanExecute(null,query),"Comment command is disabled for readonly editor");
            check(!SqlEditorCommands.SortLines.CanExecute(null,query) && !SqlEditorCommands.Duplicate.CanExecute(null,query),"Advanced text commands are disabled for readonly editor");editor.IsReadOnly=false;
            // SSMS 22.11 shortcuts (PGMS-056)
            editor.Text="select a\nfrom t";editor.Focus();editor.Select(0,0);await host.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check(query.ProcessEditKey(Key.OemQuestion,ModifierKeys.Control,100) && editor.Text=="--select a\nfrom t","Control slash toggles the line comment");
            check(query.ProcessEditKey(Key.AbntC1,ModifierKeys.Control,110) && editor.Text=="select a\nfrom t","ABNT2 slash key toggles it back");
            editor.Select(7,1);check(query.ProcessEditKey(Key.OemQuestion,ModifierKeys.Control|ModifierKeys.Shift,120) && editor.Text=="select /*a*/\nfrom t","Control Shift slash toggles a block comment");editor.Undo();
            editor.Select(0,6);check(query.ProcessEditKey(Key.U,ModifierKeys.Control|ModifierKeys.Shift,130) && editor.Text.StartsWith("SELECT"),"Control Shift U makes uppercase");
            check(query.ProcessEditKey(Key.L,ModifierKeys.Control|ModifierKeys.Shift,140) && editor.Text.StartsWith("select"),"Control Shift L makes lowercase");
            editor.Select(0,0);check(query.ProcessEditKey(Key.Down,ModifierKeys.Alt,150) && editor.Text=="from t\nselect a","Alt Down moves the line");
            check(query.ProcessEditKey(Key.Up,ModifierKeys.Alt,160) && editor.Text=="select a\nfrom t","Alt Up moves it back");
            editor.SelectAll();check(query.ProcessEditKey(Key.L,ModifierKeys.Shift|ModifierKeys.Alt,200) && query.ProcessEditKey(Key.S,ModifierKeys.Shift|ModifierKeys.Alt,300) && editor.Text=="from t\nselect a","Shift Alt L then S sorts lines");
            editor.SelectAll();check(query.ProcessEditKey(Key.L,ModifierKeys.Shift|ModifierKeys.Alt,400) && query.ProcessEditKey(Key.J,ModifierKeys.Shift|ModifierKeys.Alt,500) && editor.Text=="from t select a","Shift Alt L then J joins lines");
            query.ProcessEditKey(Key.L,ModifierKeys.Shift|ModifierKeys.Alt,600);check(!query.ProcessEditKey(Key.S,ModifierKeys.Shift|ModifierKeys.Alt,3000),"Expired Shift Alt L chord does nothing");
            editor.Text="x    =  1";editor.Select(3,0);
            check(query.ProcessCommentKey(Key.K,ModifierKeys.Control,4000) && query.ProcessCommentKey(Key.Oem5,ModifierKeys.Control,4100) && editor.Text=="x =  1","Control K then Control backslash deletes horizontal white space");
            check(!query.ProcessEditKey(Key.D,ModifierKeys.Control,4200) && editor.Text=="x =  1","Control D is not taken by Duplicate (Results to Grid in SSMS)");
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
            var advanced=(MenuItem)shell.FindName("AdvancedEditMenu");var edit=(MenuItem)shell.FindName("EditMenu");
            check(comment.Parent==advanced && uncomment.Parent==advanced,"Comment and Uncomment Selection live in Edit > Advanced, as in SSMS");
            string[] expected=["Toggle Spell Checker","Format SQL","Tabify Selected Lines","Untabify Selected Lines","Make Uppercase","Make Lowercase","Move Selected Lines Up","Move Selected Lines Down","Delete Horizontal White Space","Expand Selection to Containing Block","Expand Selection","View White Space","Word Wrap","Incremental Search","Toggle Line Comment","Toggle Block Comment","Sort Lines","Join Lines","Next subword","Previous subword","Expand Selection to Line","Comment Selection","Uncomment Selection","Increase Line Indent","Decrease Line Indent","Set End of Line Sequence","Set Indentation","Set Encoding"];
            check(advanced.Items.OfType<MenuItem>().Select(i=>(string)i.Header).SequenceEqual(expected),"Advanced submenu follows the SSMS 22.11 order without the commands left for other items");
            check(((MenuItem)shell.FindName("ToggleLineCommentMenu")).InputGestureText=="Ctrl+/" && ((MenuItem)shell.FindName("MakeLowercaseMenu")).InputGestureText=="Ctrl+Shift+L" && ((MenuItem)shell.FindName("SortLinesMenu")).InputGestureText=="Shift+Alt+L, Shift+Alt+S","Advanced items show the SSMS shortcuts");
            text.Text="b\na";text.SelectAll();SqlEditorCommands.SortLines.Execute(null,shell);check(text.Text=="a\nb","Shell routes Sort Lines to the active query");
            text.Select(0,0);SqlEditorCommands.Duplicate.Execute(null,shell);check(text.Text=="a\na\nb","Edit > Duplicate routes to the active query");
            check(((MenuItem)shell.FindName("FormatSqlMenu")).InputGestureText=="Ctrl+K, Ctrl+Q","Format SQL shows the SSMS shortcut");
            text.Text="select 1 from t";SqlEditorCommands.FormatSql.Execute(null,shell);check(text.Text.ReplaceLineEndings("\n")=="SELECT 1\nFROM   t","Shell routes Format SQL to the active query => "+text.Text.Replace("\r","\\r").Replace("\n","\\n"));
            var advancedWrap=(MenuItem)shell.FindName("AdvancedWordWrapMenu");advancedWrap.IsChecked=true;advancedWrap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));check(active.WordWrap,"Advanced Word Wrap wraps the active query");
            advancedWrap.IsChecked=false;advancedWrap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));check(!active.WordWrap,"Advanced Word Wrap unwraps it");
            string insertPath=Path.Combine(Path.GetTempPath(),"pgms-insert-"+Guid.NewGuid().ToString("N")+".sql");File.WriteAllText(insertPath,"-- ação\nSELECT 9;",new System.Text.UTF8Encoding(true));
            try
            {
                text.Text="X\nZ";text.CaretOffset=1;shell.PickInsertFile=()=>insertPath;
                ((MenuItem)shell.FindName("InsertFileAsTextMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                for(int i=0;i<100 && text.Text=="X\nZ";i++)await Task.Delay(20);
                check(text.Text=="X-- ação\nSELECT 9;\nZ" && !active.HasSession,"Insert File As Text inserts the decoded file (BOM dropped) at the caret without executing");
                shell.PickInsertFile=()=>null;text.Text="Y";((MenuItem)shell.FindName("InsertFileAsTextMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Task.Delay(100);check(text.Text=="Y","Cancelled Insert File changes nothing");
            }
            finally{File.Delete(insertPath);}
            edit.IsSubmenuOpen=true;await shell.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            advanced.IsSubmenuOpen=true;await shell.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var panel=(FrameworkElement)VisualTreeHelper.GetParent(comment);panel.UpdateLayout();
            check(comment.ActualWidth>0 && uncomment.ActualWidth>0,"Native Advanced menu lays out both comment actions");
            check(comment.IsEnabled && uncomment.IsEnabled && ((MenuItem)shell.FindName("JoinLinesMenu")).IsEnabled,"Advanced actions remain enabled for active query");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),(int)Math.Ceiling(panel.ActualHeight),96,96,PixelFormats.Pbgra32);var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var bounds=new Rect(0,0,panel.ActualWidth,panel.ActualHeight);dc.DrawRectangle(SystemColors.MenuBrush,null,bounds);dc.DrawRectangle(new VisualBrush(panel),null,bounds);}bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using(var file=File.Create("artifacts/review/m1075-edit-advanced-menu.png"))encoder.Save(file);advanced.IsSubmenuOpen=false;edit.IsSubmenuOpen=false;
        }
        finally{if(active is not null)active.IsDirty=false;shell.Close();await shell.CloseCompletion;}
    }
}

