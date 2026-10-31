using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PrivilegeCommitDialogChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="commit_ui_"+suffix,role="commit_ui_role_"+suffix,owner="commit_ui_owner_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task<bool> Has(){await using var cmd=new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(@role,@table,'SELECT')",admin);cmd.Parameters.AddWithValue("role",role);cmd.Parameters.AddWithValue("table",table);return (bool)(await cmd.ExecuteScalarAsync())!;}
        try
        {
            await Sql($"CREATE ROLE {qr};CREATE ROLE {SqlIdentifier.Quote(owner)} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {SqlIdentifier.Quote(owner)};CREATE TABLE {table}(id int);ALTER TABLE {table} OWNER TO {SqlIdentifier.Quote(owner)}");
            foreach(bool before in new[]{true,false})
            {
                await new PrivilegeService(connection).ApplyAsync(new(PrivilegeTarget.Table,"target",role,"SELECT",true,schema));
                await using var proxy=new CommitAckProxy(before){Armed=false};
                var settings=new NpgsqlConnectionStringBuilder(connection){Username=owner,Port=proxy.Port,Pooling=false,SslMode=SslMode.Disable,Timeout=10};
                var dialog=new PrivilegeDialog(settings.ConnectionString){ShowInTaskbar=false,Opacity=0};
                T Find<T>(string name)where T:FrameworkElement=>(T)dialog.FindName(name);
                try
                {
                    dialog.Show();Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
                    Find<TextBox>("SchemaName").Text=schema;Find<TextBox>("ObjectName").Text="target";Find<TextBox>("RoleName").Text=role;
                    await dialog.InspectAsync();
                    check(dialog.AccessSnapshot is not null && dialog.AccessSnapshot.RecipientIdentity is not null,"Commit fault is armed only after successful owner inspection (feedback="+Find<TextBlock>("Feedback").Text+")");
                    proxy.Armed=true;await dialog.ApplyAsync();
                    check(dialog.HasUnconfirmedCommit && !Find<Button>("ApplyButton").IsEnabled && !Find<Button>("ScriptButton").IsEnabled,"Lost commit ACK blocks duplicate Apply and Script in WPF (fault="+proxy.FaultInjected+", frontendCommits="+proxy.FrontendCommits+", backendCommits="+proxy.BackendCommits+", pending="+dialog.HasUnconfirmedCommit+", feedback="+Find<TextBlock>("Feedback").Text+")");
                    check(((string)Find<Button>("InspectButton").Content).Contains("Check transaction",StringComparison.Ordinal),"Existing Inspect action offers read-only transaction check");
                    if(!before)foreach(bool small in new[]{false,true})
                    {
                        dialog.Width=small?650:760;dialog.Height=small?540:760;dialog.UpdateLayout();
                        var element=(FrameworkElement)dialog.Content;
                        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
                        var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var rect=new Rect(0,0,element.ActualWidth,element.ActualHeight);dc.DrawRectangle(dialog.Background,null,rect);dc.DrawRectangle(new VisualBrush(element),null,rect);}bitmap.Render(drawing);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));System.IO.Directory.CreateDirectory("artifacts/review");using var file=System.IO.File.Create("artifacts/review/m727-commit-"+(small?"small":"normal")+".png");encoder.Save(file);
                    }
                    await dialog.ApplyAsync();check(await Has()==!before,"Duplicate Apply while unknown does not change server outcome");
                    proxy.RefuseNewConnections=true;
                    await dialog.InspectAsync();
                    check(dialog.HasUnconfirmedCommit && !Find<Button>("ApplyButton").IsEnabled && Find<TextBlock>("Feedback").Text.Contains("unknown",StringComparison.Ordinal),"Failed reconciliation retains receipt, blocked Apply and retry action");
                    check(!Find<FrameworkElement>("Fields").IsEnabled,"Pending transaction protects the original command fields");
                    proxy.RefuseNewConnections=false;
                    string journalFile=System.IO.Path.Combine(new PrivilegeCommitStore(settings.ConnectionString).DirectoryPath,"pending.commit");
                    check(System.IO.File.Exists(journalFile),"Unconfirmed WPF operation retains a durable protected record");
                    dialog.Close();dialog=new PrivilegeDialog(settings.ConnectionString){ShowInTaskbar=false,Opacity=0};dialog.Show();
                    check(dialog.HasUnconfirmedCommit && !Find<Button>("ApplyButton").IsEnabled,"Reopened WPF dialog blocks Apply using its restored durable receipt");
                    check(Find<TextBox>("SchemaName").Text==schema && Find<TextBox>("ObjectName").Text=="target" && Find<TextBox>("RoleName").Text==role,"Reopened WPF dialog restores exact original command fields without replay");
                    if(!before)CaptureRecovery(dialog,"restored");
                    if(!before)
                    {
                        byte[] intact=System.IO.File.ReadAllBytes(journalFile);byte[] corrupt=intact.ToArray();corrupt[^1]^=0x40;System.IO.File.WriteAllBytes(journalFile,corrupt);
                        dialog.Close();dialog=new PrivilegeDialog(settings.ConnectionString){ShowInTaskbar=false,Opacity=0};dialog.Show();
                        check(dialog.HasUnconfirmedCommit && !Find<Button>("ApplyButton").IsEnabled && !Find<Button>("ScriptButton").IsEnabled,"Unreadable pending journal cannot silently enable permission writes");
                        check(System.IO.File.Exists(journalFile) && Find<TextBlock>("Feedback").Text.Contains("preserved",StringComparison.Ordinal),"Unreadable pending journal is preserved with recoverable feedback");
                        CaptureRecovery(dialog,"unreadable");
                        System.IO.File.WriteAllBytes(journalFile,intact);
                    }
                    await dialog.InspectAsync();
                    check(!dialog.HasUnconfirmedCommit && Find<Button>("ApplyButton").IsEnabled,"Confirmed original transaction unlocks the dialog");
                    check(Find<TextBlock>("Feedback").Text.Contains(before?"aborted":"committed",StringComparison.Ordinal),"WPF reports reconciled server outcome without inferring it from ACL");
                    check(await Has()==!before,"Read-only reconciliation does not repeat the original command");
                    check(!System.IO.File.Exists(journalFile),"Confirmed WPF reconciliation clears only the matching protected intent");
                }
                finally{dialog.Close();}
            }
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {SqlIdentifier.Quote(owner)}");}
    }
    private static void CaptureRecovery(Window dialog,string state)
    {
        foreach(bool small in new[]{false,true})
        {
            dialog.Width=small?650:760;dialog.Height=small?540:760;dialog.UpdateLayout();
            var element=(FrameworkElement)dialog.Content;
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
            var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var rect=new Rect(0,0,element.ActualWidth,element.ActualHeight);dc.DrawRectangle(dialog.Background,null,rect);dc.DrawRectangle(new VisualBrush(element),null,rect);}bitmap.Render(drawing);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));System.IO.Directory.CreateDirectory("artifacts/review");using var file=System.IO.File.Create("artifacts/review/m738-commit-"+state+"-"+(small?"small":"normal")+".png");encoder.Save(file);
        }
    }
}
