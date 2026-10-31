using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class CreateTypeDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="create_type_ui_"+suffix,owner="create_type_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo}");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        try
        {
            var dialog=new CreateTypeDialog(limited,schema){ShowInTaskbar=false};dialog.Show();
            try
            {
                Find<TextBox>(dialog,"TypeName").Text="mood";Find<TextBox>(dialog,"Labels").Text="ação\r\n漢字\r\n";
                check(!Find<Button>(dialog,"ApplyButton").IsEnabled,"New type requires destination inspection");
                await dialog.InspectAsync();
                check(!Find<Button>(dialog,"ScriptButton").IsEnabled && Find<TextBlock>(dialog,"Feedback").Text.Contains("Schema inspected"),"New enum refuses implicit empty trailing label");
                Find<CheckBox>(dialog,"AllowEmptyLabels").IsChecked=true;
                check(Find<TextBox>(dialog,"Preview").Text.Contains("E'ação', E'漢字', E''") && !Find<Button>(dialog,"ApplyButton").IsEnabled,"New enum explicitly preserves Unicode order and empty label before review");
                foreach(string id in new[]{"SchemaName","TypeName","Kind","Labels","DefaultValue","Preview"})check(!string.IsNullOrEmpty(UIElementAutomationPeer.CreatePeerForElement(Find<FrameworkElement>(dialog,id))?.GetName()),"Create type field has accessible name: "+id);
                Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await CascadePrivilegeDialogChecks.Capture(dialog,"create-enum-normal",840,760,check,"m835");
                await CascadePrivilegeDialogChecks.Capture(dialog,"create-enum-minimum",720,600,check,"m835");
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,false,check);
                check(!dialog.WasApplied && !dialog.RequiresParentRefresh,"New type native No sends no creation");
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                check(dialog.WasApplied && dialog.SnapshotIsStale && dialog.RequiresParentRefresh,"New enum confirmed creation requires fresh inspection");
                var mood=await new EnumCatalog(limited).InspectAsync(schema,"mood");
                check(mood.Labels.Select(x=>x.Value).SequenceEqual(new[]{"ação","漢字",""}),"WPF creates actual enum with reviewed ordered labels");
                await dialog.InspectAsync();Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                check(Find<TextBlock>(dialog,"Feedback").Text.Contains("42710") && dialog.SnapshotIsStale,"Duplicate type creation reports collision without replacing the object");
                await dialog.InspectAsync();Find<ComboBox>(dialog,"Kind").SelectedIndex=1;Find<TextBox>(dialog,"TypeName").Text="amount";
                check(!Find<Button>(dialog,"ApplyButton").IsEnabled,"Domain creation requires selected base type");
                var numeric=(await new DataTypeCatalog(limited).InspectAsync()).Single(x=>x.Oid==1700);
                dialog.SetBaseType(new(numeric,Precision:8,Scale:2));Find<CheckBox>(dialog,"UseDefault").IsChecked=true;Find<TextBox>(dialog,"DefaultValue").Text="4.25";Find<CheckBox>(dialog,"NotNull").IsChecked=true;
                check(Find<TextBox>(dialog,"Preview").Text.Contains("numeric\"(8, 2) DEFAULT E'4.25' NOT NULL"),"New domain previews selected modifier, literal default and NOT NULL");
                Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                await CascadePrivilegeDialogChecks.Capture(dialog,"create-domain-normal",840,760,check,"m835");
                await CascadePrivilegeDialogChecks.Capture(dialog,"create-domain-minimum",720,600,check,"m835");
                await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                await Scalar($"CREATE TABLE {qs}.rows(v {qs}.amount);INSERT INTO {qs}.rows DEFAULT VALUES");
                check(Convert.ToDecimal(await Scalar($"SELECT v FROM {qs}.rows"))==4.25m,"WPF-created domain default populates a real stored row");
                await dialog.InspectAsync();Find<CheckBox>(dialog,"Reviewed").IsChecked=true;Find<TextBox>(dialog,"SchemaName").Text="public";
                check(dialog.SnapshotIsStale && Find<CheckBox>(dialog,"Reviewed").IsChecked!=true && !Find<Button>(dialog,"ApplyButton").IsEnabled,"Changing destination schema invalidates inspection and review");
                Find<TextBox>(dialog,"SchemaName").Text=schema;Find<TextBox>(dialog,"TypeName").Text="canceled";
                foreach(string mode in new[]{"button","escape","close"})
                {
                    await dialog.InspectAsync();Find<CheckBox>(dialog,"Reviewed").IsChecked=true;
                    await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();
                    await using(var cmd=new NpgsqlCommand($"CREATE DOMAIN {qs}.canceled AS int",blocker,tx))await cmd.ExecuteNonQueryAsync();
                    Task? pending=null;
                    try
                    {
                        pending=SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                        await SequenceApplyCancellationChecks.WaitForLock(connection,schema);
                        check(Find<Button>(dialog,"CancelButton").IsEnabled && !Find<FrameworkElement>(dialog,"Fields").IsEnabled,"Blocked creation protects proposal and enables cancellation: "+mode);
                        if(mode=="close")dialog.Close();else if(mode=="escape")dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});else Find<Button>(dialog,"CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await pending.WaitAsync(TimeSpan.FromSeconds(8));
                        check(dialog.IsVisible && dialog.SnapshotIsStale && Find<TextBlock>(dialog,"Feedback").Text.Contains("Result not confirmed"),"Canceled type creation preserves dialog and uncertainty: "+mode);
                    }
                    finally{dialog.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending;}
                    check(await Scalar($"SELECT to_regtype('{qs}.canceled')::text") is DBNull,"Canceled creation leaves no domain: "+mode);
                }
            }
            finally{dialog.CancelCurrentOperation();dialog.Close();}
            var scripted=new CreateTypeDialog(limited,schema){ShowInTaskbar=false};Exception? error=null;
            scripted.Loaded+=async(_,_)=>{try{await scripted.InspectAsync();Find<TextBox>(scripted,"TypeName").Text="script_only";Find<TextBox>(scripted,"Labels").Text="one";Find<Button>(scripted,"ScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}catch(Exception ex){error=ex;scripted.Close();}};
            scripted.ShowDialog();if(error is not null)throw error;
            check(scripted.ScriptSql?.Contains("CREATE TYPE")==true && await Scalar($"SELECT to_regtype('{qs}.script_only')::text") is DBNull,"New type Script returns SQL without creating an object");
        }
        finally{await Scalar($"DROP SCHEMA {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE {qo}");}
    }
}
