using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class MaintainPrivilegeDialogChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="maintain_ui_"+suffix,role="maintain_ui_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        async Task<bool> Has(){await using var cmd=new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(@role,@table,'MAINTAIN')",admin);cmd.Parameters.AddWithValue("role",role);cmd.Parameters.AddWithValue("table",table);return (bool)(await cmd.ExecuteScalarAsync())!;}
        var dialog=new PrivilegeDialog(connection){ShowInTaskbar=false,Opacity=0};
        T Find<T>(string name)where T:FrameworkElement=>(T)dialog.FindName(name);
        try
        {
            await Sql($"CREATE ROLE {qr};CREATE SCHEMA {qs};CREATE TABLE {table}(id int)");
            dialog.Show();Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;
            Find<TextBox>("SchemaName").Text=schema;Find<TextBox>("ObjectName").Text="target";Find<TextBox>("RoleName").Text=role;
            Find<ComboBox>("Privilege").SelectedItem="MAINTAIN";
            check(Find<TextBox>("Preview").Text==$"GRANT MAINTAIN ON TABLE {table} TO {qr};","WPF MAINTAIN choice produces exact reviewable SQL");
            await dialog.InspectAsync();await dialog.ApplyAsync();check(await Has(),"WPF MAINTAIN Apply grants real catalog authority");
            await dialog.InspectAsync();check(dialog.AccessSnapshot!.AclEntries.Any(e=>e.Grantee==role && e.Privilege=="MAINTAIN"),"WPF inspection shows the stored MAINTAIN recipient ACL");
            Find<CheckBox>("Revoke").IsChecked=true;await dialog.ApplyAsync();check(!await Has(),"WPF restrictive MAINTAIN revoke removes its direct grant");
            Find<CheckBox>("ColumnScope").IsChecked=true;
            check(!Find<ComboBox>("Privilege").Items.Cast<string>().Contains("MAINTAIN"),"Column scope never offers table-only MAINTAIN");
            Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Sequence;
            check(!Find<ComboBox>("Privilege").Items.Cast<string>().Contains("MAINTAIN"),"Sequence scope never offers MAINTAIN");
        }
        finally{dialog.Close();await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qr}");}
    }
}
