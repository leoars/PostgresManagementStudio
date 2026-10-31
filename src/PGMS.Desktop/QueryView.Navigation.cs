using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PGMS.Desktop;

public partial class QueryView
{
    public static RoutedUICommand GoToLineCommand{get;}=new("Go To Line","GoToLine",typeof(QueryView));
    public void GoToLine()
    {
        ClearAnalysisReview();HideSessionSettings();ReplacementBar.Hide();search.Close();NavigationBar.Visibility=Visibility.Visible;
        LineNumber.Text=Editor.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture);
        NavigationFeedback.Text=$"Line 1–{Editor.Document.LineCount}. Navigation does not change SQL.";
        Dispatcher.BeginInvoke(DispatcherPriority.Input,()=>{if(NavigationBar.IsVisible){LineNumber.Focus();LineNumber.SelectAll();}});
    }
    private void NavigateLine(object sender,RoutedEventArgs e)
    {
        if(!int.TryParse(LineNumber.Text.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int line) || line<1 || line>Editor.Document.LineCount)
        {NavigationFeedback.Text=$"Enter a line from 1 to {Editor.Document.LineCount}.";LineNumber.Focus();LineNumber.SelectAll();return;}
        Editor.Select(Editor.Document.GetLineByNumber(line).Offset,0);Editor.ScrollTo(line,1);HideNavigation();
    }
    private void HideNavigation(){NavigationBar.Visibility=Visibility.Collapsed;Editor.Focus();Keyboard.Focus(Editor.TextArea);}
    private void CloseNavigation(object sender,RoutedEventArgs e)=>HideNavigation();
    private void NavigationKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter){NavigateLine(sender,e);e.Handled=true;}
        else if(e.Key==Key.Escape){HideNavigation();e.Handled=true;}
    }
}
