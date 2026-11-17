using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>PGMS-056: Edit ▸ Advanced commands against the SSMS 22.11 behaviour piloted in M1075.</summary>
internal static class SqlTextCommandChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");
        void Set(string text){editor.Text=text;editor.Document.UndoStack.ClearAll();editor.Select(0,0);editor.CaretOffset=0;}
        int At(int line,int column)=>editor.Document.GetLineByNumber(line).Offset+column-1;
        void SelectLines(int fromLine,int fromColumn,int toLine,int toColumn){int a=At(fromLine,fromColumn);editor.Select(a,At(toLine,toColumn)-a);}
        try
        {
            const string sample="select a, b\nfrom t1\n-- comentado\nwhere x = 1\n  and y = 2;";
            // Toggle Line Comment
            Set(sample);SelectLines(1,1,2,3);SqlTextCommands.ToggleLineComment(editor);
            check(editor.Text.StartsWith("--select a, b\n--from t1\n-- comentado"),"Toggle Line Comment comments every selected line with -- and no space");
            editor.Undo();check(editor.Text==sample,"Toggle Line Comment is one Undo step");
            SelectLines(1,1,2,1);SqlTextCommands.ToggleLineComment(editor);check(editor.Text.StartsWith("--select a, b\nfrom t1"),"Selection ending at column 1 leaves that line untouched");
            Set(sample);SelectLines(2,1,3,3);SqlTextCommands.ToggleLineComment(editor);check(editor.Text.Contains("--from t1\n---- comentado"),"Mixed lines are all commented, including the commented one (as in SSMS)");
            Set(sample);editor.CaretOffset=At(3,3);SqlTextCommands.ToggleLineComment(editor);check(editor.Text.Contains("\n comentado\n"),"A commented line loses only the leading -- (the space stays)");
            Set(sample);editor.CaretOffset=At(5,4);SqlTextCommands.ToggleLineComment(editor);check(editor.Text.EndsWith("  --and y = 2;"),"Single indented line is commented at its first non-blank character");
            SqlTextCommands.ToggleLineComment(editor);check(editor.Text==sample,"Toggling again restores the line");
            Set(sample);SelectLines(4,1,5,5);SqlTextCommands.ToggleLineComment(editor);check(editor.Text.EndsWith("--where x = 1\n--  and y = 2;"),"Block comment column is the smallest indentation of the lines");
            Set("a\n\n  b");editor.SelectAll();SqlTextCommands.ToggleLineComment(editor);check(editor.Text=="--a\n\n--  b","Blank lines inside a block are left alone");
            // Toggle Block Comment
            Set(sample);editor.Select(At(1,8),4);SqlTextCommands.ToggleBlockComment(editor);check(editor.Text.StartsWith("select /*a, b*/\n") && editor.SelectedText=="/*a, b*/","Toggle Block Comment wraps the exact selection and selects the result");
            SqlTextCommands.ToggleBlockComment(editor);check(editor.Text==sample && editor.SelectedText=="a, b","Toggle Block Comment again unwraps it");
            Set(sample);editor.Select(At(1,8),4);SqlTextCommands.ToggleBlockComment(editor);editor.Select(At(1,10),4);SqlTextCommands.ToggleBlockComment(editor);check(editor.Text==sample,"Selection inside the markers is unwrapped too");
            Set(sample);SelectLines(1,3,2,3);SqlTextCommands.ToggleBlockComment(editor);check(editor.Text.StartsWith("se/*lect a, b\nfr*/om t1"),"Multi-line selection is wrapped at its exact ends");
            Set(sample);editor.CaretOffset=At(5,4);SqlTextCommands.ToggleBlockComment(editor);check(editor.Text.EndsWith("  /*and y = 2;*/"),"Without selection the line text (after indentation) is wrapped");
            // Case
            Set(sample);editor.CaretOffset=At(1,3);SqlTextCommands.ChangeCase(editor,true);check(editor.Text.StartsWith("seLect") && editor.CaretOffset==At(1,3),"Make Uppercase without selection changes only the character right of the caret");
            Set("select ação;");editor.Select(0,11);SqlTextCommands.ChangeCase(editor,true);check(editor.Text=="SELECT AÇÃO;" && editor.SelectedText=="SELECT AÇÃO","Make Uppercase keeps the selection and handles accents");
            SqlTextCommands.ChangeCase(editor,false);check(editor.Text=="select ação;","Make Lowercase restores the text");
            Set("SELECT");editor.SelectAll();editor.Document.UndoStack.ClearAll();SqlTextCommands.ChangeCase(editor,true);check(!editor.CanUndo,"Unchanged case creates no Undo entry");
            // Move lines
            Set(sample);editor.CaretOffset=At(2,3);SqlTextCommands.MoveLines(editor,true);check(editor.Text.StartsWith("from t1\nselect a, b\n") && editor.CaretOffset==At(1,3),"Alt+Up moves the current line and the caret with it");
            SqlTextCommands.MoveLines(editor,true);check(editor.Text.StartsWith("from t1\nselect"),"Alt+Up on the first line does nothing");
            Set(sample);SelectLines(1,3,2,3);SqlTextCommands.MoveLines(editor,false);check(editor.Text.StartsWith("-- comentado\nselect a, b\nfrom t1\n") && editor.SelectedText=="lect a, b\nfr","Alt+Down moves the lines touched by the selection and keeps it");
            editor.Undo();check(editor.Text==sample,"Move Lines is one Undo step");
            Set("a\r\nb");editor.CaretOffset=editor.Text.Length;SqlTextCommands.MoveLines(editor,true);check(editor.Text=="b\r\na","Moving the last line up keeps CRLF and no trailing break");
            Set("a\r\nb");SqlTextCommands.MoveLines(editor,false);check(editor.Text=="b\r\na","Moving the first line down to the end keeps CRLF");
            // Duplicate
            Set(sample);editor.CaretOffset=At(2,3);SqlTextCommands.Duplicate(editor);check(editor.Text.StartsWith("select a, b\nfrom t1\nfrom t1\n-- comentado") && editor.CaretOffset==At(3,3),"Duplicate without selection copies the line below, caret on the copy");
            Set(sample);editor.Select(At(1,8),4);SqlTextCommands.Duplicate(editor);check(editor.Text.StartsWith("select a, ba, b\n") && editor.SelectedText=="a, b" && editor.SelectionStart==At(1,12),"Duplicate with selection inserts the copy right after it");
            Set("last");editor.CaretOffset=2;SqlTextCommands.Duplicate(editor);check(editor.Text=="last"+Environment.NewLine+"last","Duplicating the only line adds the system line break");
            // Sort and join
            Set("zeta\nalpha\nMike\nend");SelectLines(1,1,3,5);SqlTextCommands.SortLines(editor);check(editor.Text=="alpha\nMike\nzeta\nend","Sort Lines ignores case (alpha, Mike, zeta)");
            Set("b\r\na");editor.SelectAll();SqlTextCommands.SortLines(editor);check(editor.Text=="a\r\nb","Sort Lines keeps CRLF");
            Set("a\nb");editor.CaretOffset=0;editor.Document.UndoStack.ClearAll();check(SqlTextCommands.SortLines(editor).StartsWith("Select two") && !editor.CanUndo,"Sort Lines needs two lines");
            Set(sample);SelectLines(4,3,5,3);SqlTextCommands.JoinLines(editor);check(editor.Text.EndsWith("where x = 1 and y = 2;"),"Join Lines joins with one space and drops the indentation of the next line");
            Set(sample);editor.CaretOffset=At(4,3);SqlTextCommands.JoinLines(editor);check(editor.Text.EndsWith("where x = 1 and y = 2;") && editor.CaretOffset==At(4,13),"Join Lines without selection joins with the next line");
            Set("a   \n   \n  b");editor.SelectAll();SqlTextCommands.JoinLines(editor);check(editor.Text=="a b","Join Lines trims trailing blanks and skips empty lines");
            // White space
            Set("x    =     1");editor.CaretOffset=3;SqlTextCommands.DeleteHorizontalWhiteSpace(editor);check(editor.Text=="x =     1","Delete Horizontal White Space between words keeps one space");
            Set("    four\ttab");editor.CaretOffset=2;SqlTextCommands.DeleteHorizontalWhiteSpace(editor);check(editor.Text=="four\ttab" && editor.CaretOffset==0,"Delete Horizontal White Space in the indentation removes it all");
            Set("a  b   c   ");editor.SelectAll();SqlTextCommands.DeleteHorizontalWhiteSpace(editor);check(editor.Text=="a b c","Selection collapses each run and drops the trailing one");
            // Tabify and indentation
            Set("    four\ttab\n      six");editor.SelectAll();SqlTextCommands.Tabify(editor,true);check(editor.Text=="\tfour\ttab\n\t  six","Tabify converts only the leading indentation");
            SqlTextCommands.Tabify(editor,false);check(editor.Text=="    four\ttab\n      six","Untabify converts leading tabs back to spaces");
            Set(sample);SelectLines(4,3,5,3);SqlTextCommands.ChangeIndent(editor,true);string indent=editor.Options.IndentationString;
            check(editor.Text.EndsWith(indent+"where x = 1\n"+indent+"  and y = 2;"),"Increase Line Indent adds one level to each touched line");
            Set(sample);editor.CaretOffset=At(5,4);SqlTextCommands.ChangeIndent(editor,false);check(editor.Text.EndsWith("\nand y = 2;"),"Decrease Line Indent removes up to one level");
            Set("\tx\n      y");editor.SelectAll();SqlTextCommands.ChangeIndent(editor,false);check(editor.Text=="x\n  y","Decrease removes one tab or up to tab-size spaces");
            // Insert text
            Set("ab\r\ncd");editor.CaretOffset=1;SqlTextCommands.InsertText(editor,"X\nY","f.sql");check(editor.Text=="aX\r\nYb\r\ncd" && editor.CaretOffset==1,"Insert text matches the document line breaks and leaves the caret at its start");
            // Guards
            Set(sample);editor.IsReadOnly=true;bool refused=false;try{SqlTextCommands.SortLines(editor);}catch(InvalidOperationException){refused=true;}editor.IsReadOnly=false;
            check(refused && editor.Text==sample,"Text commands refuse a read-only editor");
            Set(string.Join("\n",Enumerable.Repeat("x",10001)));editor.SelectAll();bool limited=false;try{SqlTextCommands.ChangeIndent(editor,true);}catch(ArgumentException){limited=true;}
            check(limited && !editor.Text.StartsWith(editor.Options.IndentationString),"Line commands refuse more than 10,000 lines before changing anything");
        }
        finally{await view.DisposeAsync();}
    }
}
