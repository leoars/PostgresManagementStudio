using Npgsql;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ColumnPrivilegeDialogChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="ui_col_"+suffix,role="ui_col_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        const string column="value'\"é";string qc=SqlIdentifier.Quote(column);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        var dialog=new PrivilegeDialog(connection){ShowInTaskbar=false};
        T Find<T>(string name)where T:FrameworkElement=>(T)dialog.FindName(name);
        void Target(PrivilegeDialog w)
        {
            ((ComboBox)w.FindName("ObjectType")).SelectedItem=PrivilegeTarget.Table;
            ((TextBox)w.FindName("SchemaName")).Text=schema;((TextBox)w.FindName("ObjectName")).Text="target";
            ((TextBox)w.FindName("RoleName")).Text=role;((CheckBox)w.FindName("ColumnScope")).IsChecked=true;
            ((TextBox)w.FindName("ColumnName")).Text=column;((ComboBox)w.FindName("Privilege")).SelectedItem="SELECT";
        }
        try
        {
            await Sql($"CREATE SCHEMA {qs};CREATE ROLE {qr} LOGIN;GRANT USAGE ON SCHEMA {qs} TO {qr};CREATE TABLE {table}({qc} integer,secret text);INSERT INTO {table} VALUES(7,'private');");
            dialog.Show();Target(dialog);
            check(Find<ComboBox>("Privilege").Items.Cast<string>().SequenceEqual(PrivilegeRequest.AllowedForColumn),"WPF column scope offers exactly four valid privileges");
            Find<TextBox>("ColumnName").Clear();
            check(!Find<Button>("ApplyButton").IsEnabled && !Find<Button>("ScriptButton").IsEnabled && Find<Button>("InspectButton").IsEnabled,"Missing column blocks mutation but not table inspection");
            await dialog.InspectAsync();var snapshot=dialog.AccessSnapshot;
            check(snapshot is not null && snapshot.Privileges.Any(p=>p.Column==column) && snapshot.Privileges.Any(p=>p.Column=="secret"),"Column mode inspection retains full table and sibling context");
            Find<TextBox>("ColumnName").Text=column;
            check(ReferenceEquals(snapshot,dialog.AccessSnapshot),"Changing proposed column does not erase full-table inspection context");
            foreach(string privilege in PrivilegeRequest.AllowedForColumn)
            {
                Find<ComboBox>("Privilege").SelectedItem=privilege;
                check(Find<TextBox>("Preview").Text==$"GRANT {privilege} ({qc}) ON TABLE {table} TO {qr};","WPF exact single-column preview: "+privilege);
            }
            Find<CheckBox>("ColumnScope").IsChecked=false;Find<ComboBox>("Privilege").SelectedItem="DELETE";Find<CheckBox>("ColumnScope").IsChecked=true;
            check((string)Find<ComboBox>("Privilege").SelectedItem=="SELECT" && !Find<TextBox>("Preview").Text.Contains("DELETE"),"Enabling column scope replaces incompatible whole-table privilege");
            Find<CheckBox>("ColumnScope").IsChecked=false;
            check(Find<TextBox>("ColumnName").Text==column && Find<TextBox>("Preview").Text==$"GRANT SELECT ON TABLE {table} TO {qr};","Whole-table mode preserves column proposal without emitting it");
            Find<CheckBox>("ColumnScope").IsChecked=true;Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Sequence;
            check(Find<StackPanel>("TableColumnScope").Visibility==Visibility.Collapsed && !Find<TextBox>("Preview").Text.Contains(qc),"Sequence ignores retained column proposal and hides column controls");
            Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
            check(Find<CheckBox>("ColumnScope").IsChecked==true && Find<TextBox>("ColumnName").Text==column && Find<TextBox>("Preview").Text.Contains(qc),"Returning to Table restores explicit column proposal");
            Find<CheckBox>("GrantOption").IsChecked=true;Find<CheckBox>("PublicRecipient").IsChecked=true;
            check(!Find<CheckBox>("GrantOption").IsEnabled && Find<CheckBox>("GrantOption").IsChecked==false && Find<TextBox>("Preview").Text.EndsWith("TO PUBLIC;"),"Column PUBLIC disables incompatible grant option");
            Find<CheckBox>("PublicRecipient").IsChecked=false;
            var scripted=new PrivilegeDialog(connection){ShowInTaskbar=false};
            scripted.Loaded+=(_,_)=>{Target(scripted);((Button)scripted.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
            scripted.ShowDialog();
            check(scripted.ScriptSql==$"GRANT SELECT ({qc}) ON TABLE {table} TO {qr};","Column Script returns reviewable SQL to editor");
            check(!(await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"target",schema)).AclEntries.Any(e=>e.Grantee==role),"Column Script and preview create no server grant");
            await dialog.InspectAsync();
            var limitedInspect=new EffectivePrivilegeService(new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString);
            foreach(string privilege in PrivilegeRequest.AllowedForColumn)
            {
                Find<ComboBox>("Privilege").SelectedItem=privilege;await dialog.ApplyAsync();
                var access=await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema);
                check(access.Privileges.Single(p=>p.Column==column && p.Privilege==privilege).Allowed && !access.Privileges.Single(p=>p.Scope=="Table" && p.Privilege==privilege).Allowed,"Column WPF Apply grants real isolated authority: "+privilege);
            }
            check(dialog.AccessSnapshotIsStale && Find<TextBox>("ColumnName").Text==column,"Column Apply marks previous observation stale and preserves proposal");
            var all=await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema);
            check(all.Privileges.Where(p=>p.Column=="secret").All(p=>!p.Allowed),"Column WPF grants never authorize sibling column");
            await dialog.InspectAsync();
            check(!dialog.AccessSnapshotIsStale && dialog.AccessSnapshot!.AclEntries.Count(e=>e.Column==column && e.Grantee==role)==4,"Column reinspection loads four stored attacl entries and clears stale");
            Find<ComboBox>("Privilege").SelectedItem="SELECT";Find<CheckBox>("GrantOption").IsChecked=true;await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").GrantOption,"Column WPF applies grant option to selected column");
            Find<CheckBox>("Revoke").IsChecked=true;await dialog.ApplyAsync();
            var baseOnly=(await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT");
            check(baseOnly.Allowed && !baseOnly.GrantOption,"Column WPF option-only revoke preserves base privilege");
            Find<CheckBox>("PublicRecipient").IsChecked=true;Find<CheckBox>("Revoke").IsChecked=false;await dialog.ApplyAsync();await dialog.InspectAsync();
            check(dialog.AccessSnapshot!.AclEntries.Any(e=>e.Column==column && e.GranteeOid==0 && e.Privilege=="SELECT"),"Column WPF PUBLIC creates stored column entry with OID0");
            Find<CheckBox>("Revoke").IsChecked=true;await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").Allowed,"Column WPF PUBLIC revoke preserves direct column grant");
            Find<CheckBox>("PublicRecipient").IsChecked=false;await dialog.ApplyAsync();
            check(!(await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").Allowed,"Column WPF normal revoke removes direct SELECT");
            Find<CheckBox>("Revoke").IsChecked=false;await dialog.InspectAsync();var beforeCancel=dialog.AccessSnapshot;
            Find<TextBox>("ColumnName").Focus();
            await using(var locker=new NpgsqlConnection(connection))
            {
                await locker.OpenAsync();await using var tx=await locker.BeginTransactionAsync();
                await using(var c=new NpgsqlCommand("SELECT attnum FROM pg_catalog.pg_attribute WHERE attrelid=@table::regclass AND attname=@column FOR UPDATE",locker,tx))
                {c.Parameters.AddWithValue("table",table);c.Parameters.AddWithValue("column",column);await c.ExecuteScalarAsync();}
                var pending=dialog.ApplyAsync();
                try
                {
                    bool waiting=false;var until=DateTime.UtcNow.AddSeconds(8);
                    while(!pending.IsCompleted && DateTime.UtcNow<until)
                    {
                        await using var c=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE position(@schema in query)>0 AND wait_event_type='Lock' AND @pid=ANY(pg_catalog.pg_blocking_pids(pid)))",admin);
                        c.Parameters.AddWithValue("schema",schema);c.Parameters.AddWithValue("pid",locker.ProcessID);
                        if((bool)(await c.ExecuteScalarAsync())!){waiting=true;break;}await Task.Delay(40);
                    }
                    check(waiting,"Column WPF Apply reaches exact attribute row lock");
                    check(!Find<TextBox>("ColumnName").IsEnabled && Find<Button>("CancelInspectionButton").IsEnabled,"Pending column Apply protects proposal and exposes cancel");
                    await dialog.ApplyAsync();
                    Find<Button>("CancelInspectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await pending.WaitAsync(TimeSpan.FromSeconds(8));
                    check(!dialog.IsBusy && Find<TextBlock>("Feedback").Text.Contains("Result not confirmed"),"Column WPF cancel restores retry with honest result message");
                    check(ReferenceEquals(beforeCancel,dialog.AccessSnapshot) && dialog.AccessSnapshotIsStale,"Column WPF cancel retains prior observation marked stale");
                    check(Find<TextBox>("ColumnName").Text==column && Find<TextBox>("ColumnName").IsKeyboardFocused,"Column cancellation preserves exact proposal and restores focus");
                }
                finally {dialog.CancelCurrentOperation();await tx.RollbackAsync();await pending.WaitAsync(TimeSpan.FromSeconds(8));}
            }
            check(!(await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").Allowed,"Canceled column WPF grant left server ACL unchanged");
            await dialog.InspectAsync();await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Table,"target",schema)).Privileges.Single(p=>p.Column==column && p.Privilege=="SELECT").Allowed,"Column Apply retries successfully after cancellation and inspection");
            await dialog.InspectAsync();var beforeFailure=dialog.AccessSnapshot;
            Find<TextBox>("ColumnName").Text="missing";await dialog.ApplyAsync();
            check(Find<TextBlock>("Feedback").Text.Contains("42703") && Find<TextBox>("ColumnName").Text=="missing","Missing column WPF Apply preserves diagnosis and input for correction");
            check(ReferenceEquals(beforeFailure,dialog.AccessSnapshot) && dialog.AccessSnapshotIsStale && !dialog.IsBusy,"Failed column Apply preserves full-table snapshot as stale and frees controls");
            Find<TextBox>("ColumnName").Text=column;await dialog.ApplyAsync();await dialog.InspectAsync();
            check(!dialog.AccessSnapshotIsStale && Find<TextBlock>("Feedback").Text.Contains("snapshot loaded"),"Corrected column proposal recovers with fresh ACL inspection");
            Find<TabControl>("PermissionTabs").SelectedIndex=0;Find<ScrollViewer>("ContentScroller").ScrollToTop();
            await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);dialog.Activate();
            check(UIElementAutomationPeer.CreatePeerForElement(Find<CheckBox>("ColumnScope"))?.GetName()=="Limit permission to one column" && UIElementAutomationPeer.CreatePeerForElement(Find<TextBox>("ColumnName"))?.GetName()=="Permission column name","Column controls expose stable automation names");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"m",false);
            check(Find<TextBox>("ColumnName").IsKeyboardFocused,"Column name mnemonic focuses editable exact name");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"c",false);
            check(Find<CheckBox>("ColumnScope").IsChecked==false && !Find<TextBox>("Preview").Text.Contains(qc),"Column scope mnemonic returns to whole-table SQL");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"c",false);
            check(Find<CheckBox>("ColumnScope").IsChecked==true && Find<TextBox>("Preview").Text.Contains(qc),"Column scope mnemonic restores column-specific SQL");
            check(!Find<Button>("ApplyButton").IsDefault,"Column Apply is never an implicit Enter action");
            foreach(bool small in new[]{false,true})
            {
                dialog.Width=small?dialog.MinWidth:760;dialog.Height=small?dialog.MinHeight:760;
                Find<TabControl>("PermissionTabs").SelectedIndex=0;Find<ScrollViewer>("ContentScroller").ScrollToEnd();
                await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                foreach(string name in new[]{"InspectButton","ScriptButton","ApplyButton","CloseButton"})
                {
                    var action=Find<Button>(name);var content=(FrameworkElement)dialog.Content;var point=action.TranslatePoint(new Point(),content);
                    check(point.X>=-0.5 && point.Y>=-0.5 && point.X+action.ActualWidth<=content.ActualWidth+0.5 && point.Y+action.ActualHeight<=content.ActualHeight+0.5,"Column permission action fits "+(small?"minimum":"normal")+": "+name);
                }
                check(Find<TextBox>("Preview").ActualHeight>100 && Find<ScrollViewer>("ContentScroller").VerticalOffset>0,"Column preview remains reachable through vertical scroll at "+(small?"minimum":"normal")+" size");
                if(Environment.GetEnvironmentVariable("PGMS_COLUMN_PRIVILEGE_REVIEW")=="1")
                {
                    Capture(dialog,"m517-column-sql-"+(small?"small":"normal"));
                    Find<TabControl>("PermissionTabs").SelectedItem=Find<TabItem>("EffectiveAccessTab");Find<TabControl>("AccessLists").SelectedItem=Find<TabItem>("AclTab");
                    var grid=Find<DataGrid>("AclRows");grid.SelectedItem=dialog.AccessSnapshot!.AclEntries.First(e=>e.Column==column && e.Grantee==role);grid.ScrollIntoView(grid.SelectedItem);
                    Find<ScrollViewer>("ContentScroller").ScrollToEnd();await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Capture(dialog,"m517-column-acl-"+(small?"small":"normal"));
                }
            }
        }
        finally {dialog.CancelCurrentOperation();dialog.Close();await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr};");}
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
