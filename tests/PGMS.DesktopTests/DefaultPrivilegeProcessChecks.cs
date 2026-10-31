using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class DefaultPrivilegeProcessChecks
{
    private sealed class PauseBeforeCommit(DefaultPrivilegeCommitStore store,string state):IPrivilegeCommitJournal
    {
        public async Task RecordAsync(PrivilegeCommitReceipt receipt,CancellationToken token)
        {
            await store.RecordAsync(receipt,token);
            File.WriteAllText(Path.Combine(state,"ready"),Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(Timeout.Infinite,token);
        }
    }
    // Test executable only; no child command-line hook is distributed in PGMS.
    internal static async Task RunChildAsync(string state,string connection,string schema,string role,string phase)
    {
        state=Path.GetFullPath(state);
        if(!state.StartsWith(Path.GetFullPath("artifacts/default-permission-process")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Child state is outside the test workspace.");
        var b=new NpgsqlConnectionStringBuilder(connection);
        if(b.Host!="127.0.0.1" || !b.Username!.StartsWith("process_default_owner_",StringComparison.Ordinal) || b.SslMode!=SslMode.Disable || !string.IsNullOrEmpty(b.Password) || !schema.StartsWith("process_commit_",StringComparison.Ordinal) || !role.StartsWith("process_commit_role_",StringComparison.Ordinal) || phase is not("before" or "after"))
            throw new ArgumentException("Child requires the synthetic permission fixture.");
        Directory.CreateDirectory(state);Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",state);
        var request=new DefaultPrivilegeRequest(DefaultPrivilegeTarget.Tables,b.Username!,role,"SELECT",schema);
        var snapshot=await new DefaultPrivilegeCatalog(connection).InspectAsync(request.Target,request.Creator,schema);
        var recipient=(await new PrivilegeService(connection).InspectRecipientAsync(role))!;
        var store=new DefaultPrivilegeCommitStore(connection,request,snapshot.Identity);using var lease=store.AcquireLease();
        IPrivilegeCommitJournal journal=phase=="before"?new PauseBeforeCommit(store,state):store;
        try{await new DefaultPrivilegeService(connection,journal).ApplyInspectedAsync(request,snapshot,recipient);throw new Exception("Expected interrupted acknowledgement.");}
        catch(PrivilegeCommitUncertainException)when(phase=="after")
        {
            File.WriteAllText(Path.Combine(state,"ready"),Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(Timeout.Infinite);
        }
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="process_commit_"+suffix,owner="process_default_owner_"+suffix,role="process_commit_role_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task<bool> Has(){var snapshot=await new DefaultPrivilegeCatalog(connection).InspectAsync(DefaultPrivilegeTarget.Tables,owner,schema);return snapshot.Entries.Any(e=>e.Recipient==role && e.Privilege=="SELECT");}
        string? previous=Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            foreach(string phase in new[]{"before","after"})
            {
                string state=Path.GetFullPath(Path.Combine("artifacts/default-permission-process",suffix,phase));Directory.CreateDirectory(state);
                await using var proxy=new CommitAckProxy(false);
                var settings=new NpgsqlConnectionStringBuilder(connection){Username=owner,Port=proxy.Port,SslMode=SslMode.Disable,Pooling=false};
                var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Environment.CurrentDirectory,RedirectStandardOutput=true,RedirectStandardError=true};
                if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(DefaultPrivilegeProcessChecks).Assembly.Location);
                foreach(string arg in new[]{"--default-commit-child",state,settings.ConnectionString,schema,role,phase})start.ArgumentList.Add(arg);
                using var child=Process.Start(start)??throw new Exception("Permission child failed to start.");
                Task<string> output=child.StandardOutput.ReadToEndAsync(),errors=child.StandardError.ReadToEndAsync();
                try
                {
                    string ready=Path.Combine(state,"ready");var deadline=DateTime.UtcNow.AddSeconds(20);
                    while(!File.Exists(ready) && !child.HasExited && DateTime.UtcNow<deadline)await Task.Delay(25);
                    check(File.Exists(ready) && File.ReadAllText(ready)==child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) && !child.HasExited,"Owned child reached durable checkpoint before forced termination: "+phase);
                    Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",state);
                    var store=new DefaultPrivilegeCommitStore(settings.ConnectionString);
                    check(store.Read() is not null,"Pending intent is readable while the original process still holds its lease: "+phase);
                    var pending=store.Read()!;check(pending.Identity.Creator==owner && pending.Identity.Schema==schema && pending.Request.Role==role,"Durable default intent preserves creator/schema/recipient: "+phase);
                    bool busy=false;try{using var blocked=store.AcquireLease();}catch(IOException){busy=true;}
                    check(busy,"Live process lease excludes a different process instance: "+phase);
                    check(proxy.FaultInjected==(phase=="after"),"Wire evidence distinguishes pre-COMMIT pause from committed ACK loss: "+phase);
                    child.Kill(true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));await output;await errors;
                    using(var lease=store.AcquireLease())
                    {
                        string path=Path.Combine(store.DirectoryPath,"pending.commit");byte[] original=File.ReadAllBytes(path);
                        var ordinaryRequest=new PrivilegeRequest(PrivilegeTarget.Table,"fixture",role,"SELECT",Schema:schema);
                        var ordinary=new PrivilegeCommitStore(settings.ConnectionString,ordinaryRequest);
                        using var ordinaryLease=ordinary.AcquireLease();
                        var receipt=new DefaultPrivilegeService(settings.ConnectionString).ResumeCommitCheck(pending.TransactionId,pending.DatabaseOid);
                        await ordinary.RecordAsync(receipt,CancellationToken.None);
                        check(new PrivilegeCommitStore(settings.ConnectionString).Read()?.Request==ordinaryRequest,"Existing ordinary v1 journal remains independently readable: "+phase);
                        File.WriteAllBytes(path,File.ReadAllBytes(Path.Combine(ordinary.DirectoryPath,"pending.commit")));
                        bool refused=false;try{store.Read();}catch(System.Security.Cryptography.CryptographicException){refused=true;}
                        finally{File.WriteAllBytes(path,original);}
                        check(refused && store.Read()?.Identity==pending.Identity,"Ordinary v1 cannot be imported as default intent; original default v1 survives: "+phase);
                        ordinary.Remove(receipt);
                    }
                    var dialog=new DefaultPrivilegeDialog(settings.ConnectionString){ShowInTaskbar=false,Opacity=0};
                    try
                    {
                        dialog.Show();
                        check(dialog.HasUnconfirmedCommit && !((Button)dialog.FindName("ApplyButton")).IsEnabled,"New WPF instance restores and blocks interrupted process intent: "+phase);
                        for(int i=0;i<50 && dialog.HasUnconfirmedCommit;i++){await dialog.InspectAsync();if(dialog.HasUnconfirmedCommit)await Task.Delay(20);}
                        check(!dialog.HasUnconfirmedCommit && ((TextBlock)dialog.FindName("Feedback")).Text.Contains(phase=="before"?"aborted":"committed",StringComparison.Ordinal),"New WPF instance confirms exact interrupted transaction outcome: "+phase);
                        check(await Has()==(phase=="after"),"Process recovery never replays a pre-COMMIT grant or undoes a committed grant: "+phase);
                        check(!((Button)dialog.FindName("ApplyButton")).IsEnabled,"Default recovery requires fresh inspection before another Apply: "+phase);
                        await Sql($"SET ROLE {qo};CREATE TABLE {qs}.{SqlIdentifier.Quote("future_"+phase)}(value integer);RESET ROLE");
                        await using(var command=new NpgsqlCommand("SELECT has_table_privilege(@role,@table,'SELECT')",admin)){command.Parameters.AddWithValue("role",role);command.Parameters.AddWithValue("table",ObjectSql.Relation(schema,"future_"+phase));check((bool)(await command.ExecuteScalarAsync())! == (phase=="after"),"Recovered default outcome governs newly created objects: "+phase);}
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
        finally{Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY",previous);await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qo};DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo}");}
    }
}
