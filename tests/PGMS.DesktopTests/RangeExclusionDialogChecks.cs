using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class RangeExclusionDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="exclude_ui_"+suffix,owner="exclude_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TABLE {qs}.target(during int4range,note text);INSERT INTO {qs}.target VALUES('[1,5)','first');RESET ROLE");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        var target=await new ExpressionIndexCatalog(limited).InspectAsync(schema,"target");
        try
        {
            var w=new RangeExclusionDialog(limited,schema,"target",target.Oid){ShowInTaskbar=false};w.Show();
            try
            {
                check(!Find<Button>(w,"ApplyButton").IsEnabled,"Exclusion requires inspection before proposal");await w.InspectAsync();
                check(Find<ListBox>(w,"Columns").Items.Cast<string>().SequenceEqual(new[]{"during"}),"Exclusion UI offers only inspected built-in range columns");
                Find<TextBox>(w,"ConstraintName").Text="no_overlap";Find<ListBox>(w,"Columns").SelectedItem="during";
                Find<CheckBox>(w,"InitiallyDeferred").IsChecked=true;check(!Find<Button>(w,"ApplyButton").IsEnabled,"Deferred exclusion requires DEFERRABLE in UI");Find<CheckBox>(w,"Deferrable").IsChecked=true;
                check(Find<TextBox>(w,"Preview").Text.Contains("OPERATOR(pg_catalog.&&)") && Find<TextBox>(w,"Preview").Text.Contains("INITIALLY DEFERRED"),"Exclusion UI scripts exact qualified operator and timing");
                await CascadePrivilegeDialogChecks.Capture(w,"range-exclusion-normal",800,730,check,"m811");await CascadePrivilegeDialogChecks.Capture(w,"range-exclusion-minimum",700,610,check,"m811");
                await w.ApplyAsync();check(w.WasApplied && w.RequiresParentRefresh && !Find<Button>(w,"ApplyButton").IsEnabled,"WPF exclusion created and observation invalidated");
                bool overlap=false;try{await Scalar($"INSERT INTO {qs}.target VALUES('[3,6)','overlap')");}catch(PostgresException e)when(e.SqlState=="23P01"){overlap=true;}check(overlap,"WPF-created exclusion rejects real overlap");
                await w.InspectAsync();Find<TextBox>(w,"ConstraintName").Text="cancelled";Find<ListBox>(w,"Columns").SelectedItem="during";
                await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();await using(var cmd=new NpgsqlCommand($"LOCK TABLE ONLY {qs}.target IN ACCESS EXCLUSIVE MODE",blocker,tx))await cmd.ExecuteNonQueryAsync();
                Task? pending=null;
                try{pending=w.ApplyAsync();await SequenceApplyCancellationChecks.WaitForLock(connection,schema);check(!Find<FrameworkElement>(w,"Fields").IsEnabled && Find<Button>(w,"CancelButton").IsEnabled,"Blocked exclusion write freezes fields and permits cancel");await w.ApplyAsync();w.CancelCurrentOperation();await pending.WaitAsync(TimeSpan.FromSeconds(8));check(!Find<Button>(w,"ApplyButton").IsEnabled && Find<TextBlock>(w,"Feedback").Text.Contains("Result not confirmed"),"Canceled exclusion requires reinspection");}
                finally{w.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending;}
                check((bool)(await Scalar($"SELECT NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='{qs}.target'::regclass AND conname='cancelled')"))!,"Canceled blocked exclusion leaves no constraint");
            }
            finally{w.Close();}
            var script=new RangeExclusionDialog(limited,schema,"target",target.Oid){ShowInTaskbar=false};var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            script.Loaded+=async(_,_)=>{try{await script.InspectAsync();Find<TextBox>(script,"ConstraintName").Text="script_only";Find<ListBox>(script,"Columns").SelectedItem="during";Find<ComboBox>(script,"Operator").SelectedItem=RangeExclusionOperator.Equal;Find<Button>(script,"ScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));done.SetResult();}catch(Exception ex){done.SetException(ex);if(script.IsVisible)script.Close();}};
            script.ShowDialog();await done.Task;check(script.ScriptSql!.Contains("OPERATOR(pg_catalog.=)") && !script.WasApplied && (bool)(await Scalar($"SELECT NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='{qs}.target'::regclass AND conname='script_only')"))!,"Exclusion Script preserves exact equality SQL without applying");
        }
        finally{await Scalar($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE {qo}");}
    }
}
