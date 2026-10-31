using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PrivilegeCommitProcessChecks
{
    private sealed class PauseBeforeCommit(PrivilegeCommitStore store,string state):IPrivilegeCommitJournal
    {
        public async Task RecordAsync(PrivilegeCommitReceipt receipt,CancellationToken token)
        {
            await store.RecordAsync(receipt,token);
            File.WriteAllText(Path.Combine(state,"ready"),Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(Timeout.Infinite,token);
        }
    }
    // Test executable only; no child command-line hook is distributed in PGMS.
    internal static async Task RunChildAsync(string state,string connection,string schema,string role,string phase,bool routine=false)
    {
        state=Path.GetFullPath(state);
        if(!state.StartsWith(Path.GetFullPath("artifacts/permission-process")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Child state is outside the test workspace.");
        var b=new NpgsqlConnectionStringBuilder(connection);
        if(b.Host!="127.0.0.1" || b.Username!="pgms_test" || b.SslMode!=SslMode.Disable || !string.IsNullOrEmpty(b.Password) || !schema.StartsWith("process_commit_",StringComparison.Ordinal) || !role.StartsWith("process_commit_role_",StringComparison.Ordinal) || phase is not("before" or "after"))
            throw new ArgumentException("Child requires the synthetic permission fixture.");
        Directory.CreateDirectory(state);Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",state);
        var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",role,"SELECT",Schema:schema);
        if(routine)
        {
            await using var lookup=new NpgsqlConnection(connection);await lookup.OpenAsync();
            await using var command=new NpgsqlCommand("SELECT p.oid FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.proname='target'",lookup);
            command.Parameters.AddWithValue("schema",schema);
            uint oid=(uint)(await command.ExecuteScalarAsync())!;
            var selection=await new RoutinePrivilegeCatalog(connection).InspectAsync(oid);
            request=new PrivilegeRequest(PrivilegeTarget.Routine,"target",role,"EXECUTE",Schema:schema){Routine=selection};
        }
        var store=new PrivilegeCommitStore(connection,request);using var lease=store.AcquireLease();
        IPrivilegeCommitJournal journal=phase=="before"?new PauseBeforeCommit(store,state):store;
        try{await new PrivilegeService(connection,journal).ApplyCurrentAsync(request);throw new Exception("Expected interrupted acknowledgement.");}
        catch(PrivilegeCommitUncertainException)when(phase=="after")
        {
            File.WriteAllText(Path.Combine(state,"ready"),Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(Timeout.Infinite);
        }
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check,bool routine=false)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="process_commit_"+suffix,role="process_commit_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task<bool> Has(){await using var cmd=new NpgsqlCommand(routine?"SELECT pg_catalog.has_function_privilege(@role,@table,'EXECUTE')":"SELECT pg_catalog.has_table_privilege(@role,@table,'SELECT')",admin);cmd.Parameters.AddWithValue("role",role);cmd.Parameters.AddWithValue("table",routine?table+"(integer)":table);return (bool)(await cmd.ExecuteScalarAsync())!;}
        string? previous=Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        try
        {
            await Sql($"CREATE ROLE {qr};CREATE SCHEMA {qs}");
            await Sql(routine?$"CREATE FUNCTION {table}(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1';REVOKE EXECUTE ON FUNCTION {table}(integer) FROM PUBLIC":$"CREATE TABLE {table}(id int)");
            RoutinePrivilegeSelection? original=null;
            if(routine)
            {
                await using var command=new NpgsqlCommand("SELECT p.oid FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.proname='target'",admin);
                command.Parameters.AddWithValue("schema",schema);
                original=await new RoutinePrivilegeCatalog(connection).InspectAsync((uint)(await command.ExecuteScalarAsync())!);
            }
            foreach(string phase in new[]{"before","after"})
            {
                string state=Path.GetFullPath(Path.Combine("artifacts/permission-process",suffix,phase));Directory.CreateDirectory(state);
                await using var proxy=new CommitAckProxy(false);
                var settings=new NpgsqlConnectionStringBuilder(connection){Port=proxy.Port,SslMode=SslMode.Disable,Pooling=false};
                var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Environment.CurrentDirectory,RedirectStandardOutput=true,RedirectStandardError=true};
                if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(PrivilegeCommitProcessChecks).Assembly.Location);
                foreach(string arg in new[]{routine?"--routine-commit-child":"--permission-commit-child",state,settings.ConnectionString,schema,role,phase})start.ArgumentList.Add(arg);
                using var child=Process.Start(start)??throw new Exception("Permission child failed to start.");
                Task<string> output=child.StandardOutput.ReadToEndAsync(),errors=child.StandardError.ReadToEndAsync();
                try
                {
                    string ready=Path.Combine(state,"ready");var deadline=DateTime.UtcNow.AddSeconds(20);
                    while(!File.Exists(ready) && !child.HasExited && DateTime.UtcNow<deadline)await Task.Delay(25);
                    check(File.Exists(ready) && File.ReadAllText(ready)==child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) && !child.HasExited,"Owned child reached durable checkpoint before forced termination: "+phase);
                    Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",state);
                    var store=new PrivilegeCommitStore(settings.ConnectionString);
                    check(store.Read() is not null,"Pending intent is readable while the original process still holds its lease: "+phase);
                    if(routine)check(store.Read()?.Request.Routine==original,"Durable process intent preserves the exact routine and argument identities: "+phase);
                    bool busy=false;try{using var blocked=store.AcquireLease();}catch(IOException){busy=true;}
                    check(busy,"Live process lease excludes a different process instance: "+phase);
                    check(proxy.FaultInjected==(phase=="after"),"Wire evidence distinguishes pre-COMMIT pause from committed ACK loss: "+phase);
                    child.Kill(true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));await output;await errors;
                    var dialog=new PrivilegeDialog(settings.ConnectionString){ShowInTaskbar=false,Opacity=0};
                    try
                    {
                        dialog.Show();
                        check(dialog.HasUnconfirmedCommit && !((Button)dialog.FindName("ApplyButton")).IsEnabled,"New WPF instance restores and blocks interrupted process intent: "+phase);
                        for(int i=0;i<50 && dialog.HasUnconfirmedCommit;i++){await dialog.InspectAsync();if(dialog.HasUnconfirmedCommit)await Task.Delay(20);}
                        check(!dialog.HasUnconfirmedCommit && ((TextBlock)dialog.FindName("Feedback")).Text.Contains(phase=="before"?"aborted":"committed",StringComparison.Ordinal),"New WPF instance confirms exact interrupted transaction outcome: "+phase);
                        check(await Has()==(phase=="after"),"Process recovery never replays a pre-COMMIT grant or undoes a committed grant: "+phase);
                        if(routine)check(!((Button)dialog.FindName("ApplyButton")).IsEnabled,"Routine recovery requires fresh inspection before another Apply: "+phase);
                        check(store.Read() is null,"Process recovery removes only the confirmed intent: "+phase);
                    }
                    finally{dialog.Close();}
                }
                finally
                {
                    if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}
                    Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",previous);
                }
            }
        }
        finally{Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",previous);await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr}");}
    }
}
