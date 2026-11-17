using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>PGMS-049: editor information bar and Set End of Line Sequence / Indentation / Encoding, against SSMS 22.11 as piloted in M1078.</summary>
internal static class EditorInfoBarChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");
        string Label(string name)=>(string)((Button)view.FindName(name)).Content;
        try
        {
            editor.Text="select 1;\r\n\tselect 2;\r\nselect 'ação';\r\n";editor.Document.UndoStack.ClearAll();editor.CaretOffset=0;
            check(Label("CaretPositionButton")=="Ln: 1, Ch: 1" && Label("IndentationButton")=="TABS" && Label("LineEndingButton")=="CRLF" && Label("EncodingButton")=="UTF-8","Bar shows Ln/Ch, TABS, CRLF and UTF-8 as in SSMS");
            editor.CaretOffset=editor.Document.GetLineByNumber(2).Offset+3;check(Label("CaretPositionButton")=="Ln: 2, Ch: 4","Ln/Ch follow the caret (characters, tab counts as one)");
            SqlEditorCommands.LineEndingsLf.Execute(null,editor);
            check(editor.Text=="select 1;\n\tselect 2;\nselect 'ação';\n" && Label("LineEndingButton")=="LF","LF converts every line break and the bar shows LF");
            editor.Undo();check(editor.Text.Contains("\r\n") && !editor.Text.Replace("\r\n","").Contains('\n') && Label("LineEndingButton")=="CRLF","The conversion is one Undo step");
            SqlEditorCommands.LineEndingsCr.Execute(null,editor);check(editor.Text=="select 1;\r\tselect 2;\rselect 'ação';\r" && Label("LineEndingButton")=="CR","CR converts too");
            SqlEditorCommands.LineEndingsCrLf.Execute(null,editor);check(editor.Text=="select 1;\r\n\tselect 2;\r\nselect 'ação';\r\n","CRLF converts back");
            editor.Document.UndoStack.ClearAll();SqlTextCommands.SetLineEndings(editor,"\r\n");check(!editor.CanUndo,"Unchanged line breaks create no Undo entry");
            editor.Text="a\r\nb\nc";check(Label("LineEndingButton")=="CRLF","Mixed breaks show the first line's break");
            editor.Text="select 1;\r\n\tselect 2;\r\n  x\r\n";editor.Document.UndoStack.ClearAll();
            SqlEditorCommands.IndentWithSpaces.Execute(null,editor);
            check(editor.Text=="select 1;\r\n    select 2;\r\n  x\r\n" && Label("IndentationButton")=="SPC" && editor.Options.ConvertTabsToSpaces,"Spaces converts the leading tabs of every line (4 spaces), the bar shows SPC and Tab inserts spaces");
            SqlEditorCommands.IndentWithTabs.Execute(null,editor);check(editor.Text=="select 1;\r\n\tselect 2;\r\n  x\r\n" && Label("IndentationButton")=="TABS" && !editor.Options.ConvertTabsToSpaces,"Tabs converts back, keeping partial indentation as spaces");
            editor.Text="a\tb";editor.Document.UndoStack.ClearAll();SqlTextCommands.SetIndentation(editor,true);check(editor.Text=="a\tb" && !editor.CanUndo,"Tabs inside a line are not touched");
            editor.IsReadOnly=true;bool refused=false;try{SqlTextCommands.SetLineEndings(editor,"\n");}catch(InvalidOperationException){refused=true;}editor.IsReadOnly=false;check(refused,"Read-only editor is refused");
            // Zoom
            var zoom=(ComboBox)view.FindName("ZoomBox");
            check(zoom.Items.Cast<string>().SequenceEqual(["20 %","50 %","70 %","100 %","150 %","200 %","400 %"]) && (string)zoom.SelectedItem=="100 %" && view.Zoom==100,"Zoom starts at 100 % with the SSMS steps");
            zoom.SelectedItem="150 %";check(view.Zoom==150 && Math.Abs(editor.FontSize-21)<0.01,"Choosing 150 % scales the editor font");
            view.Zoom=1000;check(view.Zoom==400 && (string)zoom.SelectedItem=="400 %","Zoom is limited to 400 %");
            view.Zoom=100;
            view.FileFormat=SqlFileFormat.Utf16LittleEndian;check(Label("EncodingButton")=="UTF-16 LE","The encoding button follows the file encoding");
            view.FileFormat=SqlFileFormat.Utf8;
        }
        finally{await view.DisposeAsync();}

        // Save with Encoding / Reopen with Encoding through the main window
        string root=Path.Combine(Path.GetTempPath(),"pgms-encoding-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string file=Path.Combine(root,"enc.sql");File.WriteAllText(file,"select 'ação';",new System.Text.UTF8Encoding(false));
        var shell=new MainWindow{ShowInTaskbar=false};shell.Show();
        var errors=new List<string>();shell.ShowErrorOverride=ex=>errors.Add(ex.Message);
        try
        {
            await shell.RecoveryCompletion;
            var query=await shell.OpenQueryFileAsync(file);
            check(query.FileFormat==SqlFileFormat.Utf8,"UTF-8 file opens as UTF-8");
            shell.PickEncoding=(_,reopen)=>(true,SqlFileFormat.Utf16LittleEndian);
            check(await shell.SaveWithEncodingAsync(query) && File.ReadAllBytes(file) is [0xFF,0xFE,..] && query.FileFormat==SqlFileFormat.Utf16LittleEndian && !query.IsDirty,"Save with Encoding writes the file in the chosen encoding with its BOM");
            shell.PickEncoding=(_,_)=>(false,null);check(!await shell.SaveWithEncodingAsync(query) && query.FileFormat==SqlFileFormat.Utf16LittleEndian,"Cancelled Save with Encoding keeps the encoding");
            File.WriteAllText(file,"select 'ação';",System.Text.Encoding.Latin1);
            shell.PickEncoding=(_,_)=>(true,null);
            check(!await shell.ReopenWithEncodingAsync(query) && errors.Count==1 && query.Editor.Text=="select 'ação';","Reopen with Auto-Detect refuses a file that is not valid UTF-8 and keeps the text");
            File.WriteAllText(file,"select 'b';\r\n",new System.Text.UnicodeEncoding(true,false));
            shell.PickEncoding=(_,_)=>(true,SqlFileFormat.Utf16BigEndian);
            query.Editor.Text+="-- edit";bool asked=false;shell.ConfirmReopenDiscard=_=>{asked=true;return false;};
            check(!await shell.ReopenWithEncodingAsync(query) && asked && query.Editor.Text.EndsWith("-- edit"),"Reopen with unsaved edits asks first; No keeps the edits");
            shell.ConfirmReopenDiscard=_=>true;
            check(await shell.ReopenWithEncodingAsync(query) && query.Editor.Text=="select 'b';\r\n" && query.FileFormat==SqlFileFormat.Utf16BigEndian && !query.IsDirty && !query.Editor.CanUndo,"Reopen with UTF-16 BE reads the BOM-less file with that encoding");
            query.FilePath=null;check(!SqlEditorCommands.ReopenWithEncoding.CanExecute(null,shell),"Reopen with Encoding needs a file");
        }
        finally
        {
            shell.PickEncoding=null;shell.ConfirmReopenDiscard=null;
            var dock=(DockingManager)shell.FindName("DockManager");
            foreach(var q in dock.Layout.Descendents().OfType<LayoutDocument>().Select(d=>d.Content).OfType<QueryView>())q.IsDirty=false;
            shell.Close();await shell.CloseCompletion;
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }
}
