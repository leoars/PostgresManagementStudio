using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using AvalonDock.Layout;
using PGMS.Desktop;

internal static class RecoveryPreferenceChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        string state=Path.GetFullPath(Path.Combine("artifacts","recovery-preferences",Guid.NewGuid().ToString("N")));string root=Path.Combine(state,"recovery");
        var draft=new RecoveryDraft(Guid.NewGuid(),"options.sql","SELECT  1;\n",SqlFileFormat.Utf8){WordWrap=true,ShowWhiteSpace=true,CommandTimeoutSeconds=17};
        using(var source=new QueryRecoverySession(root)){source.Store.Save(draft);check(source.Store.Read(draft.Id)==draft,"Encrypted recovery retains presentation preferences with text");
            var legacy=draft with{Id=Guid.NewGuid()};byte[] clear=JsonSerializer.SerializeToUtf8Bytes(new{Version=1,Draft=new{legacy.Id,legacy.Title,legacy.Text,legacy.Format}});
            File.WriteAllBytes(Path.Combine(source.Store.DirectoryPath,legacy.Id.ToString("N")+".draft"),ProtectedData.Protect(clear,legacy.Id.ToByteArray(),DataProtectionScope.CurrentUser));CryptographicOperations.ZeroMemory(clear);
            var read=source.Store.Read(legacy.Id);check(!read.WordWrap && !read.ShowWhiteSpace && read.CommandTimeoutSeconds==0,"Legacy draft without preferences keeps default unwrapped hidden whitespace");source.Store.Remove(legacy.Id);
        }
        string? previous=Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",state);
        var window=new MainWindow{ShowInTaskbar=false,Opacity=0};
        try
        {
            window.Show();await window.RecoveryCompletion;
            var query=((AvalonDock.DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d=>d.Content).OfType<QueryView>().Single();
            check(query.WordWrap && query.ShowWhiteSpace && query.CommandTimeoutSeconds==17 && query.IsDirty && !query.HasSession,"Async startup restores presentation on disconnected dirty query");
            long edit=query.EditRevision;query.WordWrap=false;query.ShowWhiteSpace=false;query.CommandTimeoutSeconds=31;
            check(query.EditRevision==edit && query.Editor.Text==draft.Text,"Preference changes do not advance text revision or change SQL");
            check(await window.CheckpointQueriesAsync()==1,"Preference-only change advances dirty draft recovery revision");
            var saved=new QueryRecoveryStore(window.RecoveryDirectory!).Read(query.RecoveryId);check(!saved.WordWrap && !saved.ShowWhiteSpace && saved.CommandTimeoutSeconds==31,"Async checkpoint writes changed presentation preferences");
            check(await window.CheckpointQueriesAsync()==0,"Unchanged preferences do not rewrite checkpoint");
            query.WordWrap=true;check(window.CheckpointQueries()==1 && new QueryRecoveryStore(window.RecoveryDirectory!).Read(query.RecoveryId).WordWrap,"Synchronous checkpoint preserves presentation contract too");
            query.IsDirty=false;query.ShowWhiteSpace=true;check(await window.CheckpointQueriesAsync()==0 && !query.IsDirty,"Clean document presentation changes do not create unsaved SQL drafts");
        }
        finally
        {
            foreach(var query in ((AvalonDock.DockingManager)window.FindName("DockManager")).Layout.Descendents().OfType<LayoutDocument>().Select(d=>d.Content).OfType<QueryView>())query.IsDirty=false;
            window.Close();await window.CloseCompletion;Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",previous);
        }
    }
}
