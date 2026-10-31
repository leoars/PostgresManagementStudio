using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PGMS.Desktop.ViewModels;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class RoutinePrivilegeDialogChecks
{
    private static T Find<T>(Window w,string name) where T:FrameworkElement => (T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="routine_ui_"+suffix,owner="routine_owner_"+suffix,role="routine_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(role);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        string owned=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        async Task<bool> Has(string signature)
        {
            await using var cmd=new NpgsqlCommand("SELECT pg_catalog.has_function_privilege(@role,@signature,'EXECUTE')",admin);
            cmd.Parameters.AddWithValue("role",role);cmd.Parameters.AddWithValue("signature",signature);return (bool)(await cmd.ExecuteScalarAsync())!;
        }
        PrivilegeDialog? dialog=null;
        try
        {
            await Sql($"""
                CREATE ROLE {qo} LOGIN; CREATE ROLE {qr} LOGIN; CREATE SCHEMA {qs} AUTHORIZATION {qo};
                GRANT USAGE ON SCHEMA {qs} TO {qr}; SET ROLE {qo};
                CREATE FUNCTION {qs}.target(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';
                CREATE FUNCTION {qs}.target(text) RETURNS text LANGUAGE sql AS 'SELECT $1';
                CREATE PROCEDURE {qs}.proc(INOUT arg integer) LANGUAGE plpgsql AS 'BEGIN arg:=arg+1; END';
                REVOKE EXECUTE ON ROUTINE {qs}.target(integer),{qs}.target(text),{qs}.proc(integer) FROM PUBLIC;
                RESET ROLE;
                """);
            var catalog=new CatalogService(owned);
            var routines=await catalog.ChildrenAsync(new(CatalogKind.Functions,"Functions","postgres",schema));
            var entry=routines.Single(e=>e.Name.Contains("integer",StringComparison.Ordinal));
            check(entry.CanManageRoutinePermissions && !new CatalogEntry(CatalogKind.Functions,"Functions").CanManageRoutinePermissions,"Explorer enables routine permissions only for an exact routine entry");
            await ExplorerRoute(owned,entry,check);
            var captured=await new RoutinePrivilegeCatalog(owned).InspectAsync(entry.Oid);
            var request=new PrivilegeRequest(PrivilegeTarget.Routine,captured.Name,role,"EXECUTE",Schema:schema){Routine=captured};
            var roundtrip=JsonSerializer.Deserialize<PrivilegeRequest>(JsonSerializer.Serialize(request));
            check(roundtrip==request && roundtrip.ToSql()==request.ToSql(),"Routine request JSON preserves value identity and regenerates the exact quoted SQL");
            string encoded=JsonSerializer.Serialize(captured);
            check(!encoded.Contains("ArgumentSql",StringComparison.Ordinal) && !encoded.Contains("QualifiedSignature",StringComparison.Ordinal),"Persistent routine selection contains typed identifiers, not executable signature text");
            string hostileTypes=JsonSerializer.Serialize(new[]{new{Oid=23u,NamespaceOid=11u,Schema="pg_catalog",Name="int4); DROP SCHEMA anything; --"}});
            var hostile=new RoutinePrivilegeSelection(captured.DatabaseOid,captured.ObjectOid,captured.NamespaceOid,schema,captured.Name,"f",hostileTypes);
            check(hostile.ArgumentSql.EndsWith("\"int4); DROP SCHEMA anything; --\"",StringComparison.Ordinal),"Restored type names remain quoted identifiers even with SQL punctuation");
            bool rejected=false;try{await new PrivilegeService(owned).ApplyAsync(request with{Routine=hostile});}catch(InvalidOperationException){rejected=true;}
            check(rejected && !await Has($"{qs}.target(integer)"),"Forged serialized type identity cannot authorize a routine grant");

            dialog=new PrivilegeDialog(owned,entry){ShowInTaskbar=false,Opacity=0};dialog.Show();
            Find<TextBox>(dialog,"RoleName").Text=role;
            check(!Find<Button>(dialog,"ApplyButton").IsEnabled && !Find<Button>(dialog,"ScriptButton").IsEnabled && Find<Button>(dialog,"InspectButton").IsEnabled,"Routine Apply and Script require initial inspection");
            await dialog.InspectAsync();
            check(dialog.AccessSnapshot?.Oid==entry.Oid && Find<Button>(dialog,"ApplyButton").IsEnabled,"WPF inspection binds the selected Explorer overload");
            check(!Find<ComboBox>(dialog,"ObjectType").IsEnabled && !Find<TextBox>(dialog,"SchemaName").IsEnabled && Find<TextBox>(dialog,"ObjectName").IsReadOnly,"Routine target cannot be silently changed through editable name fields");
            check(Find<TextBox>(dialog,"Preview").Text==request.ToSql(),"Routine preview displays the exact captured signature");
            check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(Find<TextBlock>(dialog,"RoutineHelp"))),"Selected overload has an accessible description");
            await Capture(dialog,"normal",760,760,check);
            await Capture(dialog,"small",650,540,check);
            Find<TabControl>(dialog,"PermissionTabs").SelectedIndex=0;Find<ScrollViewer>(dialog,"ContentScroller").ScrollToEnd();await Capture(dialog,"small-sql",650,540,check);
            await dialog.ApplyAsync();
            check(await Has($"{qs}.target(integer)") && !await Has($"{qs}.target(text)"),"WPF Apply grants only the selected overload by ordinary owner");
            check(dialog.AccessSnapshotIsStale && !Find<Button>(dialog,"ApplyButton").IsEnabled && !Find<Button>(dialog,"ScriptButton").IsEnabled,"Routine Apply requires fresh inspection before another mutation or script");
            await dialog.InspectAsync();Find<CheckBox>(dialog,"Revoke").IsChecked=true;await dialog.ApplyAsync();
            check(!await Has($"{qs}.target(integer)"),"Reinspected routine revoke removes real execution authority");
            dialog.Close();dialog=null;

            string waitName="PGMS.RoutineWait."+suffix;
            string waitConnection=new NpgsqlConnectionStringBuilder(owned){ApplicationName=waitName}.ConnectionString;
            dialog=new PrivilegeDialog(waitConnection,entry with{Name=entry.Name+new string(' ',80)}){ShowInTaskbar=false,Opacity=0};dialog.Show();Find<TextBox>(dialog,"RoleName").Text=role;
            check(Find<Button>(dialog,"InspectButton").IsEnabled,"Long Explorer display signature does not fail identifier-length validation");
            await dialog.InspectAsync();
            await using(var locker=new NpgsqlConnection(owned))
            {
                await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();
                await using(var lockCommand=new NpgsqlCommand($"GRANT EXECUTE ON ROUTINE {qs}.target(integer) TO PUBLIC",locker,tx))await lockCommand.ExecuteNonQueryAsync();
                var applying=dialog.ApplyAsync();
                try
                {
                    bool waiting=false;var deadline=DateTime.UtcNow.AddSeconds(10);
                    while(!waiting && DateTime.UtcNow<deadline)
                    {
                        await using var wait=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock')",admin);
                        wait.Parameters.AddWithValue("app",waitName);waiting=(bool)(await wait.ExecuteScalarAsync())!;
                        if(!waiting)await Task.Delay(25);
                    }
                    check(waiting && dialog.IsBusy && !Find<FrameworkElement>(dialog,"Fields").IsEnabled,"Routine Apply waits on a real catalog lock with fields protected");
                    await dialog.ApplyAsync();check(!applying.IsCompleted,"Duplicate routine Apply cannot replace the active operation");
                    dialog.CancelCurrentOperation();await applying.WaitAsync(TimeSpan.FromSeconds(10));
                    check(!dialog.IsBusy && !Find<Button>(dialog,"ApplyButton").IsEnabled && Find<Button>(dialog,"InspectButton").IsEnabled,"Canceled routine Apply requires inspection and offers recovery");
                }
                finally{await tx.RollbackAsync();await applying;}
            }
            check(!await Has($"{qs}.target(integer)"),"Canceled routine grant leaves execution privilege unchanged");
            dialog.Close();dialog=null;

            var scripted=new PrivilegeDialog(owned,entry){ShowInTaskbar=false,Opacity=0};
            var scriptedDone=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scripted.Loaded+=async(_,_)=>
            {
                try{Find<TextBox>(scripted,"RoleName").Text=role;await scripted.InspectAsync();Find<Button>(scripted,"ScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));scriptedDone.SetResult();}
                catch(Exception ex){scriptedDone.SetException(ex);scripted.Close();}
            };
            scripted.ShowDialog();await scriptedDone.Task;
            check(scripted.ScriptSql==request.ToSql() && !await Has($"{qs}.target(integer)"),"Routine Script exports exact SQL without applying a grant");

            var procedure=(await catalog.ChildrenAsync(new(CatalogKind.Procedures,"Procedures","postgres",schema))).Single();
            dialog=new PrivilegeDialog(owned,procedure){ShowInTaskbar=false,Opacity=0};dialog.Show();Find<TextBox>(dialog,"RoleName").Text=role;
            await dialog.InspectAsync();await dialog.ApplyAsync();
            check(await Has($"{qs}.proc(integer)"),"Procedure selected in Explorer supports real WPF EXECUTE grant");
            dialog.Close();dialog=null;

            foreach(bool before in new[]{true,false})
            {
                await Sql($"REVOKE EXECUTE ON ROUTINE {qs}.target(integer) FROM {qr}");
                await using var proxy=new CommitAckProxy(before){Armed=false};
                string proxied=new NpgsqlConnectionStringBuilder(owned){Port=proxy.Port,SslMode=SslMode.Disable,Timeout=10}.ConnectionString;
                dialog=new PrivilegeDialog(proxied,entry){ShowInTaskbar=false,Opacity=0};dialog.Show();Find<TextBox>(dialog,"RoleName").Text=role;
                await dialog.InspectAsync();check(dialog.AccessSnapshot?.TargetIdentity?.Routine==captured,"Routine commit fault armed only after exact inspection");
                proxy.Armed=true;await dialog.ApplyAsync();
                check(dialog.HasUnconfirmedCommit && !Find<Button>(dialog,"ApplyButton").IsEnabled && !Find<Button>(dialog,"ScriptButton").IsEnabled,"Routine lost ACK blocks repeat and scripting");
                var store=new PrivilegeCommitStore(proxied);var pending=store.Read();
                check(pending?.Request==request,"Protected journal restores exact routine and argument type identity");
                dialog.Close();dialog=new PrivilegeDialog(proxied){ShowInTaskbar=false,Opacity=0};dialog.Show();
                check(dialog.HasUnconfirmedCommit && Find<ComboBox>(dialog,"ObjectType").SelectedItem is PrivilegeTarget.Routine && !Find<FrameworkElement>(dialog,"Fields").IsEnabled,"Generic permissions window restores pending routine and freezes its fields");
                check(Find<TextBox>(dialog,"Preview").Text==request.ToSql(),"Reopened pending routine regenerates the reviewed SQL");
                await dialog.ApplyAsync();check(await Has($"{qs}.target(integer)")==!before,"Reopened routine never repeats uncertain operation");
                if(!before)await Capture(dialog,"restored-small",650,540,check);
                await dialog.InspectAsync();
                check(!dialog.HasUnconfirmedCommit && Find<TextBlock>(dialog,"Feedback").Text.Contains(before?"aborted":"committed",StringComparison.Ordinal),"Routine receipt reconciles original outcome after reopen");
                check(!Find<Button>(dialog,"ApplyButton").IsEnabled && !Find<Button>(dialog,"ScriptButton").IsEnabled && store.Read() is null,"Routine reconciliation clears matching journal but requires new access inspection");
                await dialog.InspectAsync();check(Find<Button>(dialog,"ApplyButton").IsEnabled,"Explicit inspection enables routine operation after reconciliation");
                dialog.Close();dialog=null;
            }
            dialog=new PrivilegeDialog(owned,entry){ShowInTaskbar=false,Opacity=0};dialog.Show();Find<TextBox>(dialog,"RoleName").Text=role;await dialog.InspectAsync();
            await Sql($"DROP FUNCTION {qs}.target(integer);SET ROLE {qo};CREATE FUNCTION {qs}.target(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';REVOKE EXECUTE ON ROUTINE {qs}.target(integer) FROM PUBLIC;RESET ROLE");
            await dialog.ApplyAsync();
            check(!await Has($"{qs}.target(integer)") && !Find<Button>(dialog,"ApplyButton").IsEnabled,"Recreated routine cannot receive stale WPF grant");
            await dialog.InspectAsync();
            check(dialog.AccessSnapshotIsStale && !Find<Button>(dialog,"ApplyButton").IsEnabled,"Inspect retains original routine binding rather than transferring it to replacement");
            dialog.Close();dialog=null;
            var replacement=(await catalog.ChildrenAsync(new(CatalogKind.Functions,"Functions","postgres",schema))).Single(e=>e.Name.Contains("integer",StringComparison.Ordinal));
            dialog=new PrivilegeDialog(owned,replacement){ShowInTaskbar=false,Opacity=0};dialog.Show();Find<TextBox>(dialog,"RoleName").Text=role;await dialog.InspectAsync();await dialog.ApplyAsync();
            check(await Has($"{qs}.target(integer)"),"Explicit Explorer reload and new selection authorize replacement routine");
        }
        finally{dialog?.Close();await Sql($"RESET ROLE;DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qo};DROP ROLE IF EXISTS {qr}");}
    }
    private static async Task ExplorerRoute(string connection,CatalogEntry entry,Action<bool,string> check)
    {
        var window=new MainWindow{ShowInTaskbar=false,Opacity=0};window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);var tree=Find<TreeView>(window,"ExplorerTree");tree.Items.Clear();
            var node=new CatalogNode(entry,new CatalogService(connection));tree.Items.Add(node);node.IsSelected=true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var menu=tree.ContextMenu;menu.PlacementTarget=tree;menu.IsOpen=true;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var action=menu.Items.OfType<MenuItem>().Single(m=>m.Name=="ExplorerRoutinePermissions");
            check(action.IsEnabled,"Explorer context menu enables Routine Permissions for selected overload");menu.IsOpen=false;
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
            timer.Tick+=async(_,_)=>
            {
                if(Application.Current.Windows.OfType<PrivilegeDialog>().FirstOrDefault() is not {} child)return;
                timer.Stop();
                try{await child.InspectAsync();check(child.AccessSnapshot?.Oid==entry.Oid,"Explorer command opens and inspects the selected routine in its database");completion.TrySetResult();}
                catch(Exception ex){completion.TrySetException(ex);}
                finally{child.Close();}
            };
            timer.Start();try{action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));}finally{timer.Stop();}
        }
        finally{window.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    }
    private static async Task Capture(Window dialog,string name,double width,double height,Action<bool,string> check)
    {
        dialog.Width=width;dialog.Height=height;await dialog.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);dialog.UpdateLayout();
        var root=(FrameworkElement)dialog.Content;
        foreach(string action in new[]{"InspectButton","ScriptButton","ApplyButton","CloseButton"})
        {var b=Find<Button>(dialog,action);var p=b.TranslatePoint(new Point(),root);check(p.Y>=-0.1 && p.Y+b.ActualHeight<=root.ActualHeight+0.1,"Routine action fits "+name+"/"+action);}
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);
        var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var r=new Rect(0,0,root.ActualWidth,root.ActualHeight);dc.DrawRectangle(dialog.Background,null,r);dc.DrawRectangle(new VisualBrush(root),null,r);}bitmap.Render(drawing);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/m762-routine-"+name+".png");encoder.Save(file);
    }
}
