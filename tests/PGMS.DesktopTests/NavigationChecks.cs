using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;

internal static class NavigationChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var view=new QueryView();var window=new Window{Content=view,Width=900,Height=650,ShowInTaskbar=false};window.Show();
        try
        {
            view.Editor.Text="SELECT 1;\r\n-- Unicode á😀\nSELECT 2;\r";view.IsDirty=false;view.Editor.Document.UndoStack.ClearAll();
            QueryView.GoToLineCommand.Execute(null,view);await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var line=(TextBox)view.FindName("LineNumber");var bar=(FrameworkElement)view.FindName("NavigationBar");var go=(Button)view.FindName("NavigateLineButton");
            check(bar.IsVisible && line.IsKeyboardFocused,"Go To Line command opens inline panel and focuses line number");
            int original=view.Editor.CaretOffset;
            foreach(string invalid in new[]{"0","-1","5","2147483648","text",""})
            {line.Text=invalid;go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(view.Editor.CaretOffset==original && bar.IsVisible,"Invalid navigation keeps caret and panel: "+invalid);}
            line.Text="3";go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(view.Editor.TextArea.Caret.Line==3 && view.Editor.TextArea.Caret.Column==1 && !bar.IsVisible,"Go To Line selects exact logical line across CRLF LF and CR");
            check(!view.IsDirty && !view.Editor.CanUndo && !view.HasSession,"Navigation changes no SQL Undo dirty state or connection");
            view.GoToLine();line.Text="4";go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(view.Editor.CaretOffset==view.Editor.Document.TextLength,"Navigation accepts final empty logical line");
            view.GoToLine();view.Replace();check(!bar.IsVisible,"Replace closes navigation panel");view.GoToLine();view.Find();check(!bar.IsVisible,"Find closes navigation panel");
            string text="SELECT '"+string.Concat(Enumerable.Repeat("word ",50))+"';";view.Editor.Text=text;view.IsDirty=false;view.Editor.Document.UndoStack.ClearAll();view.WordWrap=true;
            check(view.WordWrap && view.Editor.Document.LineCount==1 && view.Editor.Text==text,"Word Wrap changes visual layout without adding logical newlines");
            check(!view.IsDirty && !view.Editor.CanUndo,"Word Wrap preserves Undo and dirty state");
            {var separate=new QueryView();check(!separate.WordWrap,"Word Wrap is independent per query");await separate.DisposeAsync();}
            await ResultExportUiChecks.Capture(window,view,"m897-wrap-normal",900,650,check);await ResultExportUiChecks.Capture(window,view,"m897-wrap-small",600,440,check);
            check(view.Editor.TextArea.TextView.VisualLines.First().TextLines.Count>1,"Long logical SQL line wraps into native visual text lines");
            view.WordWrap=false;check(view.Editor.Text==text && !view.WordWrap,"Disabling Word Wrap restores unwrapped presentation without text edits");
            view.GoToLine();await ResultExportUiChecks.Capture(window,view,"m896-navigation-normal",900,650,check);await ResultExportUiChecks.Capture(window,view,"m896-navigation-small",600,440,check);
            var bounds=bar.TransformToAncestor(view).TransformBounds(new Rect(bar.RenderSize));check(bounds.Right<=view.ActualWidth && bounds.Bottom<view.ActualHeight,"Navigation fits minimum query viewport");
        }
        finally{window.Close();await view.DisposeAsync();}
    }
}
