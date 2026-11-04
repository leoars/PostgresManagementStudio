using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ActivityDialogChecks
{
    internal static T Find<T>(ActivityHost window,string name)where T:FrameworkElement=>(T)window.FindName(name);
    internal static async Task Capture(ActivityHost window,string name,bool small,Action<bool,string> check)
    {
        window.Width=small?850:1100;window.Height=small?500:650;await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();var root=(FrameworkElement)window.Content;
        foreach(string field in new[]{"RefreshButton","ApplyFiltersButton","AutoRefreshIntervalSelector"})
        {
            var control=Find<FrameworkElement>(window,field);var point=control.TranslatePoint(new Point(),root);
            check(point.X>=0 && point.X+control.ActualWidth<=root.ActualWidth+0.1,"Activity action fits "+name+"/"+field);
        }
        check(Find<DataGrid>(window,"Sessions").ActualHeight>=70,"Activity grid retains a visible data row with scrollbar: "+name);
        var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var bounds=new Rect(0,0,root.ActualWidth,root.ActualHeight);dc.DrawRectangle(window.Background,null,bounds);dc.DrawRectangle(new VisualBrush(root),null,bounds);}
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/"+name+".png");encoder.Save(file);
    }
    private static async Task CaptureWindow(Window window,string name)
    {
        await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();var root=(FrameworkElement)window.Content;
        var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var bounds=new Rect(0,0,root.ActualWidth,root.ActualHeight);dc.DrawRectangle(window.Background,null,bounds);dc.DrawRectangle(new VisualBrush(root),null,bounds);}
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/"+name+".png");encoder.Save(file);
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var worker=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){ApplicationName="PGMS.ActivityUI",Pooling=false}.ConnectionString);await worker.OpenAsync();await using var transaction=await worker.BeginTransactionAsync();
        await using(var command=new NpgsqlCommand("SELECT 1",worker,transaction))await command.ExecuteNonQueryAsync();
        var monitor=new ActivityHost(connection){ShowInTaskbar=false};Find<CheckBox>(monitor,"AutoRefreshCheck").IsChecked=false;Find<TextBox>(monitor,"PidFilter").Text=worker.ProcessID.ToString();monitor.Show();var grid=Find<DataGrid>(monitor,"Sessions");
        check(grid.Columns.Select(c=>(string)c.Header).SequenceEqual(["PID","Database","User","Application","Host","State","Query age","Transaction age","Wait","Blocked by PID","Head blocker"]),"Activity grid adds Host and Head blocker columns in SSMS order");
        check(grid.Columns.All(c=>c.HeaderStyle?.Setters.OfType<Setter>().Any(s=>s.Property==FrameworkElement.ToolTipProperty && s.Value is string { Length: >0 })==true),"Every activity column header describes its pg_stat_activity source");
        var menu=grid.ContextMenu.Items.OfType<MenuItem>().Select(m=>(string)m.Header).ToArray();
        check(menu.SequenceEqual(["_Details…","_Cancel Query","_Terminate Session…"]),"Session rows offer Details, Cancel Query and Terminate Session like the SSMS row menu");
        check(monitor.CreateDetails() is null,"Details needs a selected session");
        async Task<ActivitySession> WaitState(string state)
        {
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(DateTime.UtcNow<deadline)
            {
                await monitor.ReloadAsync();var row=grid.Items.OfType<ActivitySession>().SingleOrDefault(s=>s.Pid==worker.ProcessID);
                if(row?.State==state)return row;await Task.Delay(30);
            }
            throw new Exception("Activity window did not show synthetic state: "+state);
        }
        try
        {
            var idle=await WaitState("idle in transaction");
            check(idle.QueryAge=="—" && idle.TransactionAge!="—","Activity UI distinguishes idle query from open transaction age");
            check(grid.Columns.Single(c=>(string)c.Header=="Query age").SortMemberPath=="QueryDuration" && grid.Columns.Single(c=>(string)c.Header=="Transaction age").SortMemberPath=="TransactionDuration","Duration columns sort by TimeSpan rather than formatted text");
            var origin=DateTime.UtcNow;var days=idle with{State="active",QueryStart=origin,ObservedAt=origin.AddDays(2).AddHours(3).AddMinutes(4).AddSeconds(5)};
            check(days.QueryAge=="2.03:04:05","Duration label retains days beyond 24 hours");
            await using var sleep=new NpgsqlCommand("SELECT pg_sleep(15)",worker,transaction);Task pending=sleep.ExecuteNonQueryAsync();
            try
            {
                var active=await WaitState("active");grid.SelectedItem=active;
                check(active.QueryAge!="—" && active.TransactionAge!="—","WPF shows both ages for an actual running query");
                await Capture(monitor,"m873-activity-normal",false,check);await Capture(monitor,"m873-activity-small",true,check);
                await monitor.ReloadAsync();check(grid.SelectedItem is ActivitySession selected && selected.Pid==worker.ProcessID && selected.BackendStart==active.BackendStart,"Age refresh retains exact selected backend identity");
            }
            finally{sleep.Cancel();try{await pending;}catch(OperationCanceledException){}catch(PostgresException ex) when(ex.SqlState=="57014"){}}
            await transaction.RollbackAsync();var ended=await WaitState("idle");check(ended.TransactionAge=="—" && ended.QueryAge=="—","Idle session clears displayed running ages after rollback");
            var blockedOnly=Find<CheckBox>(monitor,"BlockedOnlyFilter");blockedOnly.IsChecked=true;await monitor.ReloadAsync();
            check(grid.Items.Count==0,"WPF blocked-only choice excludes idle unblocked session");
            foreach(string name in new[]{"DatabaseFilter","UserFilter","ApplicationFilter","PidFilter","StateFilter","BlockedOnlyFilter"})check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(monitor,name))),"Activity filter has accessible name: "+name);
            await using var waiter=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){ApplicationName="PGMS.ActivityUI.Waiter",Pooling=false}.ConnectionString);await waiter.OpenAsync();long key=Random.Shared.NextInt64(1,long.MaxValue);
            await using(var acquire=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",worker)){acquire.Parameters.AddWithValue("key",key);await acquire.ExecuteNonQueryAsync();}
            try
            {
                await using var waiting=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",waiter);waiting.Parameters.AddWithValue("key",key);Task blocked=waiting.ExecuteNonQueryAsync();
                try
                {
                    Find<TextBox>(monitor,"PidFilter").Text=waiter.ProcessID.ToString();var deadline=DateTime.UtcNow.AddSeconds(10);
                    while(DateTime.UtcNow<deadline && grid.Items.Count==0){await monitor.ReloadAsync();if(grid.Items.Count==0)await Task.Delay(25);}
                    check(grid.Items.OfType<ActivitySession>().Single().BlockingPids.Contains(worker.ProcessID),"WPF blocked-only filter shows real waiter and blocker identity");
                    Task reload=monitor.ReloadAsync();check(!blockedOnly.IsEnabled,"Running refresh freezes blocked-only choice");await reload;
                    await Capture(monitor,"m875-blocked-normal",false,check);await Capture(monitor,"m875-blocked-small",true,check);
                    var blockedRow=grid.Items.OfType<ActivitySession>().Single();grid.SelectedItem=blockedRow;
                    var details=monitor.CreateDetails()!;details.ShowInTaskbar=false;details.Show();
                    string Text(string name)=>((TextBlock)details.FindName(name)).Text;
                    check(details.Title==$"Session Details — {blockedRow.User} — {blockedRow.Pid}" && Text("BlockersValue")==worker.ProcessID.ToString() && Text("HeadBlockerValue")=="No" && Text("HostValue")=="127.0.0.1" && ((TextBox)details.FindName("QueryText")).Text.Contains("pg_advisory_lock",StringComparison.Ordinal),"Details shows the blocked session, its blocker, host and query text");
                    await details.RefreshAsync();check(details.Session.BackendStart==blockedRow.BackendStart && ((TextBlock)details.FindName("Status")).Text.StartsWith("Refreshed locally",StringComparison.Ordinal),"Details Refresh rereads the same backend");
                    await CaptureWindow(details,"m1026-session-details");details.Close();
                    blockedOnly.IsChecked=false;Find<TextBox>(monitor,"PidFilter").Text=worker.ProcessID.ToString();await monitor.ReloadAsync();
                    check(grid.Items.OfType<ActivitySession>().Single() is {HeadBlocker:"Yes",Host:"127.0.0.1"},"Activity window marks the real lock holder as head blocker");
                    await Capture(monitor,"m1026-head-blocker-normal",false,check);
                    blockedOnly.IsChecked=true;Find<TextBox>(monitor,"PidFilter").Text=waiter.ProcessID.ToString();await monitor.ReloadAsync();
                }
                finally{waiting.Cancel();try{await blocked;}catch(OperationCanceledException){}catch(PostgresException ex) when(ex.SqlState=="57014"){}}
                await monitor.ReloadAsync();check(grid.Items.Count==0,"Canceled wait disappears from blocked-only UI after refresh");
                blockedOnly.IsChecked=false;await monitor.ReloadAsync();check(grid.Items.OfType<ActivitySession>().Single().Pid==waiter.ProcessID,"Removing blocked filter restores idle session to UI");
            }
            finally{await using var release=new NpgsqlCommand("SELECT pg_advisory_unlock(@key)",worker);release.Parameters.AddWithValue("key",key);await release.ExecuteNonQueryAsync();}
            await using var victim=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){ApplicationName="PGMS.ActivityUI.Victim",Pooling=false}.ConnectionString);await victim.OpenAsync();
            Find<TextBox>(monitor,"PidFilter").Text=victim.ProcessID.ToString();await monitor.ReloadAsync();grid.SelectedItem=grid.Items.OfType<ActivitySession>().Single();
            var terminating=monitor.CreateDetails()!;terminating.ShowInTaskbar=false;terminating.Show();
            ActivitySession? asked=null;terminating.ConfirmTermination=s=>{asked=s;return false;};await terminating.TerminateAsync();
            check(asked?.Pid==victim.ProcessID && !terminating.Terminated && victim.State==System.Data.ConnectionState.Open,"Declining the Details confirmation sends no signal");
            terminating.ConfirmTermination=_=>true;await terminating.TerminateAsync();
            check(terminating.Terminated && !((Button)terminating.FindName("TerminateButton")).IsEnabled && !((Button)terminating.FindName("RefreshButton")).IsEnabled,"Details Terminate Session confirms the real termination and disables further actions");
            terminating.Close();
            var gone=DateTime.UtcNow.AddSeconds(10);while(DateTime.UtcNow<gone && grid.Items.Count>0){await monitor.ReloadAsync();if(grid.Items.Count>0)await Task.Delay(25);}
            check(grid.Items.Count==0,"Terminated session leaves the monitor after refresh");
            var endedDetails=new SessionDetailsWindow(new ActivityService(connection),idle with{Pid=victim.ProcessID}){ShowInTaskbar=false};endedDetails.Show();await endedDetails.RefreshAsync();
            check(endedDetails.Ended && ((TextBlock)endedDetails.FindName("Status")).Text.Contains("has ended",StringComparison.Ordinal),"Details Refresh reports an ended session and keeps the last details");
            endedDetails.Close();
        }
        finally{monitor.Close();}
        await ActivityCancellationChecks.RunAsync(connection,check);
        await ActivitySnapshotUiChecks.RunAsync(connection,check);
        await ActivityPanelChecks.RunAsync(connection,check);
    }
}
