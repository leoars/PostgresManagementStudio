using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ExpressionIndexDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="expr_ui_"+suffix,owner="expr_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TABLE {qs}.target(id integer,name text,note text);INSERT INTO {qs}.target VALUES(1,'Alpha','first');RESET ROLE");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        var target=await new ExpressionIndexCatalog(limited).InspectAsync(schema,"target");
        try
        {
            var w=new ExpressionIndexDialog(limited,schema,"target",target.Oid){ShowInTaskbar=false};w.Show();
            try
            {
                check(!Find<Button>(w,"ApplyButton").IsEnabled,"Expression index starts with required inspection");await w.InspectAsync();
                Find<TextBox>(w,"IndexName").Text="lower_active";Find<ComboBox>(w,"Column").SelectedItem="name";Find<ComboBox>(w,"Transform").SelectedItem=IndexTransform.Lower;
                Find<ComboBox>(w,"Opclass").SelectedItem=Find<ComboBox>(w,"Opclass").Items.Cast<object>().Single(x=>x.ToString()!.Contains("text_pattern_ops"));
                Find<ComboBox>(w,"Sort").SelectedItem=IndexSortOrder.Descending;Find<ComboBox>(w,"NullOrder").SelectedItem=IndexNullPlacement.Last;
                Find<Button>(w,"AddKeyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Find<TextBox>(w,"Included").Text="note";Find<CheckBox>(w,"Unique").IsChecked=true;Find<CheckBox>(w,"UsePredicate").IsChecked=true;
                Find<ComboBox>(w,"PredicateColumn").SelectedItem="id";Find<ComboBox>(w,"Comparison").SelectedItem=IndexComparison.Greater;Find<TextBox>(w,"PredicateValue").Text="0";
                string sql=Find<TextBox>(w,"Preview").Text;
                check(sql.Contains("pg_catalog.lower(\"name\")") && sql.Contains("\"pg_catalog\".\"text_pattern_ops\" DESC NULLS LAST") && sql.Contains("INCLUDE (\"note\")") && sql.Contains("WHERE (\"id\" > E'0')"),"Expression UI previews transform, key order, INCLUDE and literal predicate");
                await CascadePrivilegeDialogChecks.Capture(w,"expression-normal",900,820,check,"m808");Find<ScrollViewer>(w,"FormScroller").ScrollToBottom();await CascadePrivilegeDialogChecks.Capture(w,"expression-minimum",780,670,check,"m808");
                await w.ApplyAsync();check(w.WasApplied && w.RequiresParentRefresh && !Find<Button>(w,"ApplyButton").IsEnabled,"Expression Create succeeds and requires reinspection");
                bool duplicate=false;try{await Scalar($"INSERT INTO {qs}.target VALUES(2,'ALPHA','duplicate')");}catch(PostgresException e)when(e.SqlState=="23505"){duplicate=true;}check(duplicate,"WPF expression index enforces actual transformed uniqueness");
                await Scalar($"INSERT INTO {qs}.target VALUES(-1,'ALPHA','outside predicate')");check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {qs}.target"))==2,"WPF partial predicate excludes nonmatching rows");
                await w.InspectAsync();Find<TextBox>(w,"IndexName").Text="blocked";
                await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();await using(var cmd=new NpgsqlCommand($"LOCK TABLE ONLY {qs}.target IN ACCESS EXCLUSIVE MODE",blocker,tx))await cmd.ExecuteNonQueryAsync();
                Task? pending=null;
                try{pending=w.ApplyAsync();await SequenceApplyCancellationChecks.WaitForLock(connection,schema);check(!Find<FrameworkElement>(w,"Fields").IsEnabled && Find<Button>(w,"CancelButton").IsEnabled,"Blocked index build exposes cancel and freezes fields");await w.ApplyAsync();w.CancelCurrentOperation();await pending.WaitAsync(TimeSpan.FromSeconds(8));check(!Find<Button>(w,"ApplyButton").IsEnabled && Find<TextBlock>(w,"Feedback").Text.Contains("Result not confirmed"),"Canceled index requires fresh inspection");}
                finally{w.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending;}
                check((bool)(await Scalar($"SELECT to_regclass('{qs}.blocked') IS NULL"))!,"Canceled blocked index leaves no object");
            }
            finally{w.Close();}
            var script=new ExpressionIndexDialog(limited,schema,"target",target.Oid){ShowInTaskbar=false};var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            script.Loaded+=async(_,_)=>{try{await script.InspectAsync();Find<TextBox>(script,"IndexName").Text="script_only";Find<ComboBox>(script,"Column").SelectedItem="name";Find<ComboBox>(script,"Transform").SelectedItem=IndexTransform.Upper;Find<Button>(script,"AddKeyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Find<Button>(script,"ScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));done.SetResult();}catch(Exception ex){done.SetException(ex);if(script.IsVisible)script.Close();}};
            script.ShowDialog();await done.Task;check(script.ScriptSql!.Contains("pg_catalog.upper") && !script.WasApplied && (bool)(await Scalar($"SELECT to_regclass('{qs}.script_only') IS NULL"))!,"Expression Script returns SQL without creating index");
            var mismatch=new ExpressionIndexDialog(limited,schema,"target",target.Oid+1){ShowInTaskbar=false};mismatch.Show();try{await mismatch.InspectAsync();check(!Find<Button>(mismatch,"ApplyButton").IsEnabled && Find<TextBlock>(mismatch,"Feedback").Text.Contains("replaced"),"Expression flow refuses changed Explorer selection identity");}finally{mismatch.Close();}
        }
        finally{await Scalar($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE {qo}");}
    }
}
