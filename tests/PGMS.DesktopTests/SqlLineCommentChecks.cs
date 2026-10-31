using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class SqlLineCommentChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");
        try
        {
            foreach(string newline in new[]{"\n","\r\n","\r"})
            {
                string original="  SELECT 'ação 密碼';"+newline+"\tSELECT 2;"+newline+"SELECT 3;";
                editor.Text=original;int end=original.LastIndexOf("SELECT 3",StringComparison.Ordinal);editor.Select(3,end-3);
                int changed=SqlLineComments.Apply(editor,false);
                string expected="  --SELECT 'ação 密碼';"+newline+"\t--SELECT 2;"+newline+"SELECT 3;";
                check(changed==2 && editor.Text==expected,"Comment selection preserves indentation/newlines and excludes untouched boundary line: "+newline.Length);
                editor.Undo();check(editor.Text==original,"Comment selected lines is one text Undo step");editor.Redo();
                check(editor.Text==expected,"Redo restores the complete line comment operation");
                SqlLineComments.Apply(editor,true);check(editor.Text==original,"Uncomment restores exact Unicode text without trimming");
            }
            editor.Text="  SELECT 1;";editor.CaretOffset=5;int caret=editor.CaretOffset;
            check(SqlLineComments.Apply(editor,false)==1 && editor.CaretOffset==caret+2,"No selection comments only current line and tracks caret");
            check(SqlLineComments.Apply(editor,true)==1 && editor.CaretOffset==caret,"Uncomment current line returns caret to original text");
            editor.Text="SELECT '--literal';\n --real\n - not a comment";editor.SelectAll();
            check(SqlLineComments.Apply(editor,true)==1 && editor.Text=="SELECT '--literal';\n real\n - not a comment","Uncomment removes only leading double-dash after indentation");
            editor.Text="SELECT 1;";editor.Document.UndoStack.ClearAll();editor.SelectAll();
            check(SqlLineComments.Apply(editor,true)==0 && !editor.CanUndo,"Uncomment without prefixes does not create an Undo entry");
            editor.IsReadOnly=true;bool readOnly=false;try{SqlLineComments.Apply(editor,false);}catch(InvalidOperationException){readOnly=true;}editor.IsReadOnly=false;
            check(readOnly && editor.Text=="SELECT 1;","Comment command respects read-only editor");
            editor.Text=string.Join("\n",Enumerable.Repeat("x",10001));editor.SelectAll();string before=editor.Text;bool limit=false;
            try{SqlLineComments.Apply(editor,false);}catch(ArgumentException){limit=true;}
            check(limit && editor.Text==before,"Oversized line operation refuses before any mutation");
            editor.Text="";check(SqlLineComments.Apply(editor,false)==1 && editor.Text=="--","Empty current line can become a line comment");editor.Undo();check(editor.Text=="","Empty-line comment is undoable");
        }
        finally{await view.DisposeAsync();}
    }
}
