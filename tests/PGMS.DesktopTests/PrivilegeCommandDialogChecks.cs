using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PrivilegeCommandDialogChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"), schema="perm_ui_"+suffix, role="perm_r_é'\"_"+suffix;
        string future="perm_future_"+suffix;
        string qs=SqlIdentifier.Quote(schema), qr=SqlIdentifier.Quote(role), qf=SqlIdentifier.Quote(future);
        await using var admin=new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) {await using var command=new NpgsqlCommand(sql,admin);await command.ExecuteNonQueryAsync();}
        var dialog=new PrivilegeDialog(connection){ShowInTaskbar=false};
        T Find<T>(string name) where T:UIElement => (T)dialog.FindName(name);
        void Target(PrivilegeDialog window)
        {
            ((ComboBox)window.FindName("ObjectType")).SelectedItem=PrivilegeTarget.Table;
            ((TextBox)window.FindName("SchemaName")).Text=schema;
            ((TextBox)window.FindName("ObjectName")).Text="items";
        }
        try
        {
            await Sql($"CREATE ROLE {qr} LOGIN; CREATE ROLE {qf} LOGIN; CREATE SCHEMA {qs}; CREATE TABLE {qs}.items(id integer); GRANT USAGE ON SCHEMA {qs} TO {qr};");
            dialog.Show(); Target(dialog);
            check(Find<CheckBox>("PublicRecipient").IsChecked!=true && Find<CheckBox>("GrantOption").IsChecked!=true && Find<TextBox>("RoleName").IsEnabled,"Permission options default to an exact nondelegable role recipient");
            check(!Find<Button>("ApplyButton").IsEnabled && Find<Button>("InspectButton").IsEnabled,"Empty role blocks Apply but not access inspection");
            Find<TextBox>("RoleName").Text="PUBLIC";
            check(Find<TextBox>("Preview").Text.EndsWith("TO \"PUBLIC\";"),"Typing PUBLIC leaves an exact quoted role in UI preview");
            Find<TextBox>("RoleName").Text=role;
            Find<CheckBox>("GrantOption").IsChecked=true;
            check(Find<TextBox>("Preview").Text.EndsWith("WITH GRANT OPTION;"),"Grant option checkbox updates SQL preview");
            Find<CheckBox>("Revoke").IsChecked=true;
            check(Find<TextBox>("Preview").Text.StartsWith("REVOKE GRANT OPTION FOR SELECT") && Find<TextBox>("Preview").Text.EndsWith("RESTRICT;"),"Revoke option-only preview remains restrictive");
            Find<CheckBox>("PublicRecipient").IsChecked=true;
            check(!Find<TextBox>("RoleName").IsEnabled && Find<TextBox>("RoleName").Text==role && !Find<CheckBox>("GrantOption").IsEnabled && Find<CheckBox>("GrantOption").IsChecked!=true,"PUBLIC disables role and clears incompatible delegation without erasing the role text");
            check(Find<TextBox>("Preview").Text.EndsWith("FROM PUBLIC RESTRICT;") && Find<TextBlock>("Feedback").Text.Contains("current and future roles"),"PUBLIC preview and feedback explain its explicit scope");
            Find<CheckBox>("PublicRecipient").IsChecked=false;
            check(Find<TextBox>("RoleName").IsEnabled && Find<TextBox>("RoleName").Text==role && Find<CheckBox>("GrantOption").IsChecked!=true,"Leaving PUBLIC restores exact role without silently restoring delegation");
            await dialog.InspectAsync();
            var snapshot=dialog.AccessSnapshot;
            check(snapshot is not null,"Permission command fixture loads an access snapshot");
            Find<CheckBox>("PublicRecipient").IsChecked=true;
            check(ReferenceEquals(snapshot,dialog.AccessSnapshot) && snapshot!.Role=="pgms_test","PUBLIC recipient does not replace the connected-role observation");
            foreach(string name in new[]{"PublicRecipient","GrantOption"})
                check(!string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(Find<UIElement>(name))?.GetName()),"Permission option has an automation name: "+name);
            check(!Find<Button>("ApplyButton").IsDefault,"New permission options do not make Apply the default Enter action");
            var scripted=new PrivilegeDialog(connection){ShowInTaskbar=false};
            scripted.Loaded+=(_,_)=>
            {
                Target(scripted);
                ((CheckBox)scripted.FindName("PublicRecipient")).IsChecked=true;
                ((Button)scripted.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            scripted.ShowDialog();
            check(scripted.ScriptSql==new PrivilegeRequest(PrivilegeTarget.Table,"items","","SELECT",Schema:schema,RecipientIsPublic:true).ToSql(),"Script returns exact PUBLIC SQL without requiring a typed role");
            check(!(await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"items",schema)).AclEntries.Any(e=>e.GranteeOid==0),"Scripting PUBLIC creates no server grant");
            Find<CheckBox>("PublicRecipient").IsChecked=false;
            Find<CheckBox>("Revoke").IsChecked=false;
            Find<CheckBox>("GrantOption").IsChecked=true;
            await using(var locked=await admin.BeginTransactionAsync())
            {
                await using(var command=new NpgsqlCommand($"LOCK TABLE {qs}.items IN ACCESS EXCLUSIVE MODE",admin,locked)) await command.ExecuteNonQueryAsync();
                var applying=dialog.ApplyAsync();
                try
                {
                    check(dialog.IsBusy && !Find<CheckBox>("PublicRecipient").IsEnabled && !Find<CheckBox>("GrantOption").IsEnabled && !Find<Button>("InspectButton").IsEnabled,"Applying permissions disables recipient delegation and duplicate inspection");
                    await dialog.InspectAsync();
                    check(ReferenceEquals(snapshot,dialog.AccessSnapshot),"Duplicate inspection during Apply cannot replace the observed context");
                }
                finally {await locked.CommitAsync();await applying;}
            }
            var limitedConnection=new NpgsqlConnectionStringBuilder(connection){Username=role}.ConnectionString;
            var limitedInspect=new EffectivePrivilegeService(limitedConnection);
            var granted=await limitedInspect.InspectAsync(PrivilegeTarget.Table,"items",schema);
            check(granted.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:true},"WPF Apply grants real delegation to the exact limited role");
            check(dialog.AccessSnapshotIsStale && Find<TextBox>("RoleName").Text==role,"Permission Apply marks prior snapshot stale and preserves recipient");
            var delegated=new PrivilegeRequest(PrivilegeTarget.Table,"items",future,"SELECT",Schema:schema);
            var delegateService=new PrivilegeService(limitedConnection);
            await delegateService.ApplyAsync(delegated);
            Find<CheckBox>("Revoke").IsChecked=true;
            await dialog.ApplyAsync();
            check(Find<TextBlock>("Feedback").Text.StartsWith("2BP01:") && !dialog.IsBusy && Find<Button>("ApplyButton").IsEnabled,"Dependent grant failure preserves SQLSTATE and retry controls in WPF");
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Table,"items",schema)).Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").GrantOption,"Failed WPF revoke preserves server grant option");
            await delegateService.ApplyAsync(delegated with {Revoke=true});
            await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Table,"items",schema)).Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT") is {Allowed:true,GrantOption:false} && Find<TextBlock>("Feedback").Text.StartsWith("Command completed"),"WPF retry removes only grant option after explicit downstream cleanup");
            Find<CheckBox>("PublicRecipient").IsChecked=true;
            Find<CheckBox>("Revoke").IsChecked=false;
            await dialog.ApplyAsync();
            check((await limitedInspect.InspectAsync(PrivilegeTarget.Table,"items",schema)).AclEntries.Any(e=>e.GranteeOid==0 && e.Privilege=="SELECT"),"WPF PUBLIC Apply creates real PUBLIC ACL entry");
            Find<CheckBox>("Revoke").IsChecked=true;
            await dialog.ApplyAsync();
            var afterPublic=await limitedInspect.InspectAsync(PrivilegeTarget.Table,"items",schema);
            check(!afterPublic.AclEntries.Any(e=>e.GranteeOid==0) && afterPublic.Privileges.Single(p=>p.Scope=="Table" && p.Privilege=="SELECT").Allowed,"WPF PUBLIC revoke preserves an independent role grant");
            Find<CheckBox>("PublicRecipient").IsChecked=false;Find<TextBox>("RoleName").Clear();
            await dialog.ApplyAsync();
            check(!dialog.IsBusy && !Find<Button>("ApplyButton").IsEnabled && (await limitedInspect.InspectAsync(PrivilegeTarget.Table,"items",schema)).AclEntries.Any(e=>e.Grantee==role),"Disabled invalid Apply cannot mutate permissions through its async entry point");
            Find<TextBox>("RoleName").Text=role; Find<CheckBox>("Revoke").IsChecked=false;
            Find<TabControl>("PermissionTabs").SelectedIndex=0;
            Find<ScrollViewer>("ContentScroller").ScrollToTop();
            await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"p",false);
            check(Find<CheckBox>("PublicRecipient").IsChecked==true,"PUBLIC mnemonic toggles the explicit recipient");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"p",false);
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"g",false);
            check(Find<CheckBox>("PublicRecipient").IsChecked!=true && Find<CheckBox>("GrantOption").IsChecked==true,"Grant-option mnemonic toggles delegation after leaving PUBLIC");
            foreach(bool small in new[]{false,true})
            {
                dialog.Width=small?dialog.MinWidth:760;dialog.Height=small?dialog.MinHeight:760;
                Find<CheckBox>("GrantOption").IsChecked=false;Find<CheckBox>("PublicRecipient").IsChecked=true;Find<CheckBox>("Revoke").IsChecked=false;
                Find<ScrollViewer>("ContentScroller").ScrollToTop();
                await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                foreach(string name in new[]{"InspectButton","ScriptButton","ApplyButton","CloseButton"})
                {
                    var button=Find<Button>(name);var content=(FrameworkElement)dialog.Content;var point=button.TranslatePoint(new Point(),content);
                    check(point.X>=-0.5 && point.Y>=-0.5 && point.X+button.ActualWidth<=content.ActualWidth+0.5 && point.Y+button.ActualHeight<=content.ActualHeight+0.5,"Permission action fits "+(small?"minimum":"normal")+" viewport: "+name+$" ({point.X:F3},{point.Y:F3} {button.ActualWidth:F3}x{button.ActualHeight:F3} in {content.ActualWidth:F3}x{content.ActualHeight:F3})");
                }
                if(Environment.GetEnvironmentVariable("PGMS_PRIVILEGE_REVIEW")=="1")
                {
                    Capture(dialog,"m442-public-"+(small?"small":"normal"));
                    Find<CheckBox>("PublicRecipient").IsChecked=false;Find<CheckBox>("Revoke").IsChecked=true;Find<CheckBox>("GrantOption").IsChecked=true;
                    await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Capture(dialog,"m442-delegation-"+(small?"small":"normal"));
                }
            }
        }
        finally {dialog.Close();await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE; DROP ROLE IF EXISTS {qr}; DROP ROLE IF EXISTS {qf};");}
    }
    private static void Capture(Window window,string name)
    {
        var element=(FrameworkElement)window.Content;element.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        var visual=new System.Windows.Media.DrawingVisual();
        using(var context=visual.RenderOpen())
        {
            var bounds=new Rect(0,0,element.ActualWidth,element.ActualHeight);
            context.DrawRectangle(window.Background,null,bounds);context.DrawRectangle(new System.Windows.Media.VisualBrush(element),null,bounds);
        }
        bitmap.Render(visual);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory("artifacts/review");using var output=System.IO.File.Create($"artifacts/review/{name}.png");encoder.Save(output);
    }
}
