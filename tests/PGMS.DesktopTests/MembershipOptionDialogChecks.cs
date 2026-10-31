using Npgsql;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class MembershipOptionDialogChecks
{
    public static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),group="ui_mo_é\"_"+suffix,member="ui_mo_member_"+suffix;
        string qg=SqlIdentifier.Quote(group),qm=SqlIdentifier.Quote(member);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        var dialog=new RoleMembershipDialog(connection){ShowInTaskbar=false};
        T Find<T>(string name)where T:FrameworkElement=>(T)dialog.FindName(name);
        void Target(RoleMembershipDialog w)
        {((TextBox)w.FindName("GrantedRole")).Text=group;((TextBox)w.FindName("MemberRole")).Text=member;}
        try
        {
            await Sql($"CREATE ROLE {qg};CREATE ROLE {qm};");dialog.Show();Target(dialog);
            check(Find<ComboBox>("OptionChoice").Items.Count==4 && !Find<CheckBox>("OptionEnabled").IsEnabled,"Membership dialog defaults to basic operation with value disabled");
            check(Find<TextBox>("Preview").Text==$"GRANT {qg} TO {qm};","Membership-only preview preserves basic SQL");
            foreach(var option in Enum.GetValues<MembershipOption>())
            {
                string key=option.ToString().ToUpperInvariant();Find<ComboBox>("OptionChoice").SelectedItem=option;
                Find<CheckBox>("Revoke").IsChecked=false;Find<CheckBox>("OptionEnabled").IsChecked=true;
                check(Find<TextBox>("Preview").Text==$"GRANT {qg} TO {qm} WITH {key} TRUE;","WPF membership TRUE preview: "+option);
                Find<CheckBox>("OptionEnabled").IsChecked=false;
                check(Find<TextBox>("Preview").Text==$"GRANT {qg} TO {qm} WITH {key} FALSE;","WPF membership FALSE preview: "+option);
                Find<CheckBox>("Revoke").IsChecked=true;
                check(!Find<CheckBox>("OptionEnabled").IsEnabled && Find<TextBox>("Preview").Text==$"REVOKE {key} OPTION FOR {qg} FROM {qm} RESTRICT;","WPF option-only restrictive revoke preview: "+option);
            }
            Find<ComboBox>("OptionChoice").SelectedIndex=0;
            check(Find<TextBox>("Preview").Text==$"REVOKE {qg} FROM {qm} RESTRICT;" && !Find<CheckBox>("OptionEnabled").IsEnabled,"Returning to basic revoke never retains option-only SQL");
            var scripted=new RoleMembershipDialog(connection){ShowInTaskbar=false};
            scripted.Loaded+=(_,_)=>{Target(scripted);((ComboBox)scripted.FindName("OptionChoice")).SelectedItem=MembershipOption.Inherit;((CheckBox)scripted.FindName("OptionEnabled")).IsChecked=false;((Button)scripted.FindName("ScriptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};scripted.ShowDialog();
            check(scripted.ScriptSql==$"GRANT {qg} TO {qm} WITH INHERIT FALSE;","Membership option Script returns exact SQL to editor");
            check((await new RoleMembershipService(connection).InspectAsync(member))=="No direct role memberships found.","Membership option preview and Script do not create server membership");
            Find<TextBox>("GrantedRole").Text=member;
            check(!Find<Button>("ApplyButton").IsEnabled && !Find<Button>("ScriptButton").IsEnabled,"Option mode preserves self-membership validation");
            Find<TextBox>("GrantedRole").Text=group;
            Find<CheckBox>("Revoke").IsChecked=false;Find<ComboBox>("OptionChoice").SelectedIndex=0;await dialog.ApplyAsync();
            check(Find<TextBlock>("Feedback").Text.Contains("completed") && Find<TextBox>("Inspection").Text=="","Basic WPF Apply creates membership and clears old observation");
            foreach(var option in Enum.GetValues<MembershipOption>())
            {
                Find<ComboBox>("OptionChoice").SelectedItem=option;Find<CheckBox>("OptionEnabled").IsChecked=option==MembershipOption.Admin;await dialog.ApplyAsync();
                check(Find<TextBlock>("Feedback").Text.Contains("completed"),"WPF confirms actual option change: "+option);
                await dialog.InspectAsync();
                check(Find<TextBox>("Inspection").Text.Contains(option.ToString().ToUpperInvariant()+"="+(option==MembershipOption.Admin?"True":"False")),"WPF inspection reports persisted selected flag: "+option);
            }
            foreach(var option in Enum.GetValues<MembershipOption>())
            {
                Find<ComboBox>("OptionChoice").SelectedItem=option;Find<CheckBox>("Revoke").IsChecked=false;
                if(option!=MembershipOption.Admin){Find<CheckBox>("OptionEnabled").IsChecked=true;await dialog.ApplyAsync();}
                Find<CheckBox>("Revoke").IsChecked=true;await dialog.ApplyAsync();await dialog.InspectAsync();
                check(Find<TextBox>("Inspection").Text.Contains(group) && Find<TextBox>("Inspection").Text.Contains(option.ToString().ToUpperInvariant()+"=False"),"WPF option-only revoke preserves real membership: "+option);
            }
            Find<CheckBox>("Revoke").IsChecked=false;Find<ComboBox>("OptionChoice").SelectedItem=MembershipOption.Admin;
            Find<CheckBox>("OptionEnabled").IsChecked=true;Find<TextBox>("GrantedRole").Text=group+"_missing";await dialog.ApplyAsync();
            check(Find<TextBlock>("Feedback").Text.Contains("42704") && Find<TextBox>("GrantedRole").Text.EndsWith("_missing"),"Missing granted role keeps SQLSTATE and editable proposal");
            check(Find<Button>("ApplyButton").IsEnabled && Find<Button>("CloseButton").IsEnabled && Find<TextBox>("Preview").Text.Contains("WITH ADMIN TRUE"),"Server failure restores controls without losing option SQL");
            Find<TextBox>("GrantedRole").Text=group;Find<CheckBox>("OptionEnabled").IsChecked=false;await dialog.ApplyAsync();
            check(Find<TextBlock>("Feedback").Text.Contains("notice") && !Find<TextBlock>("Feedback").Text.Contains("completed"),"No-op membership Notice remains visible without false success");
            Find<CheckBox>("OptionEnabled").IsChecked=true;await dialog.ApplyAsync();await dialog.InspectAsync();
            check(Find<TextBox>("Inspection").Text.Contains("ADMIN=True INHERIT=False SET=False"),"Corrected option proposal recovers and preserves other flags");
            Find<ScrollViewer>("ContentScroller").ScrollToTop();await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);dialog.Activate();
            check(UIElementAutomationPeer.CreatePeerForElement(Find<ComboBox>("OptionChoice"))?.GetName()=="Membership option" && UIElementAutomationPeer.CreatePeerForElement(Find<CheckBox>("OptionEnabled"))?.GetName()=="Membership option enabled","Membership option controls expose stable automation names");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"o",false);
            check(Find<ComboBox>("OptionChoice").IsKeyboardFocusWithin,"Option mnemonic focuses membership selector");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"e",false);
            check(Find<CheckBox>("OptionEnabled").IsChecked==false && Find<TextBox>("Preview").Text.Contains("ADMIN FALSE"),"Enabled mnemonic toggles explicit FALSE proposal");
            AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog),"k",false);
            check(Find<CheckBox>("Revoke").IsChecked==true && Find<TextBox>("Preview").Text.StartsWith("REVOKE ADMIN OPTION FOR"),"Revoke mnemonic selects option-only action");
            check(!Find<Button>("ApplyButton").IsDefault,"Membership option Apply is never implicit Enter action");
            foreach(bool small in new[]{false,true})
            {
                dialog.Width=small?dialog.MinWidth:760;dialog.Height=small?dialog.MinHeight:580;
                Find<CheckBox>("Revoke").IsChecked=false;Find<CheckBox>("OptionEnabled").IsChecked=true;await dialog.InspectAsync();
                Find<ScrollViewer>("ContentScroller").ScrollToEnd();await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                foreach(string name in new[]{"ScriptButton","ApplyButton","CloseButton"})
                {
                    var action=Find<Button>(name);var content=(FrameworkElement)dialog.Content;var point=action.TranslatePoint(new Point(),content);
                    check(point.X>=-0.5 && point.Y>=-0.5 && point.X+action.ActualWidth<=content.ActualWidth+0.5 && point.Y+action.ActualHeight<=content.ActualHeight+0.5,"Membership action fits "+(small?"minimum":"normal")+": "+name);
                }
                check(Find<TextBox>("Preview").ActualHeight>=140 && Find<ScrollViewer>("ContentScroller").VerticalOffset>0,"Membership preview remains reachable with scroll at "+(small?"minimum":"normal")+" size");
                if(Environment.GetEnvironmentVariable("PGMS_MEMBERSHIP_REVIEW")=="1")
                {
                    Capture(dialog,"m542-membership-grant-"+(small?"small":"normal"));
                    Find<CheckBox>("Revoke").IsChecked=true;await dialog.InspectAsync();Find<ScrollViewer>("ContentScroller").ScrollToEnd();await dialog.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                    Capture(dialog,"m542-membership-revoke-"+(small?"small":"normal"));
                }
            }
        }
        finally {dialog.Close();await Sql($"DROP ROLE IF EXISTS {qm},{qg};");}
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
