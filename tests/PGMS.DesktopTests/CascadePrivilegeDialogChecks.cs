using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class CascadePrivilegeDialogChecks
{
    internal static async Task RunAsync(string connection, Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="cascade_ui_"+suffix, owner="cascade_owner_"+suffix, recipient="cascade_recipient_"+suffix, child="cascade_child_"+suffix;
        string qs=SqlIdentifier.Quote(schema), qo=SqlIdentifier.Quote(owner), qr=SqlIdentifier.Quote(recipient), qc=SqlIdentifier.Quote(child);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var command=new NpgsqlCommand(sql,admin);await command.ExecuteNonQueryAsync();}
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false,ApplicationName="PGMS.CascadeUi."+suffix}.ConnectionString;
        var dialog=new PrivilegeDialog(limited){ShowInTaskbar=false};
        T Find<T>(string name) where T:FrameworkElement => (T)dialog.FindName(name);
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr} LOGIN;CREATE ROLE {qc};CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TABLE {qs}.items(value integer);GRANT USAGE ON SCHEMA {qs} TO {qr};GRANT SELECT ON {qs}.items TO {qr} WITH GRANT OPTION;RESET ROLE;SET ROLE {qr};GRANT SELECT ON {qs}.items TO {qc};RESET ROLE");
            Exception? failure=null;
            dialog.ContentRendered+=async(_,_)=>
            {
                try
                {
                    Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
                    Find<TextBox>("SchemaName").Text=schema;Find<TextBox>("ObjectName").Text="items";Find<TextBox>("RoleName").Text=recipient;
                    Find<CheckBox>("Revoke").IsChecked=true;
                    Find<Expander>("DelegationPanel").IsExpanded=true;
                    check(!Find<CheckBox>("CascadeRevoke").IsEnabled,"CASCADE disabled until grantor is explicitly selected");
                    await dialog.LoadGrantorsAsync();
                    check(Find<ComboBox>("GrantorRoles").SelectedIndex==-1,"Loading grantors never silently selects an origin");
                    Find<ComboBox>("GrantorRoles").SelectedItem=Find<ComboBox>("GrantorRoles").Items.Cast<PrivilegeGrantorCandidate>().Single(x=>x.Name==owner);
                    check(Find<TextBox>("Preview").Text.Contains("SET LOCAL ROLE "+qo),"Explicit grantor SQL uses inspected role name");
                    Find<CheckBox>("CascadeRevoke").IsChecked=true;
                    check(!Find<Button>("ApplyButton").IsEnabled,"CASCADE requires a fresh dependency review");
                    await dialog.ReviewRevokeAsync();
                    check(Find<Button>("ApplyButton").IsEnabled && Find<TextBox>("RevokeImpact").Text.Contains(child),"Reviewed CASCADE shows dependent recipient and enables confirmation");
                    Find<ScrollViewer>("ContentScroller").ScrollToVerticalOffset(280);
                    await Capture(dialog,"normal",760,760,check);
                    Find<ScrollViewer>("ContentScroller").ScrollToVerticalOffset(430);
                    await Capture(dialog,"minimum",650,540,check);
                    await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,false,check);
                    check(Find<TextBlock>("Feedback").Text.Contains("Nothing was sent"),"Native No leaves reviewed cascading revoke unapplied");
                    check((await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"items",schema)).AclEntries.Any(x=>x.Grantee==child),"Native No preserves dependent grant in PostgreSQL");
                    Find<CheckBox>("GrantOption").IsChecked=true;
                    check(!Find<Button>("ApplyButton").IsEnabled && Find<ComboBox>("GrantorRoles").SelectedIndex==-1,"Changing revoke intent invalidates grantor selection and dependency review");
                    await dialog.LoadGrantorsAsync();
                    Find<ComboBox>("GrantorRoles").SelectedItem=Find<ComboBox>("GrantorRoles").Items.Cast<PrivilegeGrantorCandidate>().Single(x=>x.Name==owner);
                    await dialog.ReviewRevokeAsync();
                    await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                    var acl=await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"items",schema);
                    check(acl.AclEntries.Any(x=>x.Grantee==recipient && x.Privilege=="SELECT" && !x.GrantOption) && !acl.AclEntries.Any(x=>x.Grantee==child),"Confirmed WPF CASCADE preserves base privilege and removes dependent grant by common owner");
                    check(!Find<Button>("ApplyButton").IsEnabled && !dialog.HasUnconfirmedCommit,"Completed CASCADE invalidates preview and clears durable commit record");
                    await Sql($"SET ROLE {qo};GRANT SELECT ON {qs}.items TO {qr} WITH GRANT OPTION;RESET ROLE;SET ROLE {qr};GRANT SELECT ON {qs}.items TO {qc};RESET ROLE");
                    await dialog.ReviewRevokeAsync();
                    await using(var observer=new NpgsqlConnection(connection))
                    {
                        await observer.OpenAsync();await using var tx=await admin.BeginTransactionAsync();
                        await using(var locked=new NpgsqlCommand("SELECT oid FROM pg_catalog.pg_class WHERE oid=@name::regclass FOR UPDATE",admin,tx))
                        {locked.Parameters.AddWithValue("name",qs+".items");await locked.ExecuteScalarAsync();}
                        var pending=SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                        try
                        {
                            bool waiting=false;var until=DateTime.UtcNow.AddSeconds(10);
                            while(!pending.IsCompleted && DateTime.UtcNow<until)
                            {
                                await using var query=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)))",observer);
                                query.Parameters.AddWithValue("app","PGMS.CascadeUi."+suffix);query.Parameters.AddWithValue("blocker",admin.ProcessID);
                                if((bool)(await query.ExecuteScalarAsync())!){waiting=true;break;}await Task.Delay(20);
                            }
                            check(waiting && dialog.IsBusy && !Find<ComboBox>("GrantorRoles").IsEnabled && !Find<Button>("ApplyButton").IsEnabled,"Confirmed CASCADE waits on exact catalog lock with proposal and duplicate Apply blocked");
                            dialog.CancelCurrentOperation();await tx.RollbackAsync();await pending;
                            check(!dialog.IsBusy && !Find<Button>("ApplyButton").IsEnabled && Find<TextBlock>("Feedback").Text.Contains("interrupted"),"Canceled CASCADE restores controls but invalidates dependency review");
                            acl=await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"items",schema);
                            check(acl.AclEntries.Any(x=>x.Grantee==recipient && x.GrantOption) && acl.AclEntries.Any(x=>x.Grantee==child),"WPF cancellation under lock preserves root delegation and dependent grant");
                        }
                        finally{dialog.CancelCurrentOperation();if(!pending.IsCompleted){try{await tx.RollbackAsync();}catch(InvalidOperationException){}await pending;}}
                    }
                    Find<TextBox>("ObjectName").Text="missing";
                    check(Find<ComboBox>("GrantorRoles").SelectedIndex==-1 && !Find<Button>("ApplyButton").IsEnabled,"Changing target clears grantor identity and blocks stale CASCADE");
                }
                catch(Exception ex){failure=ex;}
                finally{dialog.Close();}
            };
            dialog.ShowDialog();if(failure is not null)throw failure;
        }
        finally{if(dialog.IsVisible)dialog.Close();await Sql($"RESET ROLE;DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE {qc},{qr},{qo}");}
    }
    internal static async Task Capture(Window window,string name,double width,double height,Action<bool,string> check,string milestone="m787")
    {
        window.Width=width;window.Height=height;
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        foreach(string button in new[]{"ApplyButton","CloseButton","InspectButton"})
        {
            var element=(FrameworkElement)window.FindName(button);var origin=element.TranslatePoint(new Point(),(UIElement)window.Content);
            check(origin.Y>=0 && origin.Y+element.ActualHeight<=((FrameworkElement)window.Content).ActualHeight+0.1,"CASCADE "+name+" viewport keeps "+button+" visible");
        }
        var content=(FrameworkElement)window.Content;
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var visual=new System.Windows.Media.DrawingVisual();
        using(var drawing=visual.RenderOpen()) { var bounds=new Rect(0,0,content.ActualWidth,content.ActualHeight);drawing.DrawRectangle(window.Background,null,bounds);drawing.DrawRectangle(new System.Windows.Media.VisualBrush(content),null,bounds); }
        bitmap.Render(visual);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory("artifacts/review");using var file=System.IO.File.Create("artifacts/review/"+milestone+"-cascade-"+name+".png");encoder.Save(file);
    }
}
