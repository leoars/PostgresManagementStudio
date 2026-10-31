using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class EnumDialogChecks
{
    private static T Find<T>(Window w,string name) where T:FrameworkElement => (T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="enum_ui_"+suffix,owner="enum_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TYPE {qs}.mood AS ENUM('ação','漢字');CREATE DOMAIN {qs}.number AS int;RESET ROLE");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        try
        {
            var catalog=new CatalogService(limited);
            var entry=(await catalog.ChildrenAsync(new(CatalogKind.Enums,"Enums",catalog.DefaultDatabase,schema))).Single();
            var domain=(await catalog.ChildrenAsync(new(CatalogKind.Domains,"Domains",catalog.DefaultDatabase,schema))).Single();
            check(entry.CanManageEnum && !domain.CanManageEnum,"Explorer enables enum operations only for actual enum leaves");
            var dialog=new EnumDialog(limited,schema,"mood",entry.Oid){ShowInTaskbar=false};dialog.Show();
            try
            {
                check(!Find<Button>(dialog,"ApplyButton").IsEnabled,"Enum Apply requires inspection and review");
                await dialog.InspectAsync();
                check(dialog.Snapshot?.Type.Oid==entry.Oid && Find<ListBox>(dialog,"Labels").Items.Count==2,"Enum dialog shows inspected ordered values for original Explorer OID");
                Find<TextBox>(dialog,"NewValue").Text="novo";
                check(!Find<Button>(dialog,"ApplyButton").IsEnabled && Find<TextBox>(dialog,"Preview").Text.Contains("ADD VALUE E'novo'"),"Enum SQL is previewed before review enables Apply");
                foreach(string id in new[]{"Operation","Existing","NewValue","Labels","Preview"})
                    check(!string.IsNullOrEmpty(UIElementAutomationPeer.CreatePeerForElement(Find<FrameworkElement>(dialog,id))?.GetName()),"Enum field has accessible name: "+id);
                Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await CascadePrivilegeDialogChecks.Capture(dialog,"enum-normal",820,700,check,"m831");
                await CascadePrivilegeDialogChecks.Capture(dialog,"enum-minimum",700,580,check,"m831");
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,false,check);
                check(!dialog.WasApplied && !dialog.RequiresParentRefresh,"Enum native No sends no command");
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                check(dialog.WasApplied && dialog.SnapshotIsStale && dialog.RequiresParentRefresh && !Find<Button>(dialog,"ApplyButton").IsEnabled,"Confirmed enum update invalidates observation and requests parent refresh");
                check((await new EnumCatalog(limited).InspectAsync(schema,"mood")).Labels.Last().Value=="novo","Enum WPF Apply changes the real PostgreSQL enum");
                await dialog.InspectAsync();
                Find<ComboBox>(dialog,"Operation").SelectedValue=EnumValueOperation.Rename;
                Find<ComboBox>(dialog,"Existing").SelectedValue="novo";
                Find<TextBox>(dialog,"NewValue").Clear();
                check(!Find<Button>(dialog,"ScriptButton").IsEnabled,"Blank new-value field is not silently interpreted as empty label");
                Find<CheckBox>(dialog,"EmptyValue").IsChecked=true;
                check(Find<TextBox>(dialog,"Preview").Text.Contains("TO E''") && !Find<TextBox>(dialog,"NewValue").IsEnabled,"Explicit empty label has unambiguous SQL and disables unused text");
                Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                Find<CheckBox>(dialog,"EmptyValue").IsChecked=false;Find<TextBox>(dialog,"NewValue").Text="renamed";
                check(Find<CheckBox>(dialog,"Reviewed").IsChecked!=true,"Changing enum proposal clears review");
                foreach(string mode in new[]{"button","escape","close"})
                {
                    await dialog.InspectAsync();Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                    await using var blocker=new NpgsqlConnection(limited);await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();
                    await using(var cmd=new NpgsqlCommand($"ALTER TYPE {qs}.mood ADD VALUE 'uncommitted'",blocker,tx))await cmd.ExecuteNonQueryAsync();
                    Task? pending=null;
                    try
                    {
                        pending=SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                        await SequenceApplyCancellationChecks.WaitForLock(connection,schema);
                        check(Find<Button>(dialog,"CancelButton").IsEnabled && !Find<FrameworkElement>(dialog,"Fields").IsEnabled,"Blocked enum operation offers cancellation and protects proposal: "+mode);
                        if(mode=="close")dialog.Close();else if(mode=="escape")dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});else Find<Button>(dialog,"CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await pending.WaitAsync(TimeSpan.FromSeconds(8));
                        check(dialog.IsVisible && dialog.SnapshotIsStale && Find<TextBlock>(dialog,"Feedback").Text.Contains("Result not confirmed"),"Enum cancellation keeps uncertainty feedback and requires inspection: "+mode);
                    }
                    finally{dialog.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending;}
                    check((await new EnumCatalog(limited).InspectAsync(schema,"mood")).Labels.Last().Value=="novo","Canceled enum rename preserves committed label: "+mode);
                }
                await dialog.InspectAsync();Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                check((await new EnumCatalog(limited).InspectAsync(schema,"mood")).Labels.Last().Value=="renamed","Explicit retry after enum cancellation succeeds");
            }
            finally{dialog.CancelCurrentOperation();dialog.Close();}
            var scripted=new EnumDialog(limited,schema,"mood",entry.Oid){ShowInTaskbar=false};Exception? scriptError=null;
            scripted.Loaded+=async(_,_)=>{try{await scripted.InspectAsync();Find<TextBox>(scripted,"NewValue").Text="script_only";Find<Button>(scripted,"ScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}catch(Exception ex){scriptError=ex;scripted.Close();}};
            scripted.ShowDialog();if(scriptError is not null)throw scriptError;
            check(scripted.ScriptSql?.Contains("ADD VALUE E'script_only'")==true && !(await new EnumCatalog(limited).InspectAsync(schema,"mood")).Labels.Any(x=>x.Value=="script_only"),"Enum Script returns SQL without executing it");
            await Scalar($"SET ROLE {qo};DROP TYPE {qs}.mood;CREATE TYPE {qs}.mood AS ENUM('new');RESET ROLE");
            var stale=new EnumDialog(limited,schema,"mood",entry.Oid){ShowInTaskbar=false};stale.Show();
            try{await stale.InspectAsync();check(stale.SnapshotIsStale && Find<TextBlock>(stale,"Feedback").Text.Contains("replaced") && !Find<Button>(stale,"ApplyButton").IsEnabled,"Enum dialog refuses original OID replaced after Explorer loaded");}
            finally{stale.Close();}
        }
        finally{await Scalar($"DROP SCHEMA {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE {qo}");}
    }
}
