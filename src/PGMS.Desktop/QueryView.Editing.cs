using System.Windows.Input;

namespace PGMS.Desktop;

public static class SqlEditorCommands
{
    public static readonly RoutedUICommand Comment=new("Comment Selection",nameof(Comment),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand Uncomment=new("Uncomment Selection",nameof(Uncomment),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand ToggleLineComment=new("Toggle Line Comment",nameof(ToggleLineComment),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand ToggleBlockComment=new("Toggle Block Comment",nameof(ToggleBlockComment),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand MakeUppercase=new("Make Uppercase",nameof(MakeUppercase),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand MakeLowercase=new("Make Lowercase",nameof(MakeLowercase),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand MoveLinesUp=new("Move Selected Lines Up",nameof(MoveLinesUp),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand MoveLinesDown=new("Move Selected Lines Down",nameof(MoveLinesDown),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand Duplicate=new("Duplicate",nameof(Duplicate),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand SortLines=new("Sort Lines",nameof(SortLines),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand JoinLines=new("Join Lines",nameof(JoinLines),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand DeleteHorizontalWhiteSpace=new("Delete Horizontal White Space",nameof(DeleteHorizontalWhiteSpace),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand Tabify=new("Tabify Selected Lines",nameof(Tabify),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand Untabify=new("Untabify Selected Lines",nameof(Untabify),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand IncreaseIndent=new("Increase Line Indent",nameof(IncreaseIndent),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand DecreaseIndent=new("Decrease Line Indent",nameof(DecreaseIndent),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand FormatSql=new("Format SQL",nameof(FormatSql),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand LineEndingsCrLf=new("CRLF",nameof(LineEndingsCrLf),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand LineEndingsLf=new("LF",nameof(LineEndingsLf),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand LineEndingsCr=new("CR",nameof(LineEndingsCr),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand IndentWithSpaces=new("Spaces",nameof(IndentWithSpaces),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand IndentWithTabs=new("Tabs",nameof(IndentWithTabs),typeof(SqlEditorCommands));
    /// <summary>Set Encoding ▸ Reopen with Encoding / Save with Encoding; handled by the main window (file and save flow).</summary>
    public static readonly RoutedUICommand ReopenWithEncoding=new("Reopen with Encoding",nameof(ReopenWithEncoding),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand SaveWithEncoding=new("Save with Encoding",nameof(SaveWithEncoding),typeof(SqlEditorCommands));
    // PGMS-078 (M1103).
    public static readonly RoutedUICommand ExpandSelection=new("Expand Selection",nameof(ExpandSelection),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand ExpandToContainingBlock=new("Expand Selection to Containing Block",nameof(ExpandToContainingBlock),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand ExpandToLine=new("Expand Selection to Line",nameof(ExpandToLine),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand NextSubword=new("Next subword",nameof(NextSubword),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand PreviousSubword=new("Previous subword",nameof(PreviousSubword),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand IncrementalSearch=new("Incremental Search",nameof(IncrementalSearch),typeof(SqlEditorCommands));
    public static readonly RoutedUICommand ShowClipboardHistory=new("Show Clipboard History",nameof(ShowClipboardHistory),typeof(SqlEditorCommands));
    /// <summary>Handled by the main window: the last edit can be in another tab.</summary>
    public static readonly RoutedUICommand GoToLastEditLocation=new("Go To Last Edit Location",nameof(GoToLastEditLocation),typeof(SqlEditorCommands));

    /// <summary>Selection and caret moves; they also work in a read-only editor.</summary>
    internal static readonly IReadOnlyList<(RoutedUICommand Command,Func<ICSharpCode.AvalonEdit.TextEditor,string?> Run)> SelectionMoves=
    [
        (ExpandSelection,SqlSelectionCommands.ExpandSelection),
        (ExpandToContainingBlock,SqlSelectionCommands.ExpandToContainingBlock),
        (ExpandToLine,SqlSelectionCommands.ExpandToLine),
        (NextSubword,e=>SqlSelectionCommands.MoveSubword(e,true,false)),
        (PreviousSubword,e=>SqlSelectionCommands.MoveSubword(e,false,false)),
    ];

    /// <summary>Text commands of Edit ▸ Advanced (and Edit ▸ Duplicate) with the editor operation each one runs.</summary>
    internal static readonly IReadOnlyList<(RoutedUICommand Command,Func<ICSharpCode.AvalonEdit.TextEditor,string?> Run)> TextEdits=
    [
        (ToggleLineComment,SqlTextCommands.ToggleLineComment),
        (ToggleBlockComment,SqlTextCommands.ToggleBlockComment),
        (MakeUppercase,e=>SqlTextCommands.ChangeCase(e,true)),
        (MakeLowercase,e=>SqlTextCommands.ChangeCase(e,false)),
        (MoveLinesUp,e=>SqlTextCommands.MoveLines(e,true)),
        (MoveLinesDown,e=>SqlTextCommands.MoveLines(e,false)),
        (Duplicate,SqlTextCommands.Duplicate),
        (SortLines,SqlTextCommands.SortLines),
        (JoinLines,SqlTextCommands.JoinLines),
        (DeleteHorizontalWhiteSpace,SqlTextCommands.DeleteHorizontalWhiteSpace),
        (Tabify,e=>SqlTextCommands.Tabify(e,true)),
        (Untabify,e=>SqlTextCommands.Tabify(e,false)),
        (IncreaseIndent,e=>SqlTextCommands.ChangeIndent(e,true)),
        (DecreaseIndent,e=>SqlTextCommands.ChangeIndent(e,false)),
        (FormatSql,SqlTextCommands.FormatSql),
        (LineEndingsCrLf,e=>SqlTextCommands.SetLineEndings(e,"\r\n")),
        (LineEndingsLf,e=>SqlTextCommands.SetLineEndings(e,"\n")),
        (LineEndingsCr,e=>SqlTextCommands.SetLineEndings(e,"\r")),
        (IndentWithSpaces,e=>SqlTextCommands.SetIndentation(e,true)),
        (IndentWithTabs,e=>SqlTextCommands.SetIndentation(e,false)),
    ];
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
    private long lineChordDeadline;
    /// <summary>Shift+Alt+L was pressed in the editor and its second key is still awaited.</summary>
    /// <summary>Ctrl+K was pressed in the editor and its second key is still awaited (Ctrl+K, Ctrl+\ must not start a window chord).</summary>
    internal bool CommentChordPending=>commentChordDeadline>0 && Environment.TickCount64<=commentChordDeadline && Editor.IsKeyboardFocusWithin;
    internal bool LineChordPending=>lineChordDeadline>0 && Environment.TickCount64<=lineChordDeadline && Editor.IsKeyboardFocusWithin;
    private void ConfigureSqlEditing()
    {
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Comment,(_,e)=>{CommentSelection(false);e.Handled=true;},(_,e)=>{e.CanExecute=!Editor.IsReadOnly;e.Handled=true;}));
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.Uncomment,(_,e)=>{CommentSelection(true);e.Handled=true;},(_,e)=>{e.CanExecute=!Editor.IsReadOnly;e.Handled=true;}));
        foreach(var (command,run) in SqlEditorCommands.TextEdits)
            CommandBindings.Add(new CommandBinding(command,(_,e)=>{RunTextEdit(run);e.Handled=true;},(_,e)=>{e.CanExecute=!Editor.IsReadOnly;e.Handled=true;}));
        foreach(var (command,run) in SqlEditorCommands.SelectionMoves)
            CommandBindings.Add(new CommandBinding(command,(_,e)=>{RunTextEdit(run);e.Handled=true;},(_,e)=>{e.CanExecute=true;e.Handled=true;}));
        ConfigureConveniences();
        // AvalonEdit binds Ctrl+I to its own Indent Selection, which swallowed the key before Generate SQL with AI (window binding) saw it.
        var editing=Editor.TextArea.DefaultInputHandler.Editing;
        // The gesture also lives on the routed command itself, and WPF matches a command binding's gestures too, so the command binding goes as well.
        foreach(var binding in editing.InputBindings.OfType<KeyBinding>().Where(b=>b.Command==ICSharpCode.AvalonEdit.AvalonEditCommands.IndentSelection).ToList()) editing.InputBindings.Remove(binding);
        foreach(var binding in editing.CommandBindings.Where(b=>b.Command==ICSharpCode.AvalonEdit.AvalonEditCommands.IndentSelection).ToList()) editing.CommandBindings.Remove(binding);
        foreach(var binding in Editor.TextArea.CommandBindings.OfType<CommandBinding>().Where(b=>b.Command==ICSharpCode.AvalonEdit.AvalonEditCommands.IndentSelection).ToList()) Editor.TextArea.CommandBindings.Remove(binding);
        Editor.PreviewKeyDown+=(_,e)=>{if(ProcessIncrementalSearchKey(e.Key==Key.System?e.SystemKey:e.Key,Keyboard.Modifiers))e.Handled=true;else if(ProcessCommentKey(e.Key,Keyboard.Modifiers,Environment.TickCount64) || ProcessEditKey(e.Key==Key.System?e.SystemKey:e.Key,Keyboard.Modifiers,Environment.TickCount64))e.Handled=true;};
        Editor.LostKeyboardFocus+=(_,_)=>{commentChordDeadline=0;lineChordDeadline=0;};
    }
    private static bool IsSlash(Key key)=>key is Key.OemQuestion or Key.AbntC1 or Key.Divide;
    private static bool IsBackslash(Key key)=>key is Key.OemBackslash or Key.Oem5;
    internal bool ProcessCommentKey(Key key,ModifierKeys modifiers,long now)
    {
        if(!Editor.IsKeyboardFocusWithin){commentChordDeadline=0;return false;}
        if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)return false;
        if(commentChordDeadline>0)
        {
            bool valid=now<=commentChordDeadline;commentChordDeadline=0;
            if(valid && modifiers==ModifierKeys.Control && key is Key.C or Key.U){CommentSelection(key==Key.U);return true;}
            if(valid && modifiers==ModifierKeys.Control && IsBackslash(key)){RunTextEdit(SqlTextCommands.DeleteHorizontalWhiteSpace);return true;}
            if(valid && modifiers==ModifierKeys.Control && key==Key.Z){MainWindow.RestoreClosedTabCommand.Execute(null,this);return true;}
            if(valid && modifiers==ModifierKeys.Control && key==Key.B){MainWindow.CodeSnippetsManagerCommand.Execute(null,this);return true;}
            if(valid && modifiers==ModifierKeys.Control && key==Key.Q){RunTextEdit(SqlTextCommands.FormatSql);return true;}
            if(valid && modifiers==ModifierKeys.Control && key==Key.I){_=ShowQuickInfoAsync();return true;}
            if(valid && modifiers==ModifierKeys.Control && key is Key.X or Key.S){ShowSnippetPicker(key==Key.S);return true;}
            if(valid && modifiers==ModifierKeys.Control && key==Key.K){ToggleBookmark();return true;}
            if(valid && modifiers==ModifierKeys.Control && key is Key.N or Key.P){GoToBookmark(key==Key.N);return true;}
            if(valid && modifiers==ModifierKeys.Control && key==Key.L){if(BookmarkCount>0)SqlBookmarkCommands.ClearBookmarks.Execute(null,this);return true;}
            if(key==Key.Escape && modifiers==ModifierKeys.None)return true;
        }
        if(key==Key.K && modifiers==ModifierKeys.Control){commentChordDeadline=now+2000;return true;}
        return false;
    }
    /// <summary>SSMS 22.11 shortcuts of Edit ▸ Advanced; Alt combinations arrive here with the real key (not Key.System).</summary>
    internal bool ProcessEditKey(Key key,ModifierKeys modifiers,long now)
    {
        if(!Editor.IsKeyboardFocusWithin){lineChordDeadline=0;return false;}
        if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)return false;
        const ModifierKeys shiftAlt=ModifierKeys.Shift|ModifierKeys.Alt;
        if(lineChordDeadline>0)
        {
            bool valid=now<=lineChordDeadline;lineChordDeadline=0;
            if(valid && modifiers==shiftAlt && key==Key.S){RunTextEdit(SqlTextCommands.SortLines);return true;}
            if(valid && modifiers==shiftAlt && key==Key.J){RunTextEdit(SqlTextCommands.JoinLines);return true;}
            if(key==Key.Escape && modifiers==ModifierKeys.None)return true;
        }
        if(modifiers==shiftAlt && key==Key.L){lineChordDeadline=now+2000;return true;}
        if(modifiers==ModifierKeys.Control && IsSlash(key)){RunTextEdit(SqlTextCommands.ToggleLineComment);return true;}
        if(modifiers==(ModifierKeys.Control|ModifierKeys.Shift) && IsSlash(key)){RunTextEdit(SqlTextCommands.ToggleBlockComment);return true;}
        if(modifiers==(ModifierKeys.Control|ModifierKeys.Shift) && key is Key.U or Key.L){RunTextEdit(e=>SqlTextCommands.ChangeCase(e,key==Key.U));return true;}
        if(modifiers==ModifierKeys.Alt && key is Key.PageDown or Key.PageUp){GoToIssue();return true;}
        if(modifiers==ModifierKeys.Alt && key is Key.Up or Key.Down){RunTextEdit(e=>SqlTextCommands.MoveLines(e,key==Key.Up));return true;}
        // PGMS-078 (M1103), SSMS 22.11 shortcuts.
        if(modifiers==shiftAlt && key is Key.OemPlus or Key.Add){RunTextEdit(SqlSelectionCommands.ExpandSelection);return true;}
        if(modifiers==shiftAlt && key==Key.OemCloseBrackets){RunTextEdit(SqlSelectionCommands.ExpandToContainingBlock);return true;}
        if(modifiers==shiftAlt && key==Key.E){RunTextEdit(SqlSelectionCommands.ExpandToLine);return true;}
        if((modifiers==(ModifierKeys.Control|ModifierKeys.Alt) || modifiers==(ModifierKeys.Control|ModifierKeys.Alt|ModifierKeys.Shift)) && key is Key.Right or Key.Left)
        {RunTextEdit(e=>SqlSelectionCommands.MoveSubword(e,key==Key.Right,modifiers.HasFlag(ModifierKeys.Shift)));return true;}
        if(modifiers==(ModifierKeys.Control|ModifierKeys.Shift) && key==Key.V){ShowClipboardHistoryMenu();return true;}
        if(modifiers==(ModifierKeys.Control|ModifierKeys.Shift) && key==Key.Back){SqlEditorCommands.GoToLastEditLocation.Execute(null,this);return true;}
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
    internal void RunTextEdit(Func<ICSharpCode.AvalonEdit.TextEditor,string?> run)
    {
        try{if(run(Editor) is { } status)QueryStatus.Text=status;}
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){QueryStatus.Text=ex.Message;}
        UpdateEditorInfo();
        Editor.Focus();
    }
    /// <summary>Edit ▸ Insert File As Text…: reads the file like Open (16 MiB, BOM detection) and inserts it at the caret.</summary>
    public async Task InsertFileAsTextAsync(string path)
    {
        var snapshot=await SqlFileStore.ReadAsync(path);
        RunTextEdit(e=>SqlTextCommands.InsertText(e,snapshot.Text,System.IO.Path.GetFileName(snapshot.Path)));
    }
}
