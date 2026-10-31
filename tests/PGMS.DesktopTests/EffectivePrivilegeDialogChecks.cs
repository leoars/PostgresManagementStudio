using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class EffectivePrivilegeDialogChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="pgms_access_"+suffix, role="pgms_access_role_"+suffix;
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) {await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        string qSchema=SqlIdentifier.Quote(schema),qRole=SqlIdentifier.Quote(role);
        try
        {
            await Sql($"CREATE ROLE {qRole} LOGIN; CREATE SCHEMA {qSchema}; CREATE TABLE {qSchema}.items(id integer, value text); GRANT USAGE ON SCHEMA {qSchema} TO {qRole};");
            var limited=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
            var dialog=new PrivilegeDialog(limited){ShowInTaskbar=false}; dialog.Show();
            try
            {
                T Find<T>(string name)=>(T)dialog.FindName(name);
                check(Find<Button>("InspectButton").IsEnabled && !Find<Button>("ApplyButton").IsEnabled,"Access inspection works before a GRANT recipient is supplied");
                await dialog.Dispatcher.InvokeAsync(() => {},DispatcherPriority.ApplicationIdle);
                Find<Button>("InspectButton").Focus();
                await dialog.InspectAsync();
                check(Find<Button>("InspectButton").IsKeyboardFocused,"Inspection restores focus to its initiating action");
                check(dialog.AccessSnapshot is {Target:PrivilegeTarget.Database} && dialog.AccessSnapshot.Role==role && dialog.AccessSnapshot.Privileges.Count==3,"Permissions window inspects connected limited role database");
                var initial=dialog.AccessSnapshot;
                check(ReferenceEquals(Find<DataGrid>("AclRows").ItemsSource,initial!.AclEntries),"ACL grid binds the same database snapshot as effective privileges");
                Find<TextBox>("RoleName").Text="recipient_does_not_exist";
                check(ReferenceEquals(initial,dialog.AccessSnapshot),"Editing GRANT recipient does not pretend to inspect that role");
                Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
                Find<TextBox>("SchemaName").Text=schema; Find<TextBox>("ObjectName").Text="items";
                check(dialog.AccessSnapshot is null && Find<DataGrid>("AccessRows").ItemsSource is null,"Changing inspected object clears prior permission rows");
                check(Find<DataGrid>("AclRows").ItemsSource is null && Find<TextBlock>("AclNotice").Text.Contains("Target changed"),"Changing target clears ACL entries and their context");
                await dialog.InspectAsync();
                check(dialog.AccessSnapshot?.Privileges.Count==16 && Find<DataGrid>("AccessRows").Items.Count==16,"Table inspection displays eight whole-table and eight column privilege rows");
                check(dialog.AccessSnapshot!.Privileges.All(p=>!p.Allowed),"Inspect does not give a limited role table data access");
                check(Find<DataGrid>("AclRows").IsReadOnly && Find<DataGrid>("AclRows").Items.Count==8 && dialog.AccessSnapshot.AclEntries.All(e=>e.Source==PrivilegeAclSource.BuiltInDefault),"Table ACL grid shows read-only built-in defaults without implying data authority");
                Find<TabControl>("AccessLists").SelectedItem=Find<TabItem>("AclTab");
                await Sql($"GRANT SELECT ON {qSchema}.items TO {qRole};");
                await dialog.InspectAsync();
                check(Find<TabControl>("AccessLists").SelectedItem==Find<TabItem>("AclTab") && dialog.AccessSnapshot!.AclEntries.Any(e=>e.Grantee==role && e.Source==PrivilegeAclSource.Stored),"Refresh preserves chosen ACL tab and replaces entries from server");
                var selectedGrant=dialog.AccessSnapshot!.AclEntries.Single(e=>e.Grantee==role && e.Privilege=="SELECT");
                Find<DataGrid>("AclRows").SelectedItem=selectedGrant;
                check(Find<TextBox>("AclDetails").IsReadOnly && Find<TextBox>("AclDetails").Text.Contains(role) && Find<TextBox>("AclDetails").Text.Contains($"OID {selectedGrant.GrantorOid}") && Find<TextBox>("AclDetails").Text.Contains($"OID {selectedGrant.GranteeOid}"),"Selected ACL details preserve complete recipient and both role OIDs");


                foreach(string name in new[]{"ObjectType","SchemaName","ObjectName","RoleName","Privilege","InspectButton","AccessRows","AccessSummary","AclRows","AclDetails","AclNotice","AclTab"})
                    check(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(Find<UIElement>(name))?.GetName()),"Permission control has an automation name: "+name);
                check(!Find<Button>("ApplyButton").IsDefault,"Enter never defaults to permission Apply");
                if(Environment.GetEnvironmentVariable("PGMS_ACCESS_REVIEW")=="1")
                {
                    Find<ScrollViewer>("ContentScroller").ScrollToEnd();
                    await dialog.Dispatcher.InvokeAsync(() => {},DispatcherPriority.ApplicationIdle);
                    Find<DataGrid>("AclRows").ScrollIntoView(selectedGrant);
                    await dialog.Dispatcher.InvokeAsync(() => {},DispatcherPriority.ApplicationIdle);
                    Capture(dialog,"m393-acl-normal");
                }
                dialog.Width=dialog.MinWidth;dialog.Height=dialog.MinHeight;
                await dialog.Dispatcher.InvokeAsync(() => {},DispatcherPriority.ApplicationIdle);
                foreach(string name in new[]{"InspectButton","ScriptButton","ApplyButton","CloseButton"})
                {
                    var button=Find<Button>(name);var point=button.TranslatePoint(new Point(),(UIElement)dialog.Content);
                    check(point.X>=0 && point.X+button.ActualWidth<=((FrameworkElement)dialog.Content).ActualWidth && point.Y>=0 && point.Y+button.ActualHeight<=((FrameworkElement)dialog.Content).ActualHeight,"Permission action fits minimum viewport: "+name);
                }
                if(Environment.GetEnvironmentVariable("PGMS_ACCESS_REVIEW")=="1")
                {
                    Find<ScrollViewer>("ContentScroller").ScrollToEnd();
                    Find<DataGrid>("AclRows").ScrollIntoView(selectedGrant,Find<DataGrid>("AclRows").Columns[^1]);
                    await dialog.Dispatcher.InvokeAsync(() => {},DispatcherPriority.ApplicationIdle);
                    Capture(dialog,"m393-acl-small");
                }
                var prior=dialog.AccessSnapshot;
                await Sql($"DROP TABLE {qSchema}.items;");
                await dialog.InspectAsync();
                check(ReferenceEquals(prior,dialog.AccessSnapshot) && dialog.AccessSnapshotIsStale && Find<TextBox>("AccessSummary").Text.StartsWith("Outdated"),"Failed reinspection retains and marks the previous observation");
                check(!dialog.IsBusy && Find<Button>("InspectButton").IsEnabled && Find<TextBlock>("Feedback").Text.Contains("Previous snapshot"),"Inspection error releases controls and permits retry");
                check(ReferenceEquals(Find<DataGrid>("AclRows").ItemsSource,prior.AclEntries) && Find<TextBlock>("AclNotice").Text.Contains("outdated"),"Failed refresh retains the same ACL list with a stale warning");
                check(Find<TextBox>("AclDetails").Text.Contains(role),"Failed refresh retains selected ACL detail under the stale warning");
                await Sql($"CREATE TABLE {qSchema}.items(id integer,value text);");
                await dialog.InspectAsync();
                check(dialog.AccessSnapshot!.Oid!=prior.Oid && !dialog.AccessSnapshotIsStale,"Successful refresh reports replacement OID and clears stale state");
                check(!dialog.AccessSnapshot.AclEntries.Any(e=>e.Grantee==role) && !Find<TextBlock>("AclNotice").Text.Contains("outdated"),"Replacement table removes obsolete ACL recipients and warning");
                check(Find<DataGrid>("AclRows").SelectedItem is null && Find<TextBox>("AclDetails").Text.StartsWith("Select"),"Replacing an ACL list clears obsolete selected detail");
                await Sql($"CREATE TABLE {qSchema}.wide({string.Join(",",Enumerable.Range(0,257).Select(i=>$"c{i} integer"))}); GRANT SELECT(c256) ON {qSchema}.wide TO {qRole};");
                Find<TextBox>("ObjectName").Text="wide";
                await dialog.InspectAsync();
                check(dialog.AccessSnapshot!.ColumnsTruncated && !dialog.AccessSnapshot.AclTruncated && Find<TextBlock>("AclNotice").Text.Contains("256") && !Find<TextBlock>("AclNotice").Text.Contains("Entry limit"),"Column-only scope cut has a distinct persistent ACL warning");
                string columns=string.Join(",",Enumerable.Range(0,256).Select(i=>$"c{i}"));
                await Sql($"GRANT SELECT({columns}),UPDATE({columns}) ON {qSchema}.wide TO {qRole};");
                await dialog.InspectAsync();
                check(dialog.AccessSnapshot!.AclTruncated && Find<DataGrid>("AclRows").Items.Count==512 && Find<TextBlock>("AclNotice").Text.Contains("512") && Find<TextBlock>("AclNotice").Text.Contains("256"),"ACL list exposes simultaneous entry and column limits without exceeding512 rows");
                string notice=Find<TextBlock>("AclNotice").Text;
                Find<TextBox>("RoleName").Text=role;
                check(Find<TextBlock>("AclNotice").Text==notice,"Editing GRANT recipient does not erase ACL truncation warnings");
                Find<DataGrid>("AclRows").SelectedIndex=0;
                Find<TextBox>("ObjectName").Text="items";
                check(dialog.AccessSnapshot is null && Find<TextBox>("AclDetails").Text.StartsWith("Select"),"Target changes clear selected ACL names and OIDs");
                await Sql($"CREATE TABLE {qSchema}.empty_acl(id integer); REVOKE ALL ON {qSchema}.empty_acl FROM pgms_test;");
                Find<TextBox>("ObjectName").Text="empty_acl";
                await dialog.InspectAsync();
                check(Find<DataGrid>("AclRows").Items.Count==0 && Find<TextBlock>("AclNotice").Text.Contains("effective authority may still exist"),"Empty ACL view does not claim absence of effective authority");


            }
            finally { dialog.Close(); }
            var writer=new PrivilegeDialog(connection){ShowInTaskbar=false};writer.Show();
            try
            {
                ((TextBox)writer.FindName("RoleName")).Text=role;
                await writer.InspectAsync();
                ((Button)writer.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline=DateTime.UtcNow.AddSeconds(5);
                while(writer.IsBusy && DateTime.UtcNow<deadline) await Task.Delay(10);
                check(!writer.IsBusy && writer.AccessSnapshotIsStale && ((TextBlock)writer.FindName("Feedback")).Text.Contains("Command completed"),"Successful GRANT keeps previous access snapshot explicitly stale");
                check(((TextBlock)writer.FindName("AclNotice")).Text.Contains("outdated"),"Permission Apply marks retained ACL observation stale too");
            }
            finally {writer.Close();}
            using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var stalledConnection=new NpgsqlConnectionStringBuilder(connection){Port=((IPEndPoint)listener.LocalEndpoint).Port,Timeout=2,SslMode=SslMode.Disable}.ConnectionString;
            var stalled=new PrivilegeDialog(stalledConnection){ShowInTaskbar=false};stalled.Show();
            try
            {
                var accepted=listener.AcceptTcpClientAsync();
                var pending=stalled.InspectAsync();
                using var socket=await accepted.WaitAsync(TimeSpan.FromSeconds(4));
                check(stalled.IsBusy && !((Button)stalled.FindName("ApplyButton")).IsEnabled && !((StackPanel)stalled.FindName("Fields")).IsEnabled,"Pending inspection freezes editing and permission Apply");
                await stalled.InspectAsync();
                check(stalled.IsBusy && !pending.IsCompleted,"Repeated Inspect does not replace an active inspection");
                check(((Button)stalled.FindName("CancelInspectionButton")).IsKeyboardFocused,"Pending access inspection focuses cancel");
                ((TabControl)stalled.FindName("PermissionTabs")).SelectedIndex=0;
                await stalled.Dispatcher.InvokeAsync(() => {},DispatcherPriority.ApplicationIdle);
                check(((TextBox)stalled.FindName("Preview")).Focus(),"User preview focus is established before cancellation completes");
                stalled.Close();
                check(stalled.IsVisible && !((Button)stalled.FindName("CancelInspectionButton")).IsEnabled,"Closing an active inspection requests cancellation and retains its window");
                await pending.WaitAsync(TimeSpan.FromSeconds(6));
                check(!stalled.IsBusy && stalled.AccessSnapshot is null && ((TextBlock)stalled.FindName("Feedback")).Text.Contains("canceled"),"Canceled startup releases controls without inventing a snapshot");
                check(((DataGrid)stalled.FindName("AclRows")).ItemsSource is null,"Canceled startup never publishes a partial ACL list");
                check(((TextBox)stalled.FindName("Preview")).IsKeyboardFocused && ((TabControl)stalled.FindName("PermissionTabs")).SelectedIndex==0,"Inspection completion preserves user-chosen preview tab and focus");
                var secondAccepted=listener.AcceptTcpClientAsync();
                SendKey(stalled,Key.F5);
                using var secondSocket=await secondAccepted.WaitAsync(TimeSpan.FromSeconds(4));
                check(stalled.IsBusy,"F5 routes to real access inspection");
                SendKey(stalled,Key.Escape);
                var deadline=DateTime.UtcNow.AddSeconds(6);
                while(stalled.IsBusy && DateTime.UtcNow<deadline) await Task.Delay(10);
                check(!stalled.IsBusy && stalled.IsVisible && ((TextBlock)stalled.FindName("Feedback")).Text.Contains("canceled"),"Escape cancels access inspection without closing window");
            }
            finally {stalled.CancelAccessInspection();stalled.Close();}
        }
        finally {await Sql($"DROP SCHEMA IF EXISTS {qSchema} CASCADE; DROP OWNED BY {qRole}; DROP ROLE IF EXISTS {qRole};");}
    }
    private static void SendKey(Window window,Key key)
    {
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
    }
    private static void Capture(Window window,string name)
    {
        var element=(FrameworkElement)window.Content;element.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var drawing=new System.Windows.Media.DrawingVisual();
        using(var dc=drawing.RenderOpen())
        {
            var bounds=new Rect(0,0,element.ActualWidth,element.ActualHeight);
            dc.DrawRectangle(window.Background,null,bounds);
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(element),null,bounds);
        }
        bitmap.Render(drawing);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory("artifacts/review");using var output=System.IO.File.Create($"artifacts/review/{name}.png");encoder.Save(output);
    }

}
