using System.Windows.Input;

namespace PGMS.Desktop;

public static class SqlEditorCommands
{
    public static readonly RoutedUICommand Comment=new("Comment Selection",nameof(Comment),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand Uncomment=new("Uncomment Selection",nameof(Uncomment),typeof(SqlEditorCommands));
}

public partial class QueryView
{
    public bool WordWrap{get=>Editor.WordWrap;set{if(Editor.WordWrap==value)return;Editor.WordWrap=value;RecoveryRevision++;}}
    public bool ShowWhiteSpace
    {
        get=>Editor.Options.ShowSpaces && Editor.Options.ShowTabs;
        set{if(ShowWhiteSpace==value && Editor.Options.ShowSpaces==value && Editor.Options.ShowTabs==value)return;Editor.Options.ShowSpaces=value;Editor.Options.ShowTabs=value;RecoveryRevision++;}
    }
    private long commentChordDeadline;
    private void ConfigureSqlEditing()
    {
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Comment,(_,e)=>{CommentSelection(false);e.Handled=true;},(_,e)=>{e.CanExecute=!Editor.IsReadOnly;e.Handled=true;}));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Uncomment,(_,e)=>{CommentSelection(true);e.Handled=true;},(_,e)=>{e.CanExecute=!Editor.IsReadOnly;e.Handled=true;}));
        Editor.PreviewKeyDown+=(_,e)=>{if(ProcessCommentKey(e.Key,Keyboard.Modifiers,Environment.TickCount64))e.Handled=true;};
        Editor.LostKeyboardFocus+=(_,_)=>commentChordDeadline=0;
    }
    internal bool ProcessCommentKey(Key key,ModifierKeys modifiers,long now)
    {
        if(!Editor.IsKeyboardFocusWithin){commentChordDeadline=0;return false;}
        if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)return false;
        if(commentChordDeadline>0)
        {
            bool valid=now<=commentChordDeadline;commentChordDeadline=0;
            if(valid && modifiers==ModifierKeys.Control && key is Key.C or Key.U){CommentSelection(key==Key.U);return true;}
            if(key==Key.Escape && modifiers==ModifierKeys.None)return true;
        }
        if(key==Key.K && modifiers==ModifierKeys.Control){commentChordDeadline=now+2000;return true;}
        return false;
    }
    public void CommentSelection(bool uncomment)
    {
        try
        {
            int changed=SqlLineComments.Apply(Editor,uncomment);
            QueryStatus.Text=changed==0?"No leading line comments to remove.":$"{(uncomment?"Uncommented":"Commented")} {changed:N0} line(s).";
        }
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){QueryStatus.Text=ex.Message;}
        Editor.Focus();
    }
}
