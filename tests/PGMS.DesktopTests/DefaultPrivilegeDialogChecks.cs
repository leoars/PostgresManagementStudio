using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class DefaultPrivilegeDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),owner="dui_owner_"+suffix,role="dui_role_"+suffix,schema="dui_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(role),qs=SqlIdentifier.Quote(schema);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        string owned=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        var request=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,owner,role,"SELECT",schema);
        async Task<bool> Has()=> (await new DefaultPrivilegeCatalog(owned).InspectAsync(request.Target,owner,schema)).Entries.Any(e=>e.Recipient==role && e.Privilege=="SELECT");
        void Fill(DefaultPrivilegeDialog d){Find<TextBox>(d,"RoleName").Text=role;Find<TextBox>(d,"SchemaName").Text=schema;Find<CheckBox>(d,"GlobalScope").IsChecked=false;}
        DefaultPrivilegeDialog? dialog=null;
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            await MenuRoute(owned,Fill,check);
            dialog=new(owned){ShowInTaskbar=false,Opacity=0};dialog.Show();Fill(dialog);
            check(!Find<Button>(dialog,"ApplyButton").IsEnabled && !Find<Button>(dialog,"ScriptButton").IsEnabled,"Default WPF requires inspection before Script or Apply");
            await dialog.LoadCreatorsAsync();
            var choices=Find<ComboBox>(dialog,"Creator").Items.Cast<DefaultPrivilegeCreator>().ToArray();
            check(choices.Any(r=>r.Name==owner && r.CanLogin) && choices.Any(r=>r.Name==role && !r.CanLogin),"Default creator picker lists LOGIN and NOLOGIN roles with identities");
            check(Find<ComboBox>(dialog,"Creator").Text==owner && !Find<Button>(dialog,"ApplyButton").IsEnabled,"Loading creators preserves proposal and does not authorize Apply");
            Find<ComboBox>(dialog,"Creator").SelectedItem=choices.Single(r=>r.Name==owner);
            await dialog.InspectAsync();
            check(dialog.Snapshot?.Identity.CreatorOid==choices.Single(r=>r.Name==owner).Oid,"Inspected default creator matches the selected catalog OID");
            check(dialog.Snapshot?.Identity.Creator==owner && Find<Button>(dialog,"ApplyButton").IsEnabled,"Default WPF captures creator and policy snapshot by ordinary role");
            check(Find<TextBox>(dialog,"Preview").Text==request.ToSql(),"Default preview matches the typed proposal exactly");
            await Capture(dialog,"normal",760,700,check);await Capture(dialog,"small",650,520,check);
            Find<TabControl>(dialog,"PolicyTabs").SelectedIndex=1;await Capture(dialog,"policy",760,700,check);Find<TabControl>(dialog,"PolicyTabs").SelectedIndex=0;
            Find<ComboBox>(dialog,"Creator").Text=role;await dialog.InspectAsync();
            check(dialog.Snapshot is {CreatorPrivilegesAvailable:false} && !Find<Button>(dialog,"ApplyButton").IsEnabled && Find<Button>(dialog,"ScriptButton").IsEnabled,"Unauthorized creator remains inspectable/scriptable but WPF Apply is disabled");
            await Capture(dialog,"unauthorized-small",650,520,check);
            Find<ComboBox>(dialog,"Creator").Text=owner;await dialog.InspectAsync();
            check(dialog.Snapshot is {CreatorPrivilegesAvailable:true} && Find<Button>(dialog,"ApplyButton").IsEnabled,"Returning to authorized creator restores inspected Apply");
            await dialog.ApplyAsync();check(await Has(),"Default WPF Apply commits actual future-table policy");
            check(!Find<Button>(dialog,"ApplyButton").IsEnabled && !Find<Button>(dialog,"ScriptButton").IsEnabled,"Default WPF requires inspection after Apply");
            Find<CheckBox>(dialog,"Revoke").IsChecked=true;await dialog.InspectAsync();await dialog.ApplyAsync();check(!await Has(),"Default WPF REVOKE removes real schema policy");
            Find<CheckBox>(dialog,"GrantOption").IsChecked=true;Find<CheckBox>(dialog,"PublicRecipient").IsChecked=true;
            check(Find<CheckBox>(dialog,"GrantOption").IsChecked==false && !Find<CheckBox>(dialog,"GrantOption").IsEnabled,"Selecting PUBLIC clears invalid delegation choice");
            Find<ComboBox>(dialog,"ObjectClass").SelectedItem=DefaultPrivilegeTarget.Schemas;
            check(Find<CheckBox>(dialog,"GlobalScope").IsChecked==true && !Find<CheckBox>(dialog,"GlobalScope").IsEnabled && !Find<TextBox>(dialog,"SchemaName").IsEnabled,"Schema defaults force database-wide scope");
            dialog.Close();dialog=null;
            foreach(bool before in new[]{true,false})
            {
                await Sql((request with{Revoke=true}).ToSql());
                await using var proxy=new CommitAckProxy(before){Armed=false};string settings=new NpgsqlConnectionStringBuilder(owned){Port=proxy.Port,SslMode=SslMode.Disable}.ConnectionString;
                dialog=new(settings){ShowInTaskbar=false,Opacity=0};dialog.Show();Fill(dialog);await dialog.InspectAsync();
                if(dialog.Snapshot is null)throw new Exception("Fault proxy inspection failed before arming: "+Find<TextBlock>(dialog,"Feedback").Text);
                proxy.Armed=true;await dialog.ApplyAsync();
                check(dialog.HasUnconfirmedCommit && !Find<Button>(dialog,"ApplyButton").IsEnabled && !Find<Button>(dialog,"ScriptButton").IsEnabled,"Default lost ACK blocks duplicate mutation and script: "+before);
                await dialog.ApplyAsync();check(await Has()==!before,"Default duplicate Apply never replays uncertain command: "+before);
                dialog.Close();dialog=new(settings){ShowInTaskbar=false,Opacity=0};dialog.Show();
                check(dialog.HasUnconfirmedCommit && !Find<Grid>(dialog,"Fields").IsEnabled && Find<TextBox>(dialog,"Preview").Text==request.ToSql(),"Default window restores pending proposal with protected fields: "+before);
                if(!before)await Capture(dialog,"pending-small",650,520,check);
                proxy.RefuseNewConnections=true;await dialog.InspectAsync();check(dialog.HasUnconfirmedCommit,"Unavailable default transaction check preserves blocked state: "+before);proxy.RefuseNewConnections=false;
                for(int i=0;i<30 && dialog.HasUnconfirmedCommit;i++){await dialog.InspectAsync();if(dialog.HasUnconfirmedCommit)await Task.Delay(20);}
                check(!dialog.HasUnconfirmedCommit && Find<TextBlock>(dialog,"Feedback").Text.Contains(before?"aborted":"committed",StringComparison.Ordinal),"Reopened default window checks exact transaction without replay: "+before);
                check(!Find<Button>(dialog,"ApplyButton").IsEnabled,"Default reconciliation requires new inspection: "+before);
                await dialog.InspectAsync();check(Find<Button>(dialog,"ApplyButton").IsEnabled,"Explicit inspection recovers default window after transaction check: "+before);
                dialog.Close();dialog=null;
            }
            await Sql((request with{Revoke=true}).ToSql());
            dialog=new(owned){ShowInTaskbar=false,Opacity=0};dialog.Show();Fill(dialog);await dialog.InspectAsync();
            await Sql($"DROP SCHEMA {qs};CREATE SCHEMA {qs} AUTHORIZATION {qo}");await dialog.ApplyAsync();check(!await Has(),"Stale default scope cannot write replacement schema");
            await dialog.InspectAsync();check(!Find<Button>(dialog,"ApplyButton").IsEnabled,"Reinspection retains original scope identity after replacement");
            Find<CheckBox>(dialog,"GlobalScope").IsChecked=true;Find<CheckBox>(dialog,"GlobalScope").IsChecked=false;await dialog.InspectAsync();check(Find<Button>(dialog,"ApplyButton").IsEnabled,"Explicit scope reselection permits replacement schema inspection");
            dialog.Close();dialog=null;
            var selectionDialog=new DefaultPrivilegeDialog(connection){ShowInTaskbar=false,Opacity=0};selectionDialog.Show();
            try
            {
                Fill(selectionDialog);await selectionDialog.LoadCreatorsAsync();
                var chooser=Find<ComboBox>(selectionDialog,"Creator");var originalChoice=chooser.Items.Cast<DefaultPrivilegeCreator>().Single(r=>r.Name==owner);chooser.SelectedItem=originalChoice;
                await Sql($"ALTER ROLE {qo} RENAME TO {SqlIdentifier.Quote(owner+"x")};CREATE ROLE {qo} LOGIN");
                await selectionDialog.InspectAsync();check(selectionDialog.Snapshot is null && !Find<Button>(selectionDialog,"ApplyButton").IsEnabled,"Stale creator picker cannot silently bind replacement role before first inspection");
                await selectionDialog.LoadCreatorsAsync();var newChoice=chooser.Items.Cast<DefaultPrivilegeCreator>().Single(r=>r.Name==owner);chooser.SelectedItem=newChoice;
                await selectionDialog.InspectAsync();check(newChoice.Oid!=originalChoice.Oid && selectionDialog.Snapshot?.Identity.CreatorOid==newChoice.Oid,"Explicit creator reload and reselection binds replacement OID");
                await Sql($"DROP ROLE {qo};ALTER ROLE {SqlIdentifier.Quote(owner+"x")} RENAME TO {qo}");
            }
            finally{selectionDialog.Close();}
            using(var canceled=new CancellationTokenSource())
            {canceled.Cancel();bool stopped=false;try{await new DefaultPrivilegeCreators("Host=invalid").ListAsync(canceled.Token);}catch(OperationCanceledException){stopped=true;}check(stopped,"Pre-canceled creator listing does not connect");}
            var scriptDialog=new DefaultPrivilegeDialog(owned){ShowInTaskbar=false,Opacity=0};
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scriptDialog.Loaded+=async(_,_)=>{try{Fill(scriptDialog);await scriptDialog.InspectAsync();Find<Button>(scriptDialog,"ScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));completion.SetResult();}catch(Exception ex){completion.SetException(ex);scriptDialog.Close();}};
            scriptDialog.ShowDialog();await completion.Task;
            check(scriptDialog.ScriptSql==request.ToSql() && !await Has(),"Default Script returns reviewed SQL without changing policy");
        }
        finally{dialog?.Close();await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo}");}
    }
    private static async Task MenuRoute(string connection,Action<DefaultPrivilegeDialog> fill,Action<bool,string> check)
    {
        var window=new MainWindow{ShowInTaskbar=false,Opacity=0};window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);var action=Find<MenuItem>(window,"DefaultPermissionsMenu");
            check(action.IsEnabled,"Permissions menu exposes Default Permissions");
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
            timer.Tick+=async(_,_)=>
            {
                if(Application.Current.Windows.OfType<DefaultPrivilegeDialog>().FirstOrDefault() is not {} child)return;
                timer.Stop();try{fill(child);await child.InspectAsync();check(child.Snapshot is not null,"Default Permissions menu opens operable window in selected database");completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{child.Close();}
            };
            timer.Start();try{action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));}finally{timer.Stop();}
        }
        finally{window.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    }
    private static async Task Capture(Window dialog,string name,double width,double height,Action<bool,string> check)
    {
        dialog.Width=width;dialog.Height=height;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);dialog.UpdateLayout();var root=(FrameworkElement)dialog.Content;
        foreach(string action in new[]{"InspectButton","ScriptButton","ApplyButton","CloseButton"}){var b=Find<Button>(dialog,action);var p=b.TranslatePoint(new Point(),root);check(p.X>=0 && p.Y>=0 && p.X+b.ActualWidth<=root.ActualWidth+0.1 && p.Y+b.ActualHeight<=root.ActualHeight+0.1,"Default action fits "+name+"/"+action);}
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var rect=new Rect(0,0,root.ActualWidth,root.ActualHeight);dc.DrawRectangle(dialog.Background,null,rect);dc.DrawRectangle(new VisualBrush(root),null,rect);}bitmap.Render(drawing);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/m774-default-"+name+".png");encoder.Save(file);
    }
}
