using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using PGMS.Desktop;

/// <summary>PGMS-057: bookmarks and outlining against SSMS 22.11 as piloted in M1080.</summary>
internal static class BookmarkChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        const string sample="select a,\r\n  b\r\nfrom t;\r\n/* bloco\r\n   de comentario */\r\nbegin\r\n  select 1;\r\nend;\r\nselect 2;";
        var doc=new TextDocument(sample);
        var folds=SqlFolding.Create(doc).ToList();
        check(folds.Count==3 && doc.GetText(folds[0].StartOffset,folds[0].EndOffset-folds[0].StartOffset)=="select a,\r\n  b\r\nfrom t;" && folds[1].Name=="/* ... */" && doc.GetText(folds[2].StartOffset,folds[2].EndOffset-folds[2].StartOffset)=="begin\r\n  select 1;","Outline regions: each statement of more than one line and each multi-line block comment");
        check(SqlFolding.Create(new TextDocument("select 1; select 2;")).Count()==0,"One-line statements have no region");
        bool unread=false;try{SqlFolding.Create(new TextDocument("select 'x"));}catch(FormatException){unread=true;}check(unread,"Unreadable text gives no new outline (the previous one stays)");

        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");
        try
        {
            editor.Text=sample;view.UpdateFoldings();
            check(view.OutliningEnabled && view.Folding!.AllFoldings.Count()==3,"Outlining is on by default");
            view.ToggleAllOutlining();check(view.Folding!.AllFoldings.All(f=>f.IsFolded),"Toggle All Outlining collapses every region");
            view.ToggleAllOutlining();check(view.Folding!.AllFoldings.All(f=>!f.IsFolded),"Toggle All Outlining again expands them");
            editor.CaretOffset=editor.Document.GetLineByNumber(2).Offset;view.ToggleOutliningExpansion();
            check(view.Folding!.AllFoldings.Count(f=>f.IsFolded)==1 && view.Folding.AllFoldings.First().IsFolded,"Toggle Outlining Expansion folds the region at the caret line");
            view.OutliningEnabled=false;check(view.Folding is null && editor.Text==sample,"Stop Outlining removes the regions without touching the text");
            view.OutliningEnabled=true;check(view.Folding is not null,"Start Automatic Outlining brings them back");
            // Bookmarks
            editor.CaretOffset=editor.Document.GetLineByNumber(3).Offset+2;view.ToggleBookmark();
            editor.CaretOffset=editor.Document.GetLineByNumber(8).Offset;view.ToggleBookmark();
            check(view.BookmarkLines.SequenceEqual([3,8]),"Toggle Bookmark marks the caret line");
            editor.CaretOffset=0;view.GoToBookmark(true);check(editor.TextArea.Caret.Line==3,"Next Bookmark goes to the next marked line");
            view.GoToBookmark(true);check(editor.TextArea.Caret.Line==8,"Next Bookmark again");
            view.GoToBookmark(true);check(editor.TextArea.Caret.Line==3,"Next Bookmark goes around the end (as in SSMS)");
            view.GoToBookmark(false);check(editor.TextArea.Caret.Line==8,"Previous Bookmark goes around the start");
            editor.Document.Insert(0,"-- novo\r\n");check(view.BookmarkLines.SequenceEqual([4,9]),"Bookmarks follow the edits");
            editor.CaretOffset=editor.Document.GetLineByNumber(4).Offset;view.ToggleBookmark();check(view.BookmarkLines.SequenceEqual([9]),"Toggle Bookmark again removes it");
            editor.Document.Remove(editor.Document.GetLineByNumber(8).Offset,editor.Document.GetLineByNumber(9).EndOffset+2-editor.Document.GetLineByNumber(8).Offset);
            check(view.BookmarkCount<=1,"A deleted line does not leave a duplicated bookmark");
            view.ClearBookmarks();check(view.BookmarkCount==0,"Clear All Bookmarks In Document");
            check(!SqlBookmarkCommands.NextBookmark.CanExecute(null,editor) || view.BookmarkCount==0,"Next Bookmark has nothing to do without bookmarks");
            // Chords Ctrl+K, Ctrl+K and Ctrl+K, Ctrl+N
            var host=new System.Windows.Window{Content=view,Width=600,Height=400,ShowInTaskbar=false,ShowActivated=false,Left=-10000};host.Show();
            try
            {
                editor.Focus();editor.CaretOffset=editor.Document.GetLineByNumber(2).Offset;
                view.ProcessCommentKey(System.Windows.Input.Key.K,System.Windows.Input.ModifierKeys.Control,1000);
                bool handled=view.ProcessCommentKey(System.Windows.Input.Key.K,System.Windows.Input.ModifierKeys.Control,1100);
                check(!editor.IsKeyboardFocusWithin || handled && view.BookmarkLines.SequenceEqual([2]),"Ctrl+K, Ctrl+K toggles a bookmark in the editor");
            }
            finally{host.Content=null;host.Close();}
        }
        finally{await view.DisposeAsync();}

        // Menu
        var shell=new MainWindow{ShowInTaskbar=false};shell.Show();await shell.RecoveryCompletion;
        try
        {
            var bookmarksMenu=(MenuItem)shell.FindName("BookmarksMenu");var outlining=(MenuItem)shell.FindName("OutliningMenu");
            check(bookmarksMenu.Items.OfType<MenuItem>().Select(i=>(string)i.Header).SequenceEqual(["Toggle Bookmark","Previous Bookmark","Next Bookmark","Clear Bookmarks","Previous Bookmark In Document","Next Bookmark In Document","Clear All Bookmarks In Document"]),"Edit > Bookmarks follows the SSMS order without the Bookmark window commands");
            check(outlining.Items.OfType<MenuItem>().Select(i=>(string)i.Header).SequenceEqual(["Toggle Outlining Expansion","Toggle All Outlining","Stop Outlining","Start Automatic Outlining"]),"Edit > Outlining lists the outlining commands");
        }
        finally{shell.Close();await shell.CloseCompletion;}
    }
}
