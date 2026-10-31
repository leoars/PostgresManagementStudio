using Npgsql;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PrivilegeCancellationDialogChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="ui_cancel_"+suffix,role="ui_cancel_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        string app="PGMS.UI.Cancel."+suffix;
        string uiConnection=new NpgsqlConnectionStringBuilder(connection){ApplicationName=app}.ConnectionString;
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        try
        {
            await Sql($"CREATE SCHEMA {qs};CREATE TABLE {table}(id integer);CREATE ROLE {qr};");
            using(var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0))
            {
                listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var stalled=new PrivilegeDialog($"Host=127.0.0.1;Port={port};Database=postgres;Username=pgms_test;SSL Mode=Disable;Pooling=false;Timeout=2"){ShowInTaskbar=false};
                Task? startup=null;
                try
                {
                    stalled.Show();((TextBox)stalled.FindName("RoleName")).Text=role;
                    startup=stalled.ApplyAsync();
                    check(stalled.IsBusy && ((Button)stalled.FindName("CancelInspectionButton")).IsEnabled,"Connection startup exposes Apply cancellation before PostgreSQL handshake");
                    stalled.CancelCurrentOperation();await startup.WaitAsync(TimeSpan.FromSeconds(6));
                    check(!stalled.IsBusy && stalled.IsVisible && ((TextBlock)stalled.FindName("Feedback")).Text.Contains("Result not confirmed"),"Startup cancellation finishes within connection timeout with honest outcome");
                    check(stalled.AccessSnapshot is null && ((TextBox)stalled.FindName("RoleName")).Text==role,"Canceled startup creates no snapshot and preserves proposal");
                    stalled.CancelCurrentOperation();
                    check(((Button)stalled.FindName("CancelInspectionButton")).Visibility==Visibility.Collapsed,"Cancel at idle has no effect after resource cleanup");
                }
                finally {stalled.CancelCurrentOperation();if(startup is not null)await startup.WaitAsync(TimeSpan.FromSeconds(6));stalled.Close();listener.Stop();}
            }
            foreach(string route in new[]{"button","escape","close"})
            {
                var dialog=new PrivilegeDialog(uiConnection){ShowInTaskbar=false};
                T Find<T>(string name) where T:FrameworkElement => (T)dialog.FindName(name);
                Task? pending=null;
                try
                {
                    dialog.Show();dialog.Activate();
                    Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
                    Find<TextBox>("SchemaName").Text=schema;Find<TextBox>("ObjectName").Text="target";Find<TextBox>("RoleName").Text=role;
                    Find<ComboBox>("Privilege").SelectedItem="SELECT";
                    await dialog.InspectAsync();var before=dialog.AccessSnapshot;
                    Find<TabControl>("PermissionTabs").SelectedIndex=0;
                    Find<TextBox>("RoleName").Focus();
                    await using var locker=new NpgsqlConnection(connection);await locker.OpenAsync();
                    await using var tx=await locker.BeginTransactionAsync();
                    await using(var c=new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_class WHERE oid=@table::regclass FOR UPDATE",locker,tx))
                    {c.Parameters.AddWithValue("table",table);await c.ExecuteScalarAsync();}
                    try
                    {
                        pending=dialog.ApplyAsync();
                        bool waiting=false;var until=DateTime.UtcNow.AddSeconds(8);
                        while(!pending.IsCompleted && DateTime.UtcNow<until)
                        {
                            await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                            c.Parameters.AddWithValue("app",app);c.Parameters.AddWithValue("pid",locker.ProcessID);
                            if((bool)(await c.ExecuteScalarAsync())!){waiting=true;break;}await Task.Delay(40);
                        }
                        check(waiting,"WPF Apply reached its exact catalog lock: "+route);
                        var cancel=Find<Button>("CancelInspectionButton");
                        check(dialog.IsBusy && cancel.IsVisible && cancel.IsEnabled && !Find<StackPanel>("Fields").IsEnabled && !Find<Button>("CloseButton").IsEnabled,"Pending Apply protects inputs and exposes Cancel Apply");
                        check(UIElementAutomationPeer.CreatePeerForElement(cancel)?.GetName()=="Cancel permission Apply","Apply cancellation has operation-specific automation name");
                        if(route=="button") foreach(bool small in new[]{false,true})
                        {
                            dialog.Width=small?dialog.MinWidth:760;dialog.Height=small?dialog.MinHeight:760;
                            await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                            var content=(FrameworkElement)dialog.Content;
                            foreach(string name in new[]{"InspectButton","CancelInspectionButton","ScriptButton","ApplyButton","CloseButton"})
                            {
                                var action=Find<Button>(name);var point=action.TranslatePoint(new Point(),content);
                                check(point.X>=-0.5 && point.Y>=-0.5 && point.X+action.ActualWidth<=content.ActualWidth+0.5 && point.Y+action.ActualHeight<=content.ActualHeight+0.5,"Apply action fits "+(small?"minimum":"normal")+" viewport: "+name);
                            }
                            var cancelEnd=cancel.TranslatePoint(new Point(cancel.ActualWidth,0),content).X;
                            check(cancelEnd<=Find<Button>("ScriptButton").TranslatePoint(new Point(),content).X+0.5,"Cancel Apply does not overlap right actions at "+(small?"minimum":"normal")+" size");
                            if(Environment.GetEnvironmentVariable("PGMS_PRIVILEGE_CANCEL_REVIEW")=="1") Capture(dialog,"m492-apply-cancel-"+(small?"small":"normal"));
                        }
                        await dialog.ApplyAsync();await dialog.InspectAsync();
                        check(dialog.IsBusy && ReferenceEquals(before,dialog.AccessSnapshot),"Duplicate Apply and F5 inspection do not replace running operation");
                        if(route=="button") cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else if(route=="escape") dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                        else {dialog.Close();check(dialog.IsVisible,"Close requests cancellation and waits for resource disposal");}
                        check(!cancel.IsEnabled && Find<TextBlock>("Feedback").Text.Contains("Canceling Apply"),"Cancel request disables repeated clicks and explains wait");
                        dialog.CancelCurrentOperation();
                        await pending.WaitAsync(TimeSpan.FromSeconds(8));
                        check(!dialog.IsBusy && dialog.IsVisible && Find<Button>("ApplyButton").IsEnabled && cancel.Visibility==Visibility.Collapsed,"Canceled Apply releases busy controls and retains dialog");
                        check(ReferenceEquals(before,dialog.AccessSnapshot) && dialog.AccessSnapshotIsStale && Find<DataGrid>("AclRows").Items.Count>0,"Canceled Apply preserves previous ACL marked stale");
                        check(Find<TextBlock>("Feedback").Text.Contains("Result not confirmed") && Find<TextBlock>("Feedback").Text.Contains("inspect"),"Cancellation feedback requires inspection without asserting rollback");
                        check(Find<TextBox>("RoleName").Text==role && Find<TextBox>("Preview").Text.Contains(qr),"Canceled Apply preserves recipient and reviewable SQL");
                        check(Find<TextBox>("RoleName").IsKeyboardFocused,"Canceled Apply restores original field focus");
                    }
                    finally {dialog.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending.WaitAsync(TimeSpan.FromSeconds(8));}
                    await dialog.InspectAsync();
                    check(!dialog.AccessSnapshotIsStale && !dialog.AccessSnapshot!.AclEntries.Any(e=>e.Grantee==role),"Reinspection proves no grant after canceled Apply");
                    await dialog.ApplyAsync();await dialog.InspectAsync();
                    check(dialog.AccessSnapshot!.AclEntries.Any(e=>e.Grantee==role && e.Privilege=="SELECT"),"Explicit WPF retry commits permission after cancellation");
                    await Sql($"REVOKE SELECT ON TABLE {table} FROM {qr};");
                }
                finally {dialog.CancelCurrentOperation();if(pending is not null)await pending.WaitAsync(TimeSpan.FromSeconds(8));dialog.Close();}
            }
            var failed=new PrivilegeDialog(uiConnection){ShowInTaskbar=false};
            try
            {
                failed.Show();((ComboBox)failed.FindName("ObjectType")).SelectedItem=PrivilegeTarget.Table;
                ((TextBox)failed.FindName("SchemaName")).Text=schema;((TextBox)failed.FindName("ObjectName")).Text="target";
                ((ComboBox)failed.FindName("Privilege")).SelectedItem="SELECT";((TextBox)failed.FindName("RoleName")).Text=role+"_missing";
                await failed.InspectAsync();var prior=failed.AccessSnapshot;await failed.ApplyAsync();
                check(((TextBlock)failed.FindName("Feedback")).Text.Contains("42704") && ((TextBlock)failed.FindName("Feedback")).Text.Contains("Inspect access before retrying"),"Server SQLSTATE failure preserves diagnosis and recovery instruction");
                check(!failed.IsBusy && ReferenceEquals(prior,failed.AccessSnapshot) && failed.AccessSnapshotIsStale && ((Button)failed.FindName("ApplyButton")).IsEnabled,"Failed Apply retains stale snapshot and releases retry controls");
                ((TextBox)failed.FindName("RoleName")).Text=role;await failed.ApplyAsync();
                check(((TextBlock)failed.FindName("Feedback")).Text.StartsWith("Command completed"),"Correcting the recipient permits confirmed Apply after server failure");
                failed.CancelCurrentOperation();await failed.InspectAsync();
                check(failed.AccessSnapshot!.AclEntries.Any(e=>e.Grantee==role && e.Privilege=="SELECT"),"Idle cancel after confirmed commit does not undo its grant");
            }
            finally {failed.CancelCurrentOperation();failed.Close();}
        }
        finally {await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr};");}
    }
    private static void Capture(Window window,string name)
    {
        var element=(FrameworkElement)window.Content;element.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var visual=new System.Windows.Media.DrawingVisual();using(var drawing=visual.RenderOpen())
        {
            var bounds=new Rect(0,0,element.ActualWidth,element.ActualHeight);drawing.DrawRectangle(window.Background,null,bounds);drawing.DrawRectangle(new System.Windows.Media.VisualBrush(element),null,bounds);
        }
        bitmap.Render(visual);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory("artifacts/review");using var output=System.IO.File.Create($"artifacts/review/{name}.png");encoder.Save(output);
    }
}
