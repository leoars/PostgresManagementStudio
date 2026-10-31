using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class CascadeJournalChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="cascade_journal_"+suffix,owner="journal_owner_"+suffix,recipient="journal_recipient_"+suffix,child="journal_child_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(recipient),qc=SqlIdentifier.Quote(child);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var command=new NpgsqlCommand(sql,admin);await command.ExecuteNonQueryAsync();}
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr};CREATE ROLE {qc};CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TABLE {qs}.items(value integer);GRANT USAGE ON SCHEMA {qs} TO {qr};RESET ROLE");
            foreach(bool before in new[]{true,false})
            {
                await Sql($"SET ROLE {qo};GRANT SELECT ON {qs}.items TO {qr} WITH GRANT OPTION;RESET ROLE;SET ROLE {qr};GRANT SELECT ON {qs}.items TO {qc};RESET ROLE");
                await using var proxy=new CommitAckProxy(before){Armed=false};
                var settings=new NpgsqlConnectionStringBuilder(connection){Username=owner,Port=proxy.Port,Pooling=false,SslMode=SslMode.Disable,Timeout=10};
                var dialog=new PrivilegeDialog(settings.ConnectionString){ShowInTaskbar=false};
                T Find<T>(string name)where T:FrameworkElement => (T)dialog.FindName(name);
                try
                {
                    dialog.Show();Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
                    Find<TextBox>("SchemaName").Text=schema;Find<TextBox>("ObjectName").Text="items";Find<TextBox>("RoleName").Text=recipient;
                    Find<CheckBox>("Revoke").IsChecked=true;Find<CheckBox>("GrantOption").IsChecked=true;
                    await dialog.LoadGrantorsAsync();
                    var grantor=Find<ComboBox>("GrantorRoles").Items.Cast<PrivilegeGrantorCandidate>().Single(x=>x.Name==owner);
                    Find<ComboBox>("GrantorRoles").SelectedItem=grantor;Find<CheckBox>("CascadeRevoke").IsChecked=true;await dialog.ReviewRevokeAsync();
                    check(Find<Button>("ApplyButton").IsEnabled,"CASCADE ACK fault armed only after complete dependency review");
                    proxy.Armed=true;await SequenceConfirmationChecks.RespondAsync(dialog,dialog.ApplyAsync,true,check);
                    check(dialog.HasUnconfirmedCommit && !Find<Button>("ApplyButton").IsEnabled && !Find<Button>("ScriptButton").IsEnabled,"Lost CASCADE ACK blocks duplicate Apply and Script");
                    var store=new PrivilegeCommitStore(settings.ConnectionString);var saved=store.Read();
                    check(saved is not null && saved.Request.Cascade && saved.Request.Revoke && saved.Request.GrantOption && saved.Request.Grantor?.Oid==grantor.Oid && saved.Request.Grantor.Name==owner,"Protected journal preserves exact CASCADE intent and grantor identity");
                    int commits=proxy.FrontendCommits;
                    dialog.Close();dialog=new PrivilegeDialog(settings.ConnectionString){ShowInTaskbar=false};dialog.Show();
                    Find<Expander>("DelegationPanel").IsExpanded=true;
                    check(dialog.HasUnconfirmedCommit && !Find<FrameworkElement>("Fields").IsEnabled && Find<CheckBox>("CascadeRevoke").IsChecked==true,"Reopened CASCADE journal restores blocked fields and revoke mode");
                    check(Find<TextBlock>("GrantorInfo").Text.Contains(owner) && Find<TextBlock>("GrantorInfo").Text.Contains(grantor.Oid.ToString()) && Find<TextBox>("Preview").Text.Contains("CASCADE;") && Find<TextBox>("Preview").Text.Contains("SET LOCAL ROLE "+qo),"Reopened CASCADE displays original grantor OID and exact nominal SQL");
                    await dialog.ApplyAsync();check(proxy.FrontendCommits==commits,"Reopened unknown CASCADE cannot replay the commit");
                    if(!before)
                    {
                        Find<ScrollViewer>("ContentScroller").ScrollToVerticalOffset(280);
                        await CascadePrivilegeDialogChecks.Capture(dialog,"restored-normal",760,760,check,"m790");
                        Find<ScrollViewer>("ContentScroller").ScrollToVerticalOffset(430);
                        await CascadePrivilegeDialogChecks.Capture(dialog,"restored-small",650,540,check,"m790");
                    }
                    proxy.RefuseNewConnections=true;await dialog.InspectAsync();
                    check(dialog.HasUnconfirmedCommit && store.Read() is not null,"Failed CASCADE reconciliation preserves protected intent");
                    proxy.RefuseNewConnections=false;await dialog.InspectAsync();
                    check(!dialog.HasUnconfirmedCommit && !Find<Button>("ApplyButton").IsEnabled && store.Read() is null,"Confirmed CASCADE receipt clears journal but requires new grantor/dependency inspection");
                    check(Find<TextBlock>("Feedback").Text.Contains(before?"aborted":"committed") && proxy.FrontendCommits==commits,"CASCADE reconciliation reports original xid outcome without replay");
                    var acl=await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"items",schema);
                    check(acl.AclEntries.Any(x=>x.Grantee==recipient && x.GrantOption)==before && acl.AclEntries.Any(x=>x.Grantee==child)==before,"Durable CASCADE outcome matches root and dependent ACL after reappearance");
                }
                finally{dialog.Close();}
            }
        }
        finally{await Sql($"RESET ROLE;DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qc},{qr},{qo};DROP ROLE {qc},{qr},{qo}");}
    }
}
