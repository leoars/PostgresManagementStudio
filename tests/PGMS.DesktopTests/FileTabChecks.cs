using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using System.Windows.Input;
using PGMS.Desktop;

/// <summary>PGMS-059: Save All, Recent Files, Open File with New Connection, Close All Tabs, Restore Closed Tab and the Window menu (M1076).</summary>
internal static class FileTabChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        check(RecentFileList.Abbreviate(@"D:\Leonardo\_PGMS\artifacts\acceptance\M1075-ssms-edit\pilot.sql",45) is var a && a.StartsWith(@"D:\...\") && a.EndsWith(@"M1075-ssms-edit\pilot.sql") && a.Length<=45,"Long recent paths are shortened in the middle, keeping root and last folders");
        check(RecentFileList.Abbreviate(@"C:\x\y.sql",45)==@"C:\x\y.sql","Short recent paths stay whole");
        check(RecentFileList.Add([@"C:\a.sql",@"C:\b.sql"],@"c:\B.SQL",10) is [@"c:\B.SQL",@"C:\a.sql"],"Reopening a recent file moves it to the top without duplicates");
        check(RecentFileList.Add([@"C:\a.sql",@"C:\b.sql"],@"C:\c.sql",2).Count==2,"The recent list keeps the configured count");

        string root=Path.Combine(Path.GetTempPath(),"pgms-file-tabs-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string fileA=Path.Combine(root,"a_file.sql"),fileB=Path.Combine(root,"b.sql"),fileC=Path.Combine(root,"c.sql");
        File.WriteAllText(fileA,"select 'a';");File.WriteAllText(fileB,"select 'b';");File.WriteAllText(fileC,"select 'c';");
        var shell=new MainWindow{ShowInTaskbar=false};shell.Show();
        var errors=new List<string>();shell.ShowErrorOverride=ex=>errors.Add(ex.Message);
        var dock=(DockingManager)shell.FindName("DockManager");
        List<LayoutDocument> Tabs()=>dock.Layout.Descendents().OfType<LayoutDocument>().ToList();
        List<QueryView> Queries()=>Tabs().Select(d=>d.Content).OfType<QueryView>().ToList();
        async Task Idle()=>await shell.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try
        {
            await shell.RecoveryCompletion;
            foreach(var q in Queries())await shell.CloseQueryAsync(q);
            // Recent Files
            await shell.OpenQueryFileAsync(fileA);await shell.OpenQueryFileAsync(fileB);
            check(shell.EnvironmentSettings.Load().RecentFiles is [var r1,var r2,..] && r1==fileB && r2==fileA,"Opened files go to Recent Files, most recent first");
            var fileMenu=(MenuItem)shell.FindName("FileMenu");var recentMenu=(MenuItem)shell.FindName("RecentFilesMenu");
            fileMenu.IsSubmenuOpen=true;await Idle();
            var recentItems=recentMenu.Items.OfType<MenuItem>().ToList();
            check(recentItems.Count>=2 && ((string)recentItems[0].Header).StartsWith("_1 ") && (string)recentItems[0].ToolTip==fileB && ((string)recentItems[1].Header).Contains("a__file.sql"),"File > Recent Files numbers the files and escapes underscores in the label");
            fileMenu.IsSubmenuOpen=false;
            shell.EnvironmentSettings.Update(d=>d with{RecentFilesCount=1});
            fileMenu.IsSubmenuOpen=true;await Idle();check(recentMenu.Items.Count==1,"Recent Files shows only the configured count");fileMenu.IsSubmenuOpen=false;
            shell.EnvironmentSettings.Update(d=>d with{RecentFilesCount=10});
            File.Delete(fileC);shell.EnvironmentSettings.Update(d=>d with{RecentFiles=[fileC,..d.RecentFiles??[]]});
            await shell.OpenRecentFileAsync(fileC);
            check(errors.Count==1 && errors[0].Contains("no longer exists") && !(shell.EnvironmentSettings.Load().RecentFiles??[]).Contains(fileC),"A recent file that no longer exists is reported and removed from the list: "+string.Join(" | ",errors));
            var options=new OptionsDialog(shell.EnvironmentSettings){ShowInTaskbar=false};
            ((TextBox)options.FindName("RecentFilesBox")).Text="0";options.Accept(options,new RoutedEventArgs());
            check(((TextBlock)options.FindName("GeneralError")).Visibility==Visibility.Visible && shell.EnvironmentSettings.Load().RecentCount==10,"Options refuses a recent count outside 1 to 24");
            ((TextBox)options.FindName("RecentFilesBox")).Text="5";options.Accept(options,new RoutedEventArgs());
            check(shell.EnvironmentSettings.Load().RecentCount==5,"Options saves the number of recent files");
            shell.EnvironmentSettings.Update(d=>d with{RecentFilesCount=10});
            // Save All
            var queryA=Queries().Single(q=>q.FilePath==fileA);var queryB=Queries().Single(q=>q.FilePath==fileB);
            ((TextEditor)queryA.FindName("Editor")).Text="select 'A2';";((TextEditor)queryB.FindName("Editor")).Text="select 'B2';";
            check(MainWindow.SaveAllCommand.CanExecute(null,shell),"Save All is enabled with changed tabs");
            check(await shell.SaveAllAsync() && File.ReadAllText(fileA)=="select 'A2';" && File.ReadAllText(fileB)=="select 'B2';" && !queryA.IsDirty && !queryB.IsDirty,"Save All writes every changed file tab");
            check(!MainWindow.SaveAllCommand.CanExecute(null,shell),"Save All is disabled when nothing changed");
            // Restore Closed Tab
            ApplicationCommands.New.Execute(null,shell);await Idle();
            var draft=(QueryView)dock.ActiveContent;var draftEditor=(TextEditor)draft.FindName("Editor");draftEditor.Text="select 42; -- ação";draft.IsDirty=false;
            check(await shell.CloseQueryAsync(draft) && shell.ClosedTabs[0] is {FilePath:null,Text:"select 42; -- ação"},"A closed query without file is remembered with its text");
            var restored=await shell.RestoreClosedTabAsync();
            check(restored is {FilePath:null,HasSession:false,IsDirty:true} && ((TextEditor)restored.FindName("Editor")).Text=="select 42; -- ação","Restore Closed Tab brings the query back with its text, unsaved and disconnected");
            restored!.IsDirty=false;await shell.CloseQueryAsync(restored);
            check(await shell.CloseQueryAsync(queryB) && shell.ClosedTabs[0].FilePath==fileB,"A closed file tab is remembered by path");
            var reopened=await shell.RestoreClosedTabAsync();check(reopened?.FilePath==fileB && !reopened.HasSession,"Restore Closed Tab reopens the file from disk, not connected");
            ApplicationCommands.New.Execute(null,shell);await Idle();var blank=(QueryView)dock.ActiveContent;int before=shell.ClosedTabs.Count;
            await shell.CloseQueryAsync(blank);check(shell.ClosedTabs.Count==before,"An empty query is not kept for Restore Closed Tab");
            // Editor chord Ctrl+K, Ctrl+Z
            ApplicationCommands.New.Execute(null,shell);await Idle();var chord=(QueryView)dock.ActiveContent;var chordEditor=(TextEditor)chord.FindName("Editor");chordEditor.Text="select 7;";chord.IsDirty=false;
            await shell.CloseQueryAsync(chord);
            var host=Queries().First();var hostEditor=(TextEditor)host.FindName("Editor");dock.ActiveContent=host;await Idle();hostEditor.Focus();await Idle();
            int tabsBefore=Queries().Count;
            check(host.ProcessCommentKey(Key.K,ModifierKeys.Control,Environment.TickCount64) && host.ProcessCommentKey(Key.Z,ModifierKeys.Control,Environment.TickCount64),"Ctrl+K, Ctrl+Z in the editor is taken by Restore Closed Tab");
            for(int i=0;i<50 && Queries().Count==tabsBefore;i++)await Task.Delay(20);
            check(Queries().Any(q=>((TextEditor)q.FindName("Editor")).Text=="select 7;"),"The editor chord restores the closed query");
            // Window menu
            var windowMenu=(MenuItem)shell.FindName("WindowMenu");windowMenu.IsSubmenuOpen=true;await Idle();
            var listed=windowMenu.Items.OfType<MenuItem>().Where(i=>Equals(i.Tag,"tab-list")).ToList();
            check(listed.Count==Math.Min(10,Tabs().Count) && ((string)listed[0].Header).StartsWith("_1 ") && ((MenuItem)shell.FindName("RestoreClosedTabMenu")).IsEnabled==shell.ClosedTabs.Count>0,"Window lists the open tabs numbered, in order of use");
            check(windowMenu.Items.IndexOf(shell.FindName("WindowsDialogMenu"))==windowMenu.Items.Count-1,"Windows… stays last, after the tab list");
            windowMenu.IsSubmenuOpen=false;
            check(shell.TabsInUseOrder()[0]==Tabs().First(d=>d.IsActive),"The active tab comes first in use order");
            // Windows… dialog
            var entries=Tabs().Select(d=>new WindowsDialog.Entry(d.Title.TrimEnd('*'),(d.Content as QueryView)?.FilePath??"",d,true)).ToList();
            var closedByDialog=new List<LayoutDocument>();
            var dialog=new WindowsDialog(entries,_=>{},_=>Task.CompletedTask,docs=>{closedByDialog.AddRange(docs);return Task.CompletedTask;}){ShowInTaskbar=false};dialog.Show();
            check(dialog.Shown.Select(e=>e.Path).SequenceEqual(dialog.Shown.Select(e=>e.Path).OrderBy(p=>p,StringComparer.CurrentCultureIgnoreCase)),"Windows… sorts by Path, as in SSMS");
            ((ListView)dialog.FindName("WindowList")).SelectAll();await dialog.CloseSelectedAsync();
            check(closedByDialog.Count==entries.Count,"Close Window(s) closes every selected tab");dialog.Close();
            // Close All Tabs
            foreach(var q in Queries()){((TextEditor)q.FindName("Editor")).Text+=" ";}
            IReadOnlyList<string>? asked=null;shell.CloseAllPrompt=names=>{asked=names;return MessageBoxResult.Cancel;};
            int open=Tabs().Count;
            check(!await shell.CloseAllTabsAsync() && Tabs().Count==open && asked?.Count==Queries().Count,"Close All Tabs asks once for every changed tab and Cancel keeps them all");
            shell.CloseAllPrompt=names=>MessageBoxResult.No;
            check(await shell.CloseAllTabsAsync() && Tabs().Count==0,"Don't Save closes every tab without asking again");
            check(File.ReadAllText(fileA)=="select 'A2';","Don't Save leaves the files as they were");
            // Open File with New Connection without a connection to reuse
            shell.ConnectionPicker=()=>null;
            await shell.OpenFilesWithNewConnectionAsync([fileA]);
            check(Queries().SingleOrDefault(q=>q.FilePath==fileA) is {HasSession:false},"Without a connection to reuse, cancelling the Connect dialog leaves the file open and disconnected");
        }
        finally
        {
            foreach(var q in Queries())q.IsDirty=false;
            shell.Close();await shell.CloseCompletion;
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }
}
