using System.Windows.Controls;
using System.ComponentModel;
using PGMS.PostgreSql;
using Npgsql;
using PGMS.Desktop;

internal static class ActivitySnapshotUiChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string app="PGMS.SnapshotUI."+Guid.NewGuid().ToString("N")[..10];
        string target=new NpgsqlConnectionStringBuilder(connection){ApplicationName=app,Pooling=false}.ConnectionString;
        await using var a=new NpgsqlConnection(target);await a.OpenAsync();
        await using var b=new NpgsqlConnection(target);await b.OpenAsync();
        await using var c=new NpgsqlConnection(target);await c.OpenAsync();
        var monitor=new ActivityWindow(connection,2){ShowInTaskbar=false};
        var application=ActivityDialogChecks.Find<TextBox>(monitor,"ApplicationFilter");application.Text=app;
        var grid=ActivityDialogChecks.Find<DataGrid>(monitor,"Sessions");var status=ActivityDialogChecks.Find<TextBox>(monitor,"Status");
        monitor.Show();
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(10);while(grid.Items.Count==0 && DateTime.UtcNow<deadline){await monitor.ReloadAsync();await Task.Delay(25);}
            check(grid.Items.Count==2 && status.Text.Contains("more sessions match") && status.Text.Contains("Narrow filters"),"Activity UI explicitly labels actual truncation");
            await ActivityDialogChecks.Capture(monitor,"m878-truncated-normal",false,check);await ActivityDialogChecks.Capture(monitor,"m878-truncated-small",true,check);
            await c.CloseAsync();await monitor.ReloadAsync();
            check(grid.Items.Count==2 && status.Text.Contains("complete filtered snapshot") && status.Text.Contains("Refreshed locally"),"Activity UI distinguishes exact limit and local refresh time");
            var pidColumn=grid.Columns.Single(x=>x.SortMemberPath=="Pid");
            grid.Items.SortDescriptions.Add(new SortDescription("Pid",ListSortDirection.Descending));pidColumn.SortDirection=ListSortDirection.Descending;
            grid.SelectedItem=grid.Items.OfType<ActivitySession>().First();var selected=(ActivitySession)grid.SelectedItem;
            await monitor.ReloadAsync();
            check(grid.Items.OfType<ActivitySession>().Select(x=>x.Pid).SequenceEqual(new[]{a.ProcessID,b.ProcessID}.OrderDescending()),"Refresh preserves descending visual PID order");
            check(pidColumn.SortDirection==ListSortDirection.Descending && grid.Items.SortDescriptions.Count==1,"Refresh preserves sorting descriptor and header indicator");
            check(grid.SelectedItem is ActivitySession retained && retained.Pid==selected.Pid && retained.BackendStart==selected.BackendStart,"Sorted refresh preserves selected backend identity");
            grid.Items.SortDescriptions.Clear();grid.Items.SortDescriptions.Add(new SortDescription("TransactionDuration",ListSortDirection.Descending));
            await monitor.ReloadAsync();check(grid.Items.SortDescriptions.Single().PropertyName=="TransactionDuration","Refresh preserves native duration sorting including null values");
            var auto=ActivityDialogChecks.Find<CheckBox>(monitor,"AutoRefreshCheck");auto.IsChecked=true;
            check(monitor.IsAutoRefreshRunning && monitor.AutoRefreshInterval==TimeSpan.FromSeconds(5),"Monitor timer starts at default interval");
            int beforeTimer=monitor.SnapshotVersion;deadline=DateTime.UtcNow.AddSeconds(10);
            while(beforeTimer==monitor.SnapshotVersion && DateTime.UtcNow<deadline)await Task.Delay(50);
            check(beforeTimer!=monitor.SnapshotVersion,"Monitor timer refreshes actual server snapshot");
            check(grid.Items.SortDescriptions.Single().PropertyName=="TransactionDuration","Timer preserves chosen duration sort");
            ActivityDialogChecks.Find<ComboBox>(monitor,"AutoRefreshIntervalSelector").SelectedIndex=1;
            check(monitor.AutoRefreshInterval==TimeSpan.FromSeconds(10),"Monitor interval change applies to timer");
            auto.IsChecked=false;check(!monitor.IsAutoRefreshRunning,"Monitor unchecked timer stops");
            int previous=monitor.SnapshotVersion;var pid=ActivityDialogChecks.Find<TextBox>(monitor,"PidFilter");pid.Text="invalid";await monitor.ReloadAsync();
            check(previous==monitor.SnapshotVersion && status.Text.Contains("Previous snapshot retained") && status.Text.Contains("proposed filters were not applied"),"Invalid filter labels retained snapshot without claiming new filters applied");
            pid.Clear();application.Text=app+"missing";await monitor.ReloadAsync();
            check(grid.Items.Count==0 && status.Text.Contains("complete filtered snapshot"),"Successful empty snapshot replaces stale rows");
        }
        finally{monitor.Close();}
    }
}
