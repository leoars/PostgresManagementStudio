using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>PGMS-077 (M1081): the error the server reported is underlined, counted in the editor bar and reached by Go To Next/Previous Issue in File.</summary>
internal static class IssueChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");var button=(Button)view.FindName("IssuesButton");
        try
        {
            editor.Text="select * from missing_table";
            check(!view.HasIssue && button.Visibility==Visibility.Collapsed,"Without an error there is no issue and no counter");
            view.ReportIssueForTests("select * from missing_table",15,0);
            check(view.IssueRange==(14,13) && button.Visibility==Visibility.Visible,"The server position (character 15) underlines the whole word missing_table and shows the counter");
            editor.CaretOffset=0;check(SqlBookmarkCommands.NextIssue.CanExecute(null,editor),"Go To Next Issue in File is available with an issue");
            SqlBookmarkCommands.NextIssue.Execute(null,editor);check(editor.CaretOffset==14,"Go To Next Issue in File moves the caret to the error");
            editor.CaretOffset=0;view.ProcessEditKey(System.Windows.Input.Key.PageUp,System.Windows.Input.ModifierKeys.Alt,0);
            check(!editor.IsKeyboardFocusWithin || editor.CaretOffset==14,"Alt+PgUp goes to it too");
            editor.Document.Insert(0," ");check(!view.HasIssue && button.Visibility==Visibility.Collapsed,"Editing the text clears the issue (the position no longer matches)");
            editor.Text="select 1 +";view.ReportIssueForTests("select 1 +",11,0);check(view.IssueRange==(9,1),"A position at the end marks the last character");
            view.GoToIssue();check(editor.CaretOffset==9,"Go To Issue at the end of the text");
            editor.Text="x";check(!SqlBookmarkCommands.NextIssue.CanExecute(null,editor),"No issue: the command is unavailable");
            view.GoToIssue();check(((TextBlock)view.FindName("QueryStatus")).Text=="No issues found.","Without an issue the tab says No issues found");
        }
        finally{await view.DisposeAsync();}
        var shell=new MainWindow{ShowInTaskbar=false};shell.Show();await shell.RecoveryCompletion;
        try
        {
            var edit=(MenuItem)shell.FindName("EditMenu");var names=edit.Items.OfType<MenuItem>().Select(i=>i.Header as string).ToList();
            int line=names.IndexOf("Go To _Line…");
            check(line>=0 && names[line+1]=="Go To Next Issue in File" && names[line+2]=="Go To Previous Issue in File" && ((MenuItem)shell.FindName("NextIssueMenu")).InputGestureText=="Alt+PgDn","Edit lists Go To Next/Previous Issue in File after Go To Line, with the SSMS shortcuts");
        }
        finally{shell.Close();await shell.CloseCompletion;}
    }
}
