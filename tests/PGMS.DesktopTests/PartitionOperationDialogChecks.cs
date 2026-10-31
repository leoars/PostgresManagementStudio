using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PartitionOperationDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="partition_ops_"+suffix,owner="partition_ops_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task<object?> Scalar(string sql){await using var c=new NpgsqlCommand(sql,admin);return await c.ExecuteScalarAsync();}
        await Scalar($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TABLE {qs}.parent(id integer) PARTITION BY RANGE(id);CREATE TABLE {qs}.list_parent(id text) PARTITION BY LIST(id);CREATE TABLE {qs}.hash_parent(id integer) PARTITION BY HASH(id);CREATE TABLE {qs}.existing(id integer);INSERT INTO {qs}.existing VALUES(25);RESET ROLE");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        var catalog=new PartitionCatalog(limited);
        try
        {
            var parent=(await catalog.InspectAsync(schema,"parent")).Parent;
            var w=new PartitionDialog(limited,schema,"parent",parent.Oid){ShowInTaskbar=false};w.Show();
            try
            {
                check(!Find<Button>(w,"ApplyButton").IsEnabled,"Partition operations require parent inspection");
                await w.InspectAsync();Find<TextBox>(w,"ChildName").Text="created";
                var row=Find<DataGrid>(w,"ValuesGrid").Items.Cast<PartitionInput>().Single();row.LowerText="0";row.UpperText="10";
                check(Find<TextBox>(w,"Preview").Text.Contains("FOR VALUES FROM (E'0') TO (E'10')") && Find<Button>(w,"ApplyButton").IsEnabled,"Partition values render literal bounds and enable reviewed creation");
                await CascadePrivilegeDialogChecks.Capture(w,"range-normal",900,800,check,"m801");
                Find<ScrollViewer>(w,"FormScroller").ScrollToBottom();await CascadePrivilegeDialogChecks.Capture(w,"range-minimum",760,650,check,"m801");
                await w.ApplyAsync();check(w.WasApplied && w.RequiresParentRefresh && !Find<Button>(w,"ApplyButton").IsEnabled,"Partition creation invalidates observation after success");
                await Scalar($"INSERT INTO {qs}.parent VALUES(4)");check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {qs}.created"))==1,"WPF-created RANGE receives actual parent inserts");
                await w.InspectAsync();Find<ComboBox>(w,"Operation").SelectedIndex=1;Find<TextBox>(w,"ChildName").Text="existing";
                row=Find<DataGrid>(w,"ValuesGrid").Items.Cast<PartitionInput>().Single();row.LowerText="20";row.UpperText="30";
                check(!Find<Button>(w,"ApplyButton").IsEnabled,"ATTACH requires explicit child inspection");
                await w.InspectCandidateAsync();check(Find<Button>(w,"ApplyButton").IsEnabled,"Inspected existing child enables ATTACH");
                Find<TextBox>(w,"ChildName").Text="other";check(!Find<Button>(w,"ApplyButton").IsEnabled,"Changing child name invalidates candidate identity");
                Find<TextBox>(w,"ChildName").Text="existing";await w.InspectCandidateAsync();await w.ApplyAsync();
                check(Convert.ToInt64(await Scalar($"SELECT count(*) FROM {qs}.parent"))==2,"WPF ATTACH exposes existing rows through parent");
                await w.InspectAsync();Find<ComboBox>(w,"Operation").SelectedIndex=2;
                Find<ComboBox>(w,"Children").SelectedItem=Find<ComboBox>(w,"Children").Items.Cast<PartitionChild>().Single(c=>c.Name=="existing");
                check(Find<TextBox>(w,"ChildObservation").Text.Contains("FROM (20) TO (30)") && Find<TextBlock>(w,"Help").Text.Contains("keeps"),"DETACH shows inspected bound and preservation semantics");
                await CascadePrivilegeDialogChecks.Capture(w,"detach-minimum",760,650,check,"m801");
                await SequenceConfirmationChecks.RespondAsync(w,w.ApplyAsync,false,check);
                check(Find<Button>(w,"ApplyButton").IsEnabled && (await catalog.InspectAsync(schema,"parent")).Children.Count==2,"Native No retains link and reviewed detach proposal");
                await SequenceConfirmationChecks.RespondAsync(w,w.ApplyAsync,true,check);
                check((await catalog.InspectAsync(schema,"parent")).Children.Count==1 && Convert.ToInt64(await Scalar($"SELECT count(*) FROM {qs}.existing"))==1,"Native Yes detaches but preserves table data");
                await w.InspectAsync();Find<ComboBox>(w,"Operation").SelectedIndex=0;Find<TextBox>(w,"ChildName").Text="cancelled";
                row=Find<DataGrid>(w,"ValuesGrid").Items.Cast<PartitionInput>().Single();row.LowerText="40";row.UpperText="50";
                await using var blocker=new NpgsqlConnection(connection);await blocker.OpenAsync();await using var tx=await blocker.BeginTransactionAsync();
                await using(var cmd=new NpgsqlCommand($"LOCK TABLE ONLY {qs}.parent IN ACCESS EXCLUSIVE MODE",blocker,tx))await cmd.ExecuteNonQueryAsync();
                Task? pending=null;
                try
                {
                    pending=w.ApplyAsync();await SequenceApplyCancellationChecks.WaitForLock(connection,schema);
                    check(!Find<FrameworkElement>(w,"Fields").IsEnabled && !Find<Button>(w,"ApplyButton").IsEnabled && Find<Button>(w,"CancelButton").IsVisible,"Blocked partition write freezes fields and exposes cancellation");
                    await w.ApplyAsync();await w.InspectAsync();w.CancelCurrentOperation();await pending.WaitAsync(TimeSpan.FromSeconds(8));
                    check(!Find<Button>(w,"ApplyButton").IsEnabled && Find<TextBlock>(w,"Feedback").Text.Contains("Result not confirmed"),"Canceled partition change requires reinspection and reports uncertainty");
                }
                finally{w.CancelCurrentOperation();await tx.RollbackAsync();if(pending is not null)await pending;}
                check((bool)(await Scalar($"SELECT to_regclass('{qs}.cancelled') IS NULL"))!,"Canceled blocked partition creation leaves no child");
            }
            finally{w.Close();}
            foreach(var item in new[]{("list_parent",PartitionStrategy.List),("hash_parent",PartitionStrategy.Hash)})
            {
                var p=(await catalog.InspectAsync(schema,item.Item1)).Parent;var dialog=new PartitionDialog(limited,schema,item.Item1,p.Oid){ShowInTaskbar=false};dialog.Show();
                try
                {
                    await dialog.InspectAsync();Find<TextBox>(dialog,"ChildName").Text=item.Item1+"_child";
                    if(item.Item2==PartitionStrategy.List){var row=Find<DataGrid>(dialog,"ValuesGrid").Items.Cast<PartitionInput>().Single();row.LowerText="ação'";}
                    else{Find<TextBox>(dialog,"Modulus").Text="1";Find<TextBox>(dialog,"Remainder").Text="0";}
                    await dialog.ApplyAsync();check(dialog.WasApplied,"WPF creates child for strategy "+item.Item2);
                }
                finally{dialog.Close();}
            }
            var stale=new PartitionDialog(limited,schema,"parent",parent.Oid+1){ShowInTaskbar=false};stale.Show();
            try{await stale.InspectAsync();check(!Find<Button>(stale,"ApplyButton").IsEnabled && Find<TextBlock>(stale,"Feedback").Text.Contains("replaced"),"Explorer identity mismatch refuses new parent binding");}finally{stale.Close();}
            check(new CatalogEntry(CatalogKind.Relation,"p"){RelationType="p"}.CanManagePartitions && !new CatalogEntry(CatalogKind.Relation,"r"){RelationType="r"}.CanManagePartitions,"Explorer partition command is limited to partitioned parents");
        }
        finally{await Scalar($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE {qo}");}
    }
}
