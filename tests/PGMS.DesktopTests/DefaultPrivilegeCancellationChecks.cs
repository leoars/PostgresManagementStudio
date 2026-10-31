using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class DefaultPrivilegeCancellationChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),owner="dc_owner_"+suffix,role="dc_role_"+suffix,schema="dc_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(role),qs=SqlIdentifier.Quote(schema),app="PGMS.DefaultCancel."+suffix;
        string owned=new NpgsqlConnectionStringBuilder(connection){Username=owner,ApplicationName=app,Pooling=false}.ConnectionString;
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        var request=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,owner,role,"SELECT",schema);
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            await Sql((request with{Privilege="INSERT"}).ToSql());
            foreach(string route in new[]{"button","escape","close","conflict"})
            {
                var dialog=new DefaultPrivilegeDialog(owned){ShowInTaskbar=false,Opacity=0,Width=650,Height=520};Task? pending=null;
                T Find<T>(string name)where T:FrameworkElement=>(T)dialog.FindName(name);
                await using var locker=new NpgsqlConnection(connection);await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();
                try
                {
                    dialog.Show();Find<TextBox>("RoleName").Text=role;Find<TextBox>("SchemaName").Text=schema;Find<CheckBox>("GlobalScope").IsChecked=false;
                    await dialog.InspectAsync();var original=dialog.Snapshot;
                    await using(var c=new NpgsqlCommand((request with{Privilege="UPDATE"}).ToSql(),locker,tx))await c.ExecuteNonQueryAsync();
                    pending=dialog.ApplyAsync();bool blocked=false;var until=DateTime.UtcNow.AddSeconds(8);
                    while(!pending.IsCompleted && DateTime.UtcNow<until)
                    {
                        await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                        c.Parameters.AddWithValue("app",app);c.Parameters.AddWithValue("pid",locker.ProcessID);blocked=(bool)(await c.ExecuteScalarAsync())!;if(blocked)break;await Task.Delay(20);
                    }
                    check(blocked && Find<Button>("CancelButton").IsEnabled && !Find<Grid>("Fields").IsEnabled,"Default window exposes Cancel while waiting on exact policy lock: "+route);
                    await dialog.ApplyAsync();await dialog.InspectAsync();check(ReferenceEquals(original,dialog.Snapshot) && !pending.IsCompleted,"Duplicate default Apply/Inspect cannot replace running operation: "+route);
                    dialog.UpdateLayout();var root=(FrameworkElement)dialog.Content;var cancel=Find<Button>("CancelButton");var end=cancel.TranslatePoint(new Point(cancel.ActualWidth,0),root);
                    check(end.X<=Find<Button>("ScriptButton").TranslatePoint(new Point(),root).X && end.Y+cancel.ActualHeight<=root.ActualHeight,"Default Cancel fits minimum window without overlapping actions: "+route);
                    if(route=="conflict")await tx.CommitAsync();
                    else
                    {
                        if(route=="button")cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else if(route=="escape")dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                        else dialog.Close();
                        check(dialog.IsVisible && !cancel.IsEnabled,"Default cancellation waits for completion and disables duplicate request: "+route);
                    }
                    await pending.WaitAsync(TimeSpan.FromSeconds(10));if(route!="conflict")await tx.RollbackAsync();
                    check(dialog.IsVisible && Find<Grid>("Fields").IsEnabled && !Find<Button>("ApplyButton").IsEnabled && !Find<Button>("ScriptButton").IsEnabled,"Default canceled/conflicting operation requires inspection and preserves window: "+route);
                    check(Find<TextBox>("RoleName").Text==role && Find<TextBox>("Preview").Text==request.ToSql(),"Default canceled/conflicting proposal remains intact: "+route);
                    await dialog.InspectAsync();check(dialog.Snapshot is not null && !dialog.Snapshot.Entries.Any(e=>e.Recipient==role && e.Privilege=="SELECT"),"Default reinspection proves no SELECT after cancellation/conflict: "+route);
                    await dialog.ApplyAsync();check((await new DefaultPrivilegeCatalog(owned).InspectAsync(request.Target,owner,schema)).Entries.Any(e=>e.Recipient==role && e.Privilege=="SELECT"),"Explicit reinspected default retry succeeds: "+route);
                    await Sql((request with{Revoke=true}).ToSql());if(route=="conflict")await Sql((request with{Privilege="UPDATE",Revoke=true}).ToSql());
                }
                finally{dialog.CancelCurrentOperation();if(pending is {IsCompleted:false}){try{await tx.RollbackAsync();}catch(InvalidOperationException){}await pending.WaitAsync(TimeSpan.FromSeconds(10));}dialog.Close();}
            }
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo}");}
    }
}
