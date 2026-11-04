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
        var monitor=new ActivityHost(connection,2){ShowInTaskbar=false};ActivityDialogChecks.Find<CheckBox>(monitor,"AutoRefreshCheck").IsChecked=false;
        var application=ActivityDialogChecks.Find<TextBox>(monitor,"ApplicationFilter");application.Text=app;
        var grid=ActivityDialogChecks.Find<DataGrid>(monitor,"Sessions");var status=ActivityDialogChecks.Find<TextBox>(monitor,"Status");
        monitor.Show();
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(10);while(grid.Items.Count==0 && DateTime.UtcNow<deadline){await monitor.ReloadAsync();await Task.Delay(25);}
            Button Pager(string name)=>ActivityDialogChecks.Find<Button>(monitor,name);var pageInfo=ActivityDialogChecks.Find<TextBlock>(monitor,"PageInfo");
            check(grid.Items.Count==2 && status.Text.Contains("more sessions match") && status.Text.Contains("Next Page") && pageInfo.Text=="Sessions 1–2 of 3","Activity UI explicitly labels actual truncation and page position");
            check(Pager("NextPageButton").IsEnabled && !Pager("PreviousPageButton").IsEnabled && !Pager("FirstPageButton").IsEnabled,"First page enables only Next Page");
            await ActivityDialogChecks.Capture(monitor,"m878-truncated-normal",false,check);await ActivityDialogChecks.Capture(monitor,"m878-truncated-small",true,check);
            var connections=new[]{a,b,c};var highest=connections.MaxBy(x=>x.ProcessID)!;var remaining=connections.Where(x=>x!=highest).Select(x=>x.ProcessID).ToArray();
            await monitor.NextPageAsync();
            check(grid.Items.OfType<ActivitySession>().Single().Pid==highest.ProcessID && pageInfo.Text=="Sessions 3–3 of 3" && !Pager("NextPageButton").IsEnabled && Pager("PreviousPageButton").IsEnabled,"Next Page loads the following sessions by PID");
            await ActivityDialogChecks.Capture(monitor,"m1025-last-page-small",true,check);
            await monitor.ReloadAsync();check(grid.Items.OfType<ActivitySession>().Single().Pid==highest.ProcessID,"Refresh keeps the current page");
            await monitor.PreviousPageAsync();check(grid.Items.Count==2 && pageInfo.Text=="Sessions 1–2 of 3","Previous Page returns to the earlier sessions");
            await monitor.NextPageAsync();
            await highest.CloseAsync();await monitor.ReloadAsync();
            check(grid.Items.Count==2 && status.Text.Contains("No sessions remain on that page") && status.Text.Contains("complete filtered snapshot") && status.Text.Contains("Refreshed locally") && pageInfo.Text=="","Emptied page falls back to the complete first page with local refresh time");
            var pidColumn=grid.Columns.Single(x=>x.SortMemberPath=="Pid");
            check(grid.Items.SortDescriptions.Single().PropertyName=="Pid" && pidColumn.SortDirection==ListSortDirection.Ascending,"Processes starts sorted by PID ascending (M1030)");
            grid.Items.SortDescriptions.Clear();grid.Items.SortDescriptions.Add(new SortDescription("Pid",ListSortDirection.Descending));pidColumn.SortDirection=ListSortDirection.Descending;
            grid.SelectedItem=grid.Items.OfType<ActivitySession>().First();var selected=(ActivitySession)grid.SelectedItem;
            await monitor.ReloadAsync();
            check(grid.Items.OfType<ActivitySession>().Select(x=>x.Pid).SequenceEqual(remaining.OrderDescending()),"Refresh preserves descending visual PID order");
            check(pidColumn.SortDirection==ListSortDirection.Descending && grid.Items.SortDescriptions.Count==1,"Refresh preserves sorting descriptor and header indicator");
            check(grid.SelectedItem is ActivitySession retained && retained.Pid==selected.Pid && retained.BackendStart==selected.BackendStart,"Sorted refresh preserves selected backend identity");
            grid.Items.SortDescriptions.Clear();grid.Items.SortDescriptions.Add(new SortDescription("TransactionDuration",ListSortDirection.Descending));
            await monitor.ReloadAsync();check(grid.Items.SortDescriptions.Single().PropertyName=="TransactionDuration","Refresh preserves native duration sorting including null values");
            var auto=ActivityDialogChecks.Find<CheckBox>(monitor,"AutoRefreshCheck");auto.IsChecked=true;
            check(monitor.IsAutoRefreshRunning && monitor.AutoRefreshInterval==TimeSpan.FromSeconds(10),"Monitor timer starts at default interval");
            ActivityDialogChecks.Find<ComboBox>(monitor,"AutoRefreshIntervalSelector").SelectedIndex=0;
            int beforeTimer=monitor.SnapshotVersion;deadline=DateTime.UtcNow.AddSeconds(10);
            while(beforeTimer==monitor.SnapshotVersion && DateTime.UtcNow<deadline)await Task.Delay(50);
            check(beforeTimer!=monitor.SnapshotVersion,"Monitor timer refreshes actual server snapshot");
            check(grid.Items.SortDescriptions.Single().PropertyName=="TransactionDuration","Timer preserves chosen duration sort");
            ActivityDialogChecks.Find<ComboBox>(monitor,"AutoRefreshIntervalSelector").SelectedIndex=5;
            check(monitor.AutoRefreshInterval==TimeSpan.FromSeconds(30),"Monitor interval change applies to timer");
            auto.IsChecked=false;check(!monitor.IsAutoRefreshRunning,"Monitor unchecked timer stops");
            // A tick may still be loading; let it finish so the invalid filter below is judged on its own.
            await monitor.ReloadAsync();
            int previous=monitor.SnapshotVersion;var pid=ActivityDialogChecks.Find<TextBox>(monitor,"PidFilter");pid.Text="invalid";await monitor.ReloadAsync();
            check(previous==monitor.SnapshotVersion && status.Text.Contains("Previous snapshot retained") && status.Text.Contains("proposed filters were not applied"),"Invalid filter labels retained snapshot without claiming new filters applied");
            pid.Clear();application.Text=app+"missing";await monitor.ReloadAsync();
            check(grid.Items.Count==0 && status.Text.Contains("complete filtered snapshot"),"Successful empty snapshot replaces stale rows");
        }
        finally{monitor.Close();}
    }
}
