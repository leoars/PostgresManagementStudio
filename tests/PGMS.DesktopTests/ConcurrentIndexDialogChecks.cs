using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class ConcurrentIndexDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="concurrent_ui_"+suffix,role="concurrent_owner_"+suffix,qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        await Sql($"CREATE ROLE {qr} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qr};SET ROLE {qr};CREATE TABLE {qs}.target(id integer,label text);INSERT INTO {qs}.target VALUES(1,'a'),(1,'b');RESET ROLE");
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=role,Pooling=false}.ConnectionString;
        try
        {
            var target=await new ExpressionIndexCatalog(limited).InspectAsync(schema,"target");
            var proposal=new ExpressionIndexEdit(target,"created",[new(new("label"))],true);
            var w=new ConcurrentIndexDialog(limited,proposal){ShowInTaskbar=false};w.Show();
            try
            {
                check(!Find<Button>(w,"ApplyButton").IsEnabled,"Concurrent UI requires explicit SQL and nominal-resolution review");
                Find<CheckBox>(w,"Review").IsChecked=true;
                check(Find<Button>(w,"ApplyButton").IsEnabled && Find<TextBox>(w,"Preview").Text.Contains("INDEX CONCURRENTLY"),"Reviewed concurrent UI enables standalone SQL proposal");
                await CascadePrivilegeDialogChecks.Capture(w,"concurrent-normal",800,620,check,"m816");
                await CascadePrivilegeDialogChecks.Capture(w,"concurrent-minimum",700,520,check,"m816");
                await w.ApplyAsync();
                check(w.WasApplied && w.RequiresParentRefresh && !Find<Button>(w,"ApplyButton").IsEnabled,"Concurrent UI creates once and requires fresh proposal");
                check(new ConcurrentIndexStore(limited).Read() is null,"Acknowledged and verified UI success clears matching receipt");
            }
            finally{w.Close();}
            var blockedProposal=new ExpressionIndexEdit(target,"canceled",[new(new("id"))]);
            var blocked=new ConcurrentIndexDialog(limited,blockedProposal){ShowInTaskbar=false};blocked.Show();
            try
            {
                Find<CheckBox>(blocked,"Review").IsChecked=true;
                await using var guard=new NpgsqlConnection(connection);await guard.OpenAsync();await using var tx=await guard.BeginTransactionAsync();
                await using(var cmd=new NpgsqlCommand($"LOCK TABLE {qs}.target IN ROW EXCLUSIVE MODE",guard,tx))await cmd.ExecuteNonQueryAsync();
                var operation=blocked.ApplyAsync();bool seen=false;
                try
                {
                    for(int i=0;i<100&&!operation.IsCompleted;i++)
                    {
                        await using var cmd=new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='{schema}' AND c.relname='canceled')",admin);
                        seen=(bool)(await cmd.ExecuteScalarAsync())!;if(seen)break;await Task.Delay(25);
                    }
                    check(seen && Find<Button>(blocked,"CancelButton").IsEnabled && !Find<CheckBox>(blocked,"Review").IsEnabled,"Concurrent UI exposes cancellation while real phased build waits for writers");
                    var receipt=new ConcurrentIndexStore(limited).Read()!;
                    check(await new ConcurrentIndexService(limited,new ConcurrentIndexStore(limited)).IsWorkerActiveAsync(receipt)==true,"Durable PID and backend start identify the live command session");
                    blocked.CancelCurrentOperation();await operation.WaitAsync(TimeSpan.FromSeconds(8));
                    check(!blocked.WasApplied && Find<Button>(blocked,"InspectButton").IsEnabled,"Canceled UI retains pending operation for inspection");
                }
                finally{blocked.CancelCurrentOperation();await tx.RollbackAsync();await operation;}
            }
            finally{blocked.Close();}
            // Pending Index Operation… of a table shows the pending operation only when it is on that table.
            await Sql($"SET ROLE {qr};CREATE TABLE {qs}.other(id integer);RESET ROLE");
            async Task<uint> Oid(string name){await using var cmd=new NpgsqlCommand($"SELECT '{qs}.{name}'::regclass::oid",admin);return (uint)(await cmd.ExecuteScalarAsync())!;}
            var onOther=new ConcurrentIndexDialog(limited,new CatalogEntry(CatalogKind.Relation,"other",null,schema,await Oid("other"))){ShowInTaskbar=false};onOther.Show();
            try
            {
                string feedback=Find<TextBlock>(onOther,"Feedback").Text;
                check(feedback.StartsWith("No pending operation on ",StringComparison.Ordinal) && feedback.Contains(".\"target\" / canceled",StringComparison.Ordinal) && !Find<TextBox>(onOther,"Preview").Text.Contains("canceled",StringComparison.Ordinal) && !Find<Button>(onOther,"InspectButton").IsEnabled,
                    "From another table, the pending operation is not shown: the window names the table it is on: "+feedback);
            }
            finally{onOther.Close();}
            var onTarget=new ConcurrentIndexDialog(limited,new CatalogEntry(CatalogKind.Relation,"target",null,schema,await Oid("target"))){ShowInTaskbar=false};onTarget.Show();
            try{check(Find<TextBox>(onTarget,"Preview").Text.Contains("canceled",StringComparison.Ordinal) && Find<Button>(onTarget,"InspectButton").IsEnabled,"From its own table, the pending operation is shown and can be inspected");}
            finally{onTarget.Close();}
            var reopened=new ConcurrentIndexDialog(limited){ShowInTaskbar=false};reopened.Show();
            try
            {
                check(!Find<Button>(reopened,"ApplyButton").IsEnabled && Find<TextBox>(reopened,"Preview").Text.Contains("canceled"),"Reopened concurrent UI restores pending SQL and blocks replay");
                await reopened.InspectPendingAsync();
                check(Find<TextBlock>(reopened,"Feedback").Text.Contains("Invalid") && new ConcurrentIndexStore(limited).Read() is not null,"Reopened UI reads actual invalid residue and retains durable record");
                check(Find<Button>(reopened,"ArchiveButton").IsEnabled,"Disconnected worker and completed inspection enable receipt archival");
                await CascadePrivilegeDialogChecks.Capture(reopened,"concurrent-pending",700,520,check,"m817");
                await SequenceConfirmationChecks.RespondAsync(reopened,reopened.ArchiveAsync,false,check);
                check(new ConcurrentIndexStore(limited).Read() is not null,"Native No preserves pending receipt");
                await SequenceConfirmationChecks.RespondAsync(reopened,reopened.ArchiveAsync,true,check);
                check(new ConcurrentIndexStore(limited).Read() is null && !Find<Button>(reopened,"ApplyButton").IsEnabled,"Native Yes archives local receipt without replay or reuse of old proposal");
                check(System.IO.Directory.EnumerateFiles(new ConcurrentIndexStore(limited).DirectoryPath,"*.reviewed").Any(),"Archived receipt remains encrypted in operation history");
            }
            finally{reopened.Close();}
            foreach(var operation in new[]{IndexMaintenanceOperation.Reindex,IndexMaintenanceOperation.ReindexConcurrently,IndexMaintenanceOperation.DropConcurrently})
            {
                var snapshot=await new IndexMaintenanceCatalog(limited).InspectAsync(schema,"target","created");
                var maintenance=new ConcurrentIndexDialog(limited,maintenance:snapshot){ShowInTaskbar=false};maintenance.Show();
                try
                {
                    Find<ComboBox>(maintenance,"Operation").SelectedValue=operation;Find<CheckBox>(maintenance,"Review").IsChecked=true;
                    check(Find<Button>(maintenance,"ApplyButton").IsEnabled && Find<TextBox>(maintenance,"Preview").Text.Contains(operation==IndexMaintenanceOperation.DropConcurrently?"DROP INDEX":"REINDEX INDEX"),"Maintenance UI previews inspected "+operation);
                    if(operation==IndexMaintenanceOperation.ReindexConcurrently)
                    {
                        await CascadePrivilegeDialogChecks.Capture(maintenance,"maintenance-normal",800,620,check,"m821");
                        await CascadePrivilegeDialogChecks.Capture(maintenance,"maintenance-minimum",700,520,check,"m821");
                    }
                    if(operation==IndexMaintenanceOperation.DropConcurrently)
                    {
                        await SequenceConfirmationChecks.RespondAsync(maintenance,maintenance.ApplyAsync,false,check);
                        check(!maintenance.WasApplied && Find<Button>(maintenance,"ApplyButton").IsEnabled,"Native No preserves index and maintenance proposal");
                        await SequenceConfirmationChecks.RespondAsync(maintenance,maintenance.ApplyAsync,true,check);
                    }
                    else await maintenance.ApplyAsync();
                    check(maintenance.WasApplied && new ConcurrentIndexStore(limited).Read() is null,"WPF executes and verifies "+operation);
                }
                finally{maintenance.Close();}
            }
        }
        finally{await Sql($"DROP SCHEMA {qs} CASCADE;DROP ROLE {qr}");}
    }
}
