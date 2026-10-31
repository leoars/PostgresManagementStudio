using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class GrantorAuthorityDialogChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="authority_"+suffix,owner="authority_owner_"+suffix,member="authority_member_"+suffix,recipient="authority_recipient_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner),qm=SqlIdentifier.Quote(member),qr=SqlIdentifier.Quote(recipient);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var command=new NpgsqlCommand(sql,admin);await command.ExecuteNonQueryAsync();}
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=member,Pooling=false}.ConnectionString;
        var dialog=new PrivilegeDialog(limited){ShowInTaskbar=false};
        T Find<T>(string name)where T:FrameworkElement => (T)dialog.FindName(name);
        async Task ChooseOwner(){await dialog.LoadGrantorsAsync();Find<ComboBox>("GrantorRoles").SelectedItem=Find<ComboBox>("GrantorRoles").Items.Cast<PrivilegeGrantorCandidate>().Single(x=>x.Name==owner);}
        try
        {
            await Sql($"CREATE ROLE {qo};CREATE ROLE {qm} LOGIN;CREATE ROLE {qr};CREATE SCHEMA {qs} AUTHORIZATION {qo};SET ROLE {qo};CREATE TABLE {qs}.items(value integer);RESET ROLE;GRANT {qo} TO {qm} WITH INHERIT TRUE,SET FALSE");
            Exception? failure=null;
            dialog.ContentRendered+=async(_,_)=>
            {
                try
                {
                    Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;Find<TextBox>("SchemaName").Text=schema;Find<TextBox>("ObjectName").Text="items";Find<TextBox>("RoleName").Text=recipient;
                    Find<Expander>("DelegationPanel").IsExpanded=true;await ChooseOwner();
                    string info=Find<TextBlock>("GrantorInfo").Text;
                    check(info.Contains("SET allowed: False") && info.Contains("Role available without SET: True"),"Grantor UI distinguishes inherited role availability from absent SET authorization");
                    check(info.Contains("Object owner: True") && info.Contains("Effective grant option: True") && info.Contains("not every membership path"),"Grantor UI explains owner/effective authority without claiming complete membership paths");
                    check(!Find<Button>("ApplyButton").IsEnabled && Find<Button>("ScriptButton").IsEnabled,"INHERIT-only owner choice permits nominal Script but blocks explicit-role Apply");
                    await dialog.ApplyAsync();check(!(await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"items",schema)).AclEntries.Any(x=>x.Grantee==recipient),"Disabled explicit-role Apply does not use inherited authority silently");
                    Find<ScrollViewer>("ContentScroller").ScrollToVerticalOffset(280);await CascadePrivilegeDialogChecks.Capture(dialog,"inherit-normal",760,760,check,"m791");
                    await Sql($"GRANT {qo} TO {qm} WITH INHERIT FALSE,SET TRUE");await ChooseOwner();
                    info=Find<TextBlock>("GrantorInfo").Text;
                    check(info.Contains("SET allowed: True") && info.Contains("Role available without SET: False") && Find<Button>("ApplyButton").IsEnabled,"Reloaded SET-only membership authorizes explicit choice without implying inherited access");
                    Find<ScrollViewer>("ContentScroller").ScrollToVerticalOffset(430);await CascadePrivilegeDialogChecks.Capture(dialog,"set-small",650,540,check,"m791");
                    await dialog.ApplyAsync();
                    var acl=await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table,"items",schema);
                    check(acl.AclEntries.Any(x=>x.Grantee==recipient && x.Grantor==owner),"SET-only operator grants with exact selected owner origin");
                    await Sql($"REVOKE {qo} FROM {qm}");Find<ComboBox>("Privilege").SelectedItem="UPDATE";
                    check(!Find<Button>("ApplyButton").IsEnabled && !Find<Button>("ScriptButton").IsEnabled,"Changing privilege after explicit grantor selection cannot silently fall back to session origin");
                    await ChooseOwner();
                    check(!Find<Button>("ApplyButton").IsEnabled && Find<TextBlock>("GrantorInfo").Text.Contains("SET allowed: False"),"Revoked membership is reflected by explicit reload and blocks Apply");
                }
                catch(Exception ex){failure=ex;}
                finally{dialog.Close();}
            };
            dialog.ShowDialog();if(failure is not null)throw failure;
        }
        finally{if(dialog.IsVisible)dialog.Close();await Sql($"RESET ROLE;DROP SCHEMA IF EXISTS {qs} CASCADE;DROP OWNED BY {qm},{qr},{qo};DROP ROLE {qm},{qr},{qo}");}
    }
}
