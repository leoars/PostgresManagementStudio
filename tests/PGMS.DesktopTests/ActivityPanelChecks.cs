using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ActivityPanelChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        check(ActivityChart.NiceCeiling(0)==1 && ActivityChart.NiceCeiling(0.5)==1 && ActivityChart.NiceCeiling(3)==5 && ActivityChart.NiceCeiling(12)==20 && ActivityChart.NiceCeiling(101)==200 && ActivityChart.NiceCeiling(double.NaN)==1,"Chart scale rounds up to 1, 2 or 5 × 10ⁿ");
        check(ActivityChart.NiceCeiling(1)==2 && ActivityChart.NiceCeiling(5)==5*2 && ActivityChart.NiceCeiling(50)==100 && ActivityChart.NiceCeiling(10)==20,"A value on a scale step moves the scale up, so the line never lies on the top border (M1030)");
        await PanelsAsync(connection,check);
        await OverviewMenuAsync(connection,check);
        await DocumentAsync(connection,check);
    }
    private static async Task OverviewMenuAsync(string connection,Action<bool,string> check)
    {
        var monitor=new ActivityHost(connection){ShowInTaskbar=false};var view=monitor.View;
        T Find<T>(string name)where T:FrameworkElement=>(T)view.FindName(name);
        monitor.Show();
        try
        {
            await Until(()=>monitor.SnapshotVersion>0,()=>Task.CompletedTask,"the first refresh");
            await Until(()=>view.RequestClose(),()=>Task.CompletedTask,"an idle monitor");
            var area=Find<StackPanel>("OverviewArea");var menu=Find<ContextMenu>("OverviewMenu");
            var intervals=Find<MenuItem>("RefreshIntervalMenu").Items.OfType<MenuItem>().ToArray();
            var pause=Find<MenuItem>("PauseMenu");var resume=Find<MenuItem>("ResumeMenu");var refresh=Find<MenuItem>("RefreshMenu");
            MenuItem Interval(string label)=>intervals.Single(i=>(string)i.Header==label);
            check(area.ContextMenu==menu && area.Background is not null && menu.Items.OfType<MenuItem>().Select(i=>(string)i.Header).SequenceEqual(["Refresh _interval","_Pause","Res_ume","_Refresh"]),"Overview area carries the SSMS menu: Refresh interval, Pause, Resume, Refresh");
            check(menu.Items.Cast<object>().Select(i=>i is Separator).SequenceEqual([false,true,false,false,true,false]),"Separators group the menu like SSMS: interval | Pause, Resume | Refresh (M1030)");
            check(intervals.Select(i=>(string)i.Header).SequenceEqual(["1 second","3 seconds","5 seconds","10 seconds","15 seconds","30 seconds","1 minute","5 minutes","30 minutes","1 hour"]),"Refresh Interval lists the ten SSMS intervals");
            view.UpdateOverviewMenu();
            check(intervals.Where(i=>i.IsChecked).Select(i=>(string)i.Header).SequenceEqual(["10 seconds"]) && pause.IsEnabled && !resume.IsEnabled && refresh.IsEnabled,"Menu opens with 10 seconds checked, Pause enabled and Resume disabled while refreshing");
            pause.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));view.UpdateOverviewMenu();
            check(!monitor.IsAutoRefreshRunning && Find<CheckBox>("AutoRefreshCheck").IsChecked==false && !pause.IsEnabled && resume.IsEnabled,"Pause stops auto-refresh, clears the toolbar box and enables Resume");
            check((string)Find<Expander>("OverviewPanel").Header=="Overview (paused)","A paused monitor titles its panel Overview (paused), like SSMS");
            Interval("1 minute").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));view.UpdateOverviewMenu();
            check(monitor.AutoRefreshInterval==TimeSpan.FromMinutes(1) && !monitor.IsAutoRefreshRunning && (string)((ComboBoxItem)Find<ComboBox>("AutoRefreshIntervalSelector").SelectedItem).Tag=="60" && intervals.Where(i=>i.IsChecked).Select(i=>(string)i.Header).SequenceEqual(["1 minute"]),"Choosing an interval while paused changes the toolbar interval and keeps the monitor paused");
            resume.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));view.UpdateOverviewMenu();
            check(monitor.IsAutoRefreshRunning && monitor.AutoRefreshInterval==TimeSpan.FromMinutes(1) && pause.IsEnabled && !resume.IsEnabled && (string)Find<Expander>("OverviewPanel").Header=="Overview","Resume restarts auto-refresh at the chosen interval and drops (paused) from the title");
            Find<ComboBox>("AutoRefreshIntervalSelector").SelectedIndex=0;view.UpdateOverviewMenu();
            check(intervals.Where(i=>i.IsChecked).Select(i=>(string)i.Header).SequenceEqual(["1 second"]),"The menu marks an interval chosen in the toolbar");
            Find<CheckBox>("AutoRefreshCheck").IsChecked=false;
            int version=monitor.SnapshotVersion,readings=monitor.MetricsVersion;
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));view.UpdateOverviewMenu();
            check(!refresh.IsEnabled,"Refresh is disabled while a refresh runs");
            await Until(()=>view.RequestClose(),()=>Task.CompletedTask,"the menu refresh to finish");
            check(monitor.SnapshotVersion>version && monitor.MetricsVersion>readings,"Refresh from the Overview menu reloads sessions and server metrics");
        }
        finally{await Until(()=>view.RequestClose(),()=>Task.CompletedTask,"an idle monitor");monitor.Close();}
    }
    private static async Task Until(Func<bool> done,Func<Task> step,string what)
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(DateTime.UtcNow<deadline){await step();if(done())return;await Task.Delay(40);}
        throw new Exception("Activity panels did not reach: "+what);
    }
    private static async Task PanelsAsync(string connection,Action<bool,string> check)
    {
        (string Connection,string Sql,bool Plan)? opened=null;
        var monitor=new ActivityHost(connection,openQueryText:(c,sql)=>{opened=(c,sql,false);return Task.CompletedTask;},openQueryPlan:(c,sql)=>{opened=(c,sql,true);return Task.CompletedTask;}){ShowInTaskbar=false};var view=monitor.View;
        T Find<T>(string name)where T:FrameworkElement=>(T)view.FindName(name);
        Find<CheckBox>("AutoRefreshCheck").IsChecked=false;monitor.Show();
        long key=Random.Shared.NextInt64(1,long.MaxValue);string marker="pgms_panel_"+Guid.NewGuid().ToString("N")[..10];bool created=false;
        string app="PGMS.ActivityPanels."+Guid.NewGuid().ToString("N")[..8];string target=new NpgsqlConnectionStringBuilder(connection){ApplicationName=app,Pooling=false}.ConnectionString;
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        await using var holder=new NpgsqlConnection(target);await holder.OpenAsync();
        await using var waiter=new NpgsqlConnection(target);await waiter.OpenAsync();
        Task? blocked=null;NpgsqlCommand? wait=null;
        try
        {
            var expanders=((StackPanel)Find<ScrollViewer>("Panels").Content).Children.OfType<Expander>().ToArray();
            check(expanders.Select(e=>(string)e.Header).SequenceEqual(["Overview (paused)","Processes","Resource Waits","Data File I/O","Recent Expensive Queries","Active Expensive Queries"]),"Activity Monitor has the six SSMS panels in SSMS order, Overview marked paused while auto-refresh is off");
            var dataPanel=Find<Expander>("DataFileIoPanel");dataPanel.ApplyTemplate();var toggle=(ToggleButton)dataPanel.Template.FindName("HeaderSite",dataPanel);
            var expand=UIElementAutomationPeer.CreatePeerForElement(dataPanel).GetPattern(PatternInterface.ExpandCollapse) as System.Windows.Automation.Provider.IExpandCollapseProvider;
            toggle.IsChecked=true;bool opens=dataPanel.IsExpanded;expand?.Collapse();
            check(opens && !dataPanel.IsExpanded && toggle.IsChecked==false && toggle.Focusable && System.Windows.Automation.AutomationProperties.GetName(toggle)=="Data File I/O","SSMS-style panel header is one focusable toggle named after the panel and keeps the expand/collapse pattern (M1030)");
            var inactive=view.TryFindResource(SystemColors.InactiveSelectionHighlightBrushKey) as System.Windows.Media.SolidColorBrush;
            check(inactive?.Color==SystemColors.HighlightColor,"Selected rows keep the selection colour when the grid loses focus, like SSMS (M1030)");
            var sessions=Find<DataGrid>("Sessions");
            check(sessions.Columns[0].SortDirection==System.ComponentModel.ListSortDirection.Ascending && sessions.Items.SortDescriptions.Single().PropertyName=="Pid","Processes opens marked as sorted by PID ascending, so the first header click reverses it (M1030)");
            check(expanders.Select(e=>e.IsExpanded).SequenceEqual([true,true,false,false,false,false]),"Overview and Processes open expanded, the other panels collapsed (user decision M1027)");
            await Until(()=>monitor.MetricsVersion>=2,monitor.ReloadAsync,"two metrics readings");
            check(!Find<TextBlock>("TransactionsTitle").Text.Contains('—') && Find<TextBlock>("ActiveSessionsTitle").Text.StartsWith("Active Sessions (",StringComparison.Ordinal) && Find<TextBlock>("OverviewNote").Text.Contains("pg_stat_io",StringComparison.Ordinal),"Overview titles show current values once two readings exist");
            var chart=Find<ActivityChart>("ActiveSessionsChart");var peer=UIElementAutomationPeer.CreatePeerForElement(chart);
            check(chart.Values.Count==Math.Min(monitor.MetricsVersion,ActivityMetricsHistory.Capacity) && peer.GetAutomationControlType()==AutomationControlType.Image && peer.GetName().StartsWith(Find<TextBlock>("ActiveSessionsTitle").Text,StringComparison.Ordinal),"Overview chart holds one point per reading and announces its title and value");
            check(ActivityChart.Divisions==5 && chart.AxisLabel(0)==chart.Scale.ToString("G4",System.Globalization.CultureInfo.InvariantCulture) && chart.AxisLabel(ActivityChart.Divisions)=="0" && Enumerable.Range(0,6).Select(chart.AxisLabel).Distinct().Count()==6,"Chart axis labels run from the scale down to 0 in five steps, like the SSMS axis (M1030)");
            var waits=Find<DataGrid>("ResourceWaits");var files=Find<DataGrid>("DataFileIo");
            check(waits.Items.OfType<ResourceWaitRow>().Select(r=>r.Category).Take(ActivityMetricsHistory.Categories.Count).SequenceEqual(ActivityMetricsHistory.Categories) && files.Items.OfType<DataFileIoRow>().Any(r=>r.Database==admin.Database),"Resource Waits and Data File I/O fill even while collapsed, so their history has no gaps");
            foreach(var grid in new[]{waits,files,Find<DataGrid>("RecentQueries"),Find<DataGrid>("ActiveQueries")})
                check(grid.Columns.All(c=>c.HeaderStyle?.Setters.OfType<Setter>().Any(s=>s.Property==FrameworkElement.ToolTipProperty && s.Value is string { Length: >0 })==true),"Every column header of "+System.Windows.Automation.AutomationProperties.GetName(grid)+" describes its source");
            await using(var hold=new NpgsqlCommand("SELECT pg_advisory_lock(@key)",holder)){hold.Parameters.AddWithValue("key",key);await hold.ExecuteNonQueryAsync();}
            wait=new NpgsqlCommand("SELECT pg_advisory_lock(@key) /* activity panel waiter */",waiter);wait.Parameters.AddWithValue("key",key);blocked=wait.ExecuteNonQueryAsync();
            Find<Expander>("ResourceWaitsPanel").IsExpanded=true;Find<Expander>("DataFileIoPanel").IsExpanded=true;
            await Until(()=>waits.Items.OfType<ResourceWaitRow>().Single(r=>r.Category=="Lock").WaitingNow>=1,monitor.ReloadAsync,"a Lock waiter in Resource Waits");
            check(waits.Items.OfType<ResourceWaitRow>().Single(r=>r.Category=="Lock").WaitMsPerSecond>=1000 && !Find<TextBlock>("WaitingSessionsTitle").Text.Contains("(0)"),"A real lock wait shows in Resource Waits (ms/sec) and in Waiting Sessions");
            Find<Expander>("ActiveQueriesPanel").IsExpanded=true;var active=Find<DataGrid>("ActiveQueries");
            await Until(()=>active.Items.OfType<ActiveExpensiveQuery>().Any(q=>q.Pid==waiter.ProcessID),monitor.ReloadAsync,"the waiter in Active Expensive Queries");
            var running=active.Items.OfType<ActiveExpensiveQuery>().Single(q=>q.Pid==waiter.ProcessID);
            check(running.Query.Contains("activity panel waiter",StringComparison.Ordinal) && running.Wait.StartsWith("Lock",StringComparison.Ordinal) && running.ElapsedLabel!="—" && Find<TextBlock>("ActiveQueriesNote").Text.Contains("oldest first",StringComparison.Ordinal),"Expanding Active Expensive Queries lists the running waiter with its wait and elapsed time");
            foreach(var grid in new[]{active,Find<DataGrid>("RecentQueries")})
                check((string)grid.Columns[0].Header=="Query" && ((System.Windows.Data.Binding)((DataGridTextColumn)grid.Columns[0]).Binding).Path.Path=="QueryLine" && grid.Columns[0].SortMemberPath=="Query","Query is the first column of "+System.Windows.Automation.AutomationProperties.GetName(grid)+" and shows one line (M1030)");
            check(active.ContextMenu.Items.OfType<MenuItem>().Select(i=>(string)i.Header).SequenceEqual(["_Edit Query Text","Show Execution _Plan"]),"Active Expensive Queries has the SSMS row menu: Edit Query Text, Show Execution Plan");
            active.SelectedItem=null;check(!await view.OpenSelectedQueryAsync(false) && opened is null,"Without a selected query the row menu opens nothing");
            active.SelectedItem=running;
            check(await view.OpenSelectedQueryAsync(false) && opened is { Plan: false } text && text.Sql==running.Query && new NpgsqlConnectionStringBuilder(text.Connection).Database==running.Database,"Edit Query Text hands the running statement and its database to a new query tab");
            check(await view.OpenSelectedQueryAsync(true) && opened is { Plan: true } plan && plan.Sql==running.Query && new NpgsqlConnectionStringBuilder(plan.Connection).Database==running.Database,"Show Execution Plan hands the same statement and database to the estimated plan");
            await using(var exists=new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_extension WHERE extname='pg_stat_statements'",admin))
                if((long)(await exists.ExecuteScalarAsync())! == 0){await using var create=new NpgsqlCommand("CREATE EXTENSION pg_stat_statements",admin);await create.ExecuteNonQueryAsync();created=true;}
            await using(var table=new NpgsqlCommand($"CREATE TABLE {SqlIdentifier.Quote(marker)}(id int)",admin))await table.ExecuteNonQueryAsync();
            var recentNote=Find<TextBlock>("RecentQueriesNote");var recent=Find<DataGrid>("RecentQueries");
            Find<Expander>("RecentQueriesPanel").IsExpanded=true;
            await Until(()=>recentNote.Text.StartsWith("Measuring from",StringComparison.Ordinal),monitor.ReloadAsync,"a Recent Expensive Queries baseline");
            for(int i=0;i<3;i++)await using(var count=new NpgsqlCommand($"SELECT count(*), pg_sleep(0.02) FROM {SqlIdentifier.Quote(marker)}",admin))await count.ExecuteScalarAsync();
            await Until(()=>recent.Items.OfType<RecentExpensiveQuery>().Any(q=>q.Query.Contains(marker,StringComparison.Ordinal)),monitor.ReloadAsync,"the marker statement in Recent Expensive Queries");
            check(recentNote.Text.Contains($"database \"{admin.Database}\"",StringComparison.Ordinal) && recent.Items.OfType<RecentExpensiveQuery>().First(q=>q.Query.Contains(marker,StringComparison.Ordinal)).ExecutionsPerMinute>0,"Recent Expensive Queries shows a real statement measured between refreshes and names the database");
            foreach(var e in expanders)e.IsExpanded=true;
            await ActivityDialogChecks.Capture(monitor,"m1027-activity-panels-normal",false,check);await ActivityDialogChecks.Capture(monitor,"m1027-activity-panels-small",true,check);
            Find<Expander>("RecentQueriesPanel").IsExpanded=false;
            check(recent.Items.Count==0 && recentNote.Text.StartsWith("Expand",StringComparison.Ordinal),"Collapsing Recent Expensive Queries drops its interval so the next one starts fresh");
            Find<CheckBox>("AutoRefreshCheck").IsChecked=true;check(monitor.IsAutoRefreshRunning && (string)expanders[0].Header=="Overview","Auto-refresh runs before the monitor closes and Overview is no longer marked paused");
        }
        finally
        {
            wait?.Cancel();if(blocked is not null){try{await blocked;}catch(OperationCanceledException){}catch(PostgresException ex) when(ex.SqlState=="57014"){}}
            wait?.Dispose();
            await using(var release=new NpgsqlCommand("SELECT pg_advisory_unlock_all()",holder))await release.ExecuteNonQueryAsync();
            await using(var drop=new NpgsqlCommand($"DROP TABLE IF EXISTS {SqlIdentifier.Quote(marker)}",admin))await drop.ExecuteNonQueryAsync();
            await Until(()=>monitor.View.RequestClose(),()=>Task.CompletedTask,"an idle monitor");monitor.Close();
            if(created){await using var remove=new NpgsqlCommand("DROP EXTENSION pg_stat_statements",admin);await remove.ExecuteNonQueryAsync();}
        }
        check(!monitor.IsAutoRefreshRunning,"Closing the monitor stops auto-refresh");
    }
    private static async Task DocumentAsync(string connection,Action<bool,string> check)
    {
        var window=new MainWindow{ShowInTaskbar=false,Opacity=0};window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var menu=(MenuItem)window.FindName("ActivityMonitorMenu");var dock=(DockingManager)window.FindName("DockManager");
            int windows=Application.Current.Windows.Count;
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view=window.ActivityMonitors.SingleOrDefault();
            var document=dock.Layout.Descendents().OfType<LayoutDocument>().SingleOrDefault(d=>ReferenceEquals(d.Content,view));
            check(view is not null && document is not null && document.Title==view.Title && document.Title.EndsWith(" - Activity Monitor",StringComparison.Ordinal) && document.Title.StartsWith(new NpgsqlConnectionStringBuilder(connection).Host+":",StringComparison.Ordinal) && dock.ActiveContent==view && Application.Current.Windows.Count==windows,"View → Activity Monitor opens an active document tab, not a separate window");
            check(menu.InputGestureText=="Ctrl+Alt+A" && (string)menu.Header=="Activity _Monitor","The menu item keeps the SSMS shortcut and no longer promises a dialog");
            check(new[]{AvalonDock.Themes.VS.Themes.ResourceKeys.DocumentWellTabUnselectedText,AvalonDock.Themes.VS.Themes.ResourceKeys.DocumentWellTabUnselectedHoveredText,AvalonDock.Themes.VS.Themes.ResourceKeys.DocumentWellTabSelectedInactiveText}
                .All(k=>dock.TryFindResource(k) is System.Windows.Media.SolidColorBrush { Color: { R: <0x40, G: <0x40, B: <0x40 } }),"Inactive document tab titles are dark, like SSMS, not pale on the blue tab well (M1030)");
            await Until(()=>view!.SnapshotVersion>0,()=>Task.CompletedTask,"the first refresh of the tab");
            ApplicationCommands.New.Execute(null,window);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(dock.ActiveContent is QueryView,"A new query tab takes focus beside the monitor");
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(window.ActivityMonitors.Count==1 && dock.ActiveContent==view,"Opening the monitor again for the same server and user activates the existing tab");
            await Until(()=>view!.RequestClose(),()=>Task.CompletedTask,"an idle monitor tab");
            check(ApplicationCommands.Close.CanExecute(null,window),"Ctrl+F4 can close the active monitor tab");
            ApplicationCommands.Close.Execute(null,window);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(window.ActivityMonitors.Count==0 && !view!.IsAutoRefreshRunning && !dock.Layout.Descendents().OfType<LayoutDocument>().Any(d=>d.Content is ActivityMonitorView),"Closing the tab removes it and stops its refresh timer");
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var reopened=window.ActivityMonitors.Single();check(!ReferenceEquals(reopened,view) && reopened.IsAutoRefreshRunning,"A closed monitor reopens as a new tab with auto-refresh on");
            // The real actions behind Active Expensive Queries: a statement that would sleep an hour proves the plan does not execute it.
            const string sleeper="SELECT pg_sleep(3600)";var started=System.Diagnostics.Stopwatch.StartNew();
            var planned=await window.OpenEstimatedPlanAsync(connection,sleeper);
            check(planned.IsConnected && planned.Editor.Text==sleeper && ((ExecutionPlanView)planned.FindName("PlanDiagram")).Statements.Count==1 && ((TextBlock)planned.FindName("PlanStatus")).Text.Contains("Estimated plan") && started.Elapsed<TimeSpan.FromSeconds(30) && dock.ActiveContent==planned,"Show Execution Plan opens a connected tab with the estimated plan without running the statement");
            var edited=await window.OpenQueryTextAsync(connection,"SELECT 42");
            check(edited.IsConnected && edited.Editor.Text=="SELECT 42" && !edited.IsDirty && ((ExecutionPlanView)edited.FindName("PlanDiagram")).Statements.Count==0 && dock.ActiveContent==edited,"Edit Query Text opens a connected tab with the statement and does not run it");
            window.Close();await window.CloseCompletion.WaitAsync(TimeSpan.FromSeconds(30));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(!reopened.IsAutoRefreshRunning,"Closing the main window shuts down an open monitor tab");
        }
        finally{if(window.IsLoaded)window.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    }
}
