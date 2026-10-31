using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class SequencePrivilegeDialogChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="seq_ui_"+suffix,role="seq_ui_role_"+suffix,name="counter'\"é";
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),sequence=ObjectSql.Relation(schema,name);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var command=new NpgsqlCommand(sql,admin);await command.ExecuteNonQueryAsync();}
        async Task<(long,bool)> State(){await using var command=new NpgsqlCommand($"SELECT last_value,is_called FROM {sequence}",admin);await using var row=await command.ExecuteReaderAsync();await row.ReadAsync();return(row.GetInt64(0),row.GetBoolean(1));}
        var dialog=new PrivilegeDialog(connection){ShowInTaskbar=false};
        T Find<T>(string element) where T:UIElement=>(T)dialog.FindName(element);
        void Target(PrivilegeDialog window)
        {
            ((ComboBox)window.FindName("ObjectType")).SelectedItem=PrivilegeTarget.Sequence;
            ((TextBox)window.FindName("SchemaName")).Text=schema;((TextBox)window.FindName("ObjectName")).Text=name;
        }
        try
        {
            await Sql($"CREATE ROLE {qr} LOGIN;CREATE SCHEMA {qs};CREATE SEQUENCE {sequence} START 33 CACHE 1;GRANT USAGE ON SCHEMA {qs} TO {qr};");
            dialog.Show();Target(dialog);Find<TextBox>("RoleName").Text=role;
            check(Find<TextBox>("SchemaName").IsEnabled && Find<ComboBox>("Privilege").Items.Cast<string>().SequenceEqual(new[]{"USAGE","SELECT","UPDATE"}),"Sequence UI requires schema and offers only its three supported privileges");
            check(Find<TextBlock>("SequencePrivilegeHelp").IsVisible && Find<TextBlock>("SequencePrivilegeHelp").Text.Contains("setval"),"Sequence UI explains state-changing privilege separately from inspection");
            Find<TextBox>("SchemaName").Clear();
            check(!Find<Button>("ApplyButton").IsEnabled && !Find<Button>("InspectButton").IsEnabled,"Empty sequence schema blocks Apply and Inspect");
            Find<TextBox>("SchemaName").Text=schema;
            foreach(string privilege in new[]{"USAGE","SELECT","UPDATE"})
            {
                Find<ComboBox>("Privilege").SelectedItem=privilege;
                check(Find<TextBox>("Preview").Text==new PrivilegeRequest(PrivilegeTarget.Sequence,name,role,privilege,Schema:schema).ToSql(),"Sequence preview binds exact schema and privilege: "+privilege);
            }
            Find<CheckBox>("PublicRecipient").IsChecked=true;
            check(Find<TextBox>("Preview").Text.EndsWith("TO PUBLIC;") && !Find<CheckBox>("GrantOption").IsEnabled,"Sequence PUBLIC preview keeps delegation unavailable");
            var scripted=new PrivilegeDialog(connection){ShowInTaskbar=false};
            scripted.Loaded+=(_,_)=>{Target(scripted);((CheckBox)scripted.FindName("PublicRecipient")).IsChecked=true;((Button)scripted.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
            scripted.ShowDialog();
            check(scripted.ScriptSql==new PrivilegeRequest(PrivilegeTarget.Sequence,name,"","USAGE",Schema:schema,RecipientIsPublic:true).ToSql(),"Sequence Script returns exact PUBLIC SQL without execution");
            check(!(await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Sequence,name,schema)).AclEntries.Any(e=>e.GranteeOid==0),"Sequence Script creates no PUBLIC grant");
            Find<CheckBox>("PublicRecipient").IsChecked=false;Find<TextBox>("RoleName").Clear();
            check(Find<Button>("InspectButton").IsEnabled && !Find<Button>("ApplyButton").IsEnabled,"Sequence Inspect does not require a grant recipient");
            await dialog.InspectAsync();
            check(dialog.AccessSnapshot is {Target:PrivilegeTarget.Sequence,RowSecurityActive:null} && Find<DataGrid>("AccessRows").Items.Count==3,"WPF sequence snapshot shows three privilege rows and no table RLS context");
            check(Find<DataGrid>("AclRows").Items.Count==3 && dialog.AccessSnapshot!.AclEntries.All(e=>e.Scope=="Sequence" && e.Source==PrivilegeAclSource.BuiltInDefault),"WPF sequence ACL shows sequence-specific built-in defaults");
            check(await State()==(33L,false),"WPF Script and Inspect do not consume sequence state");
            check(UIElementAutomationPeer.CreatePeerForElement(Find<TextBlock>("SequencePrivilegeHelp"))?.GetName()=="Sequence privilege semantics","Sequence semantics help has an automation name");
            Find<TextBox>("RoleName").Text=role;Find<ComboBox>("Privilege").SelectedItem="USAGE";Find<CheckBox>("GrantOption").IsChecked=true;
            await dialog.ApplyAsync();
            string limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
            var limitedInspect=new EffectivePrivilegeService(limitedConnection);
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).Privileges.Single(p=>p.Privilege=="USAGE") is {Allowed:true,GrantOption:true},"Sequence WPF Apply grants real USAGE delegation to selected role");
            check(dialog.AccessSnapshotIsStale && Find<TextBox>("RoleName").Text==role,"Sequence Apply preserves recipient and marks old observation stale");
            Find<CheckBox>("Revoke").IsChecked=true;await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).Privileges.Single(p=>p.Privilege=="USAGE") is {Allowed:true,GrantOption:false},"Sequence WPF revokes only grant option while preserving USAGE");
            Find<CheckBox>("PublicRecipient").IsChecked=true;Find<CheckBox>("Revoke").IsChecked=false;await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).AclEntries.Any(e=>e.GranteeOid==0 && e.Privilege=="USAGE"),"Sequence PUBLIC Apply creates real ACL entry");
            Find<CheckBox>("Revoke").IsChecked=true;await dialog.ApplyAsync();
            var noPublic=await limitedInspect.InspectAsync(PrivilegeTarget.Sequence,name,schema);
            check(!noPublic.AclEntries.Any(e=>e.GranteeOid==0) && noPublic.Privileges.Single(p=>p.Privilege=="USAGE").Allowed,"Sequence PUBLIC revoke preserves direct role grant");
            Find<CheckBox>("PublicRecipient").IsChecked=false;Find<CheckBox>("Revoke").IsChecked=false;Find<ComboBox>("Privilege").SelectedItem="UPDATE";await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Sequence,name,schema)).Privileges.Single(p=>p.Privilege=="UPDATE").Allowed,"Sequence WPF applies UPDATE as a distinct privilege");
            check(await State()==(33L,false),"Sequence permission Apply and revoke do not advance or set its value");
            await dialog.InspectAsync();
            check(!dialog.AccessSnapshotIsStale && dialog.AccessSnapshot!.AclEntries.Any(e=>e.Grantee==role && e.Privilege=="UPDATE" && e.Source==PrivilegeAclSource.Stored),"Sequence refresh replaces stale observation with stored ACL from server");
            var prior=dialog.AccessSnapshot;
            await Sql($"DROP SEQUENCE {sequence};");await dialog.InspectAsync();
            check(ReferenceEquals(prior,dialog.AccessSnapshot) && dialog.AccessSnapshotIsStale && Find<DataGrid>("AclRows").Items.Count>0,"Missing sequence refresh preserves prior ACL with stale warning");
            await Sql($"CREATE TABLE {sequence}(id integer);");await dialog.InspectAsync();
            check(ReferenceEquals(prior,dialog.AccessSnapshot) && Find<TextBlock>("Feedback").Text.Contains("unsupported kind"),"Same-name table cannot replace sequence permission snapshot");
            await Sql($"DROP TABLE {sequence};CREATE SEQUENCE {sequence} START 77;");await dialog.InspectAsync();
            check(dialog.AccessSnapshot!.Oid!=prior!.Oid && !dialog.AccessSnapshotIsStale && dialog.AccessSnapshot.AclEntries.All(e=>e.Grantee!=role) && await State()==(77L,false),"Sequence retry observes new OID without old grants or value consumption");
            Find<TextBox>("SchemaName").Text=schema+"_different";
            check(dialog.AccessSnapshot is null && Find<DataGrid>("AccessRows").ItemsSource is null && Find<DataGrid>("AclRows").ItemsSource is null,"Changing sequence schema clears prior effective rows and ACL");
            Find<TextBox>("SchemaName").Text=schema;await dialog.InspectAsync();Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
            check(dialog.AccessSnapshot is null && Find<TextBlock>("SequencePrivilegeHelp").Visibility==Visibility.Collapsed,"Switching target type clears sequence context and hides sequence semantics");
            Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Sequence;await dialog.InspectAsync();
            using var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
            int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var stalled=new PrivilegeDialog($"Host=127.0.0.1;Port={port};Database=postgres;Username=pgms_test;SSL Mode=Disable;Pooling=false;Timeout=2"){ShowInTaskbar=false};
            Task? pending=null;
            try
            {
                stalled.Show();Target(stalled);pending=stalled.InspectAsync();
                check(stalled.IsBusy && !((TextBox)stalled.FindName("SchemaName")).IsEnabled && ((Button)stalled.FindName("CancelInspectionButton")).IsEnabled,"Pending sequence inspection protects schema and exposes cancellation");
                stalled.CancelAccessInspection();await pending.WaitAsync(TimeSpan.FromSeconds(6));
                check(!stalled.IsBusy && stalled.IsVisible && ((TextBlock)stalled.FindName("Feedback")).Text.Contains("canceled"),"Sequence cancellation restores retry without closing its dialog");
            }
            finally{stalled.CancelAccessInspection();if(pending is not null)await pending.WaitAsync(TimeSpan.FromSeconds(6));stalled.Close();listener.Stop();}
            Find<TabControl>("PermissionTabs").SelectedIndex=0;Find<ScrollViewer>("ContentScroller").ScrollToTop();
            await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"h",false);
            check(Find<TextBox>("SchemaName").IsKeyboardFocused,"Schema mnemonic remains usable for sequences");
            check(!Find<Button>("ApplyButton").IsDefault,"Sequence Apply never becomes the default Enter action");
            foreach(bool small in new[]{false,true})
            {
                dialog.Width=small?dialog.MinWidth:760;dialog.Height=small?dialog.MinHeight:760;
                Find<TextBox>("RoleName").Text=role;Find<ComboBox>("Privilege").SelectedItem="USAGE";Find<CheckBox>("PublicRecipient").IsChecked=true;
                Find<TabControl>("PermissionTabs").SelectedIndex=0;Find<ScrollViewer>("ContentScroller").ScrollToTop();
                await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                foreach(string element in new[]{"InspectButton","ScriptButton","ApplyButton","CloseButton"})
                {
                    var button=Find<Button>(element);var content=(FrameworkElement)dialog.Content;var point=button.TranslatePoint(new Point(),content);
                    check(point.X>=-0.5 && point.Y>=-0.5 && point.X+button.ActualWidth<=content.ActualWidth+0.5 && point.Y+button.ActualHeight<=content.ActualHeight+0.5,"Sequence permission action fits "+(small?"minimum":"normal")+" viewport: "+element);
                }
                if(Environment.GetEnvironmentVariable("PGMS_SEQUENCE_PRIVILEGE_REVIEW")=="1")
                {
                    Capture(dialog,"m467-sequence-sql-"+(small?"small":"normal"));
                    Find<TabControl>("PermissionTabs").SelectedItem=Find<TabItem>("EffectiveAccessTab");Find<TabControl>("AccessLists").SelectedItem=Find<TabItem>("AclTab");
                    Find<DataGrid>("AclRows").SelectedIndex=0;Find<ScrollViewer>("ContentScroller").ScrollToEnd();
                    await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Capture(dialog,"m467-sequence-acl-"+(small?"small":"normal"));
                }
            }
        }
        finally{dialog.Close();await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr};");}
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
