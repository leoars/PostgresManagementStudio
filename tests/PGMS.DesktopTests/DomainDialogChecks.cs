using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class DomainDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="domain_ui_"+suffix,owner="domain_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE DOMAIN {qs}.amount AS numeric(8,2) DEFAULT 4.25;CREATE TABLE {qs}.rows(v {qs}.amount);INSERT INTO {qs}.rows VALUES(-1);RESET ROLE");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        try
        {
            var catalog=new CatalogService(limited);
            var entry=(await catalog.ChildrenAsync(new(CatalogKind.Domains,"Domains",catalog.DefaultDatabase,schema))).Single();
            check(entry.CanManageDomain && !entry.CanManageEnum,"Explorer domain leaf offers domain operations rather than enum editing");
            var dialog=new DomainDialog(limited,schema,"amount",entry.Oid){ShowInTaskbar=false};dialog.Show();
            try
            {
                check(!Find<Button>(dialog,"ApplyButton").IsEnabled,"Domain Apply requires inspection and review");
                await dialog.InspectAsync();Find<TextBox>(dialog,"ConstraintName").Text="positive";Find<ComboBox>(dialog,"Comparison").SelectedValue=IndexComparison.Greater;Find<TextBox>(dialog,"Literal").Text="0";
                check(dialog.Snapshot?.Type.Oid==entry.Oid && Find<TextBlock>(dialog,"Observation").Text.Contains("4.25"),"Domain observation preserves Explorer identity and shows actual default");
                check(Find<TextBox>(dialog,"Preview").Text.Contains("CHECK (VALUE > E'0') NOT VALID") && !Find<Button>(dialog,"ApplyButton").IsEnabled,"Domain preview exposes pending validation and waits for review");
                foreach(string id in new[]{"Operation","Existing","ConstraintName","Comparison","Literal","Constraints","Preview"})check(!string.IsNullOrEmpty(UIElementAutomationPeer.CreatePeerForElement(Find<FrameworkElement>(dialog,id))?.GetName()),"Domain field has accessible name: "+id);
                Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await CascadePrivilegeDialogChecks.Capture(dialog,"domain-normal",860,760,check,"m833");
                await CascadePrivilegeDialogChecks.Capture(dialog,"domain-minimum",720,600,check,"m833");
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,false,check);
                check(!dialog.WasApplied && !dialog.RequiresParentRefresh,"Domain native No leaves the constraint absent");
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                check(dialog.WasApplied && dialog.SnapshotIsStale && dialog.RequiresParentRefresh,"Domain confirmed ADD requires fresh inspection");
                await dialog.InspectAsync();
                check(dialog.Snapshot!.Constraints.Single().Validated==false && Find<TextBox>(dialog,"Constraints").Text.Contains("NOT VALID"),"Domain UI reports the actual unvalidated rule");
                Find<ComboBox>(dialog,"Operation").SelectedItem=DomainConstraintOperation.Validate;Find<ComboBox>(dialog,"Existing").SelectedValue="positive";Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                check(dialog.SnapshotIsStale && Find<TextBlock>(dialog,"Feedback").Text.Contains("23514"),"Domain Validate reports existing-row violation without claiming success");
                await Scalar($"UPDATE {qs}.rows SET v=1");
                foreach(string mode in new[]{"button","escape","close"})
                {
                    await dialog.InspectAsync();Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                    await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();
                    await using(var cmd=new NpgsqlCommand($"LOCK TABLE {qs}.rows IN ACCESS EXCLUSIVE MODE",blocker,tx))await cmd.ExecuteNonQueryAsync();
                    Task? pending=null;
                    try
                    {
                        pending=SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                        await SequenceApplyCancellationChecks.WaitForLock(connection,schema);
                        check(Find<Button>(dialog,"CancelButton").IsEnabled && !Find<FrameworkElement>(dialog,"Fields").IsEnabled,"Blocked domain validation protects review and offers cancellation: "+mode);
                        if(mode=="close")dialog.Close();else if(mode=="escape")dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});else Find<Button>(dialog,"CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await pending.WaitAsync(TimeSpan.FromSeconds(8));
                        check(dialog.IsVisible && dialog.SnapshotIsStale && Find<TextBlock>(dialog,"Feedback").Text.Contains("Result not confirmed"),"Domain cancellation keeps dialog and uncertainty feedback: "+mode);
                    }
                    finally{dialog.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending;}
                    check(!(await new DomainCatalog(limited).InspectAsync(schema,"amount")).Constraints.Single().Validated,"Canceled domain validation preserves pending state: "+mode);
                }
                await dialog.InspectAsync();Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                await dialog.InspectAsync();check(dialog.Snapshot!.Constraints.Single().Validated,"Domain validation succeeds after repair and explicit retry");
                Find<ComboBox>(dialog,"Operation").SelectedItem=DomainConstraintOperation.Drop;
                check(Find<CheckBox>(dialog,"Reviewed").IsChecked!=true && Find<TextBox>(dialog,"Preview").Text.EndsWith("RESTRICT;"),"Switching to DROP invalidates review and exposes RESTRICT");
                Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                check((await new DomainCatalog(limited).InspectAsync(schema,"amount")).Constraints.Count==0,"Confirmed domain DROP removes the actual constraint");
            }
            finally{dialog.CancelCurrentOperation();dialog.Close();}
            var scripted=new DomainDialog(limited,schema,"amount",entry.Oid){ShowInTaskbar=false};Exception? scriptError=null;
            scripted.Loaded+=async(_,_)=>{try{await scripted.InspectAsync();Find<TextBox>(scripted,"ConstraintName").Text="script_only";Find<ComboBox>(scripted,"Comparison").SelectedValue=IndexComparison.IsNotNull;Find<Button>(scripted,"ScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}catch(Exception ex){scriptError=ex;scripted.Close();}};
            scripted.ShowDialog();if(scriptError is not null)throw scriptError;
            check(scripted.ScriptSql?.Contains("VALUE IS NOT NULL")==true && (await new DomainCatalog(limited).InspectAsync(schema,"amount")).Constraints.Count==0,"Domain Script returns typed SQL without executing");
            await Scalar($"DROP TABLE {qs}.rows;SET ROLE {qo};DROP DOMAIN {qs}.amount;CREATE DOMAIN {qs}.amount AS int;RESET ROLE");
            var stale=new DomainDialog(limited,schema,"amount",entry.Oid){ShowInTaskbar=false};stale.Show();
            try{await stale.InspectAsync();check(stale.SnapshotIsStale && Find<TextBlock>(stale,"Feedback").Text.Contains("replaced"),"Domain dialog refuses replaced Explorer OID");}finally{stale.Close();}
        }
        finally{await Scalar($"DROP SCHEMA {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE {qo}");}
    }
}
