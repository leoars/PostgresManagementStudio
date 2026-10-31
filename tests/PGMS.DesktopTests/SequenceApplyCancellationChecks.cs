using Npgsql;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequenceApplyCancellationChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string name="pgms_cancel_apply_"+Guid.NewGuid().ToString("N"), seq=ObjectSql.Relation("public",name);
        await using var db=new NpgsqlConnection(connection); await db.OpenAsync();
        async Task Sql(string sql) { await using var c=new NpgsqlCommand(sql,db); await c.ExecuteNonQueryAsync(); }
        try
        {
            await Sql($"CREATE SEQUENCE {seq};");
            foreach(string route in new[]{"button","escape","close"})
            {
                await Sql($"ALTER SEQUENCE {seq} CACHE 1;");
                SequenceRestartChecks.Show(new SequenceDialog(connection,"public",name){ShowInTaskbar=false},async w=>
                {
                    ((ComboBox)w.FindName("Operation")).SelectedItem=SequenceOperation.SetCache;
                    ((TextBox)w.FindName("ParameterValue")).Text="7"; await w.InspectAsync();
                    await using var tx=await db.BeginTransactionAsync();
                    await using(var c=new NpgsqlCommand($"ALTER SEQUENCE {seq} CACHE 1",db,tx)) await c.ExecuteNonQueryAsync();
                    try
                    {
                        ((TextBox)w.FindName("ParameterValue")).Focus();
                        var pending=w.ApplyAsync();
                        await WaitForLock(connection,name);
                        var cancel=(Button)w.FindName("CancelApplyButton");
                        check(cancel.IsVisible && cancel.IsEnabled && !((Button)w.FindName("ApplyButton")).IsEnabled,"Blocked Apply exposes cancellation and protects duplicate actions: "+route);
                        await w.ApplyAsync();
                        if(route=="button") cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else if(route=="escape") w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(w),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                        else { w.Close(); check(w.IsVisible,"Closing waits for Apply cleanup"); }
                        check(!cancel.IsEnabled,"Cancel Apply refuses repeated cancellation: "+route);
                        await pending;
                        check(!w.WasApplied && w.Snapshot is null,"Canceled Apply requires new inspection: "+route);
                        check(w.IsVisible==(route!="close"),"Apply cancellation preserves or closes window according to intent: "+route);
                        if(route!="close") check(((TextBlock)w.FindName("Feedback")).Text.Contains("Inspect") && !((Button)w.FindName("ApplyButton")).IsEnabled,"Canceled Apply explains outcome uncertainty and blocks retry without inspection: "+route);
                    }
                    finally { await tx.RollbackAsync(); }
                    if(route=="button")
                    {
                        check(((Button)w.FindName("InspectButton")).IsKeyboardFocusWithin,"Canceled Apply focuses required reinspection");
                        await w.InspectAsync();
                        check(((TextBox)w.FindName("ParameterValue")).Text=="7" && ((Button)w.FindName("ApplyButton")).IsEnabled,"Reinspection preserves proposal and enables retry");
                        await w.ApplyAsync(); check(w.WasApplied,"Apply succeeds after cancellation and reinspection");
                    }

                });
                check((await new SequenceService(connection).InspectAsync("public",name)).Cache==(route=="button"?7:1),"Canceled Apply persists only an explicitly retried proposal: "+route);
            }
            SequenceRestartChecks.Show(new SequenceDialog(connection,"public",name){ShowInTaskbar=false},async w=>
            {
                ((ComboBox)w.FindName("Operation")).SelectedItem=SequenceOperation.Restart;
                ((TextBox)w.FindName("ParameterValue")).Text="50"; await w.InspectAsync();
                await using var tx=await db.BeginTransactionAsync();
                await using(var c=new NpgsqlCommand($"ALTER SEQUENCE {seq} CACHE 1",db,tx)) await c.ExecuteNonQueryAsync();
                try
                {
                    var pending=SequenceConfirmationChecks.RespondAsync(w,true,check);
                    await WaitForLock(connection,name);
                    await SequenceRestartChecks.Capture(w,"sequence-apply-cancel",check);
                    ((Button)w.FindName("CancelApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await pending;
                    check(!w.WasApplied && w.IsVisible && w.Snapshot is null,"Confirmed restart can be canceled while awaiting lock");
                }
                finally { await tx.RollbackAsync(); }
            });
            await using(var c=new NpgsqlCommand($"SELECT last_value::text||':'||is_called::text FROM {seq}",db))
                check((string?)await c.ExecuteScalarAsync()=="1:false","Canceled confirmed restart does not consume or reset sequence");
        }
        finally { await Sql($"DROP SEQUENCE IF EXISTS {seq};"); }
    }
    internal static async Task WaitForLock(string connection,string name)
    {
        await using var observer=new NpgsqlConnection(connection); await observer.OpenAsync();
        DateTime deadline=DateTime.UtcNow.AddSeconds(8);
        while(DateTime.UtcNow<deadline)
        {
            await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid<>pg_backend_pid() AND wait_event_type='Lock' AND position(@name in query)>0)",observer);
            c.Parameters.AddWithValue("name",name);
            if((bool)(await c.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        throw new Exception("Apply did not reach server lock wait.");
    }
}
