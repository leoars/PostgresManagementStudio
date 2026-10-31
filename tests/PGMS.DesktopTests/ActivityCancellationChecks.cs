using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ActivityCancellationChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string application="PGMS.ActivityCancel."+Guid.NewGuid().ToString("N")[..10];
        string pooled=new NpgsqlConnectionStringBuilder(connection){ApplicationName=application,Pooling=true,MinPoolSize=1,MaxPoolSize=1}.ConnectionString;
        await using var observer=new NpgsqlConnection(connection);await observer.OpenAsync();
        await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();
        var monitor=new ActivityWindow(pooled){ShowInTaskbar=false};ActivityDialogChecks.Find<TextBox>(monitor,"PidFilter").Text=observer.ProcessID.ToString();monitor.Show();var grid=ActivityDialogChecks.Find<DataGrid>(monitor,"Sessions");
        Task? pending=null;
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(grid.Items.Count==0 && DateTime.UtcNow<deadline){await monitor.ReloadAsync();await Task.Delay(25);}
            check(grid.Items.OfType<ActivitySession>().Single().Pid==observer.ProcessID,"Cancellation fixture has a real previous activity snapshot");
            int previous=monitor.SnapshotVersion;
            int monitorPid;await using(var pid=new NpgsqlCommand("SELECT pid FROM pg_stat_get_activity(NULL) WHERE application_name=@app",observer)){pid.Parameters.AddWithValue("app",application);monitorPid=Convert.ToInt32(await pid.ExecuteScalarAsync());}
            await using var blockedQuery=new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid))>0",observer);blockedQuery.Parameters.AddWithValue("pid",monitorPid);await blockedQuery.ExecuteScalarAsync();
            ActivityDialogChecks.Find<ComboBox>(monitor,"AutoRefreshIntervalSelector").SelectedIndex=3;ActivityDialogChecks.Find<CheckBox>(monitor,"AutoRefreshCheck").IsChecked=true;
            await using(var transaction=await blocker.BeginTransactionAsync())
            {
                await using(var lockView=new NpgsqlCommand("LOCK TABLE pg_catalog.pg_stat_activity IN ACCESS EXCLUSIVE MODE",blocker,transaction)){await lockView.ExecuteNonQueryAsync();}
                pending=monitor.ReloadAsync();bool observed=false;deadline=DateTime.UtcNow.AddSeconds(10);
                try
                {
                    while(DateTime.UtcNow<deadline && !pending.IsCompleted){observed=(bool)(await blockedQuery.ExecuteScalarAsync())!;if(observed)break;await Task.Delay(25);}
                    check(observed,"Monitor refresh is observed waiting on an actual isolated catalog lock");
                    var cancel=ActivityDialogChecks.Find<Button>(monitor,"CancelRefreshButton");
                    check(cancel.IsVisible && cancel.IsEnabled && !ActivityDialogChecks.Find<Button>(monitor,"CancelButton").IsEnabled,"Cancel Refresh is available independently of Cancel Query while loading");
                    monitor.Close();check(monitor.IsVisible,"Closing blocked monitor retains window until refresh cancellation");
                    await ActivityDialogChecks.Capture(monitor,"m876-refresh-normal",false,check);await ActivityDialogChecks.Capture(monitor,"m876-refresh-small",true,check);
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(!cancel.IsEnabled,"Cancel Refresh disables repeated cancellation");await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    check(previous==monitor.SnapshotVersion && ActivityDialogChecks.Find<TextBox>(monitor,"Status").Text.Contains("Previous snapshot retained"),"Canceled refresh retains prior snapshot and labels it explicitly");
                    check(!monitor.IsAutoRefreshRunning && ActivityDialogChecks.Find<CheckBox>(monitor,"AutoRefreshCheck").IsChecked==false,"Cancel Refresh pauses automatic retries");
                    check(ActivityDialogChecks.Find<Button>(monitor,"RefreshButton").IsEnabled && !cancel.IsVisible,"Canceled refresh releases controls and hides its cancellation action");
                }
                finally
                {
                    ActivityDialogChecks.Find<Button>(monitor,"CancelRefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await transaction.RollbackAsync();if(pending is not null)await pending.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            await monitor.ReloadAsync();check(previous!=monitor.SnapshotVersion && grid.Items.Count==1,"Explicit refresh succeeds after cancellation and lock release");
        }
        finally{monitor.Close();await using var poolKey=new NpgsqlConnection(pooled);NpgsqlConnection.ClearPool(poolKey);}
    }
}
