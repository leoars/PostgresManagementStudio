using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class PrivilegeIdentityDialogChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),schema="identity_ui_"+suffix,role="identity_ui_role_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qr=SqlIdentifier.Quote(role),table=ObjectSql.Relation(schema,"target");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        async Task<bool> Has(bool column=false){await using var c=new NpgsqlCommand(column?"SELECT pg_catalog.has_column_privilege(@role,@table,'value','UPDATE')":"SELECT pg_catalog.has_table_privilege(@role,@table,'SELECT')",admin);c.Parameters.AddWithValue("role",role);c.Parameters.AddWithValue("table",table);return (bool)(await c.ExecuteScalarAsync())!;}
        var dialog=new PrivilegeDialog(connection){ShowInTaskbar=false,Opacity=0};
        T Find<T>(string name)where T:FrameworkElement=>(T)dialog.FindName(name);
        try
        {
            await Sql($"CREATE ROLE {qr}; CREATE SCHEMA {qs}; CREATE TABLE {table}(id int,value text)");
            dialog.Show();Find<ComboBox>("ObjectType").SelectedItem=PrivilegeTarget.Table;Find<TextBox>("SchemaName").Text=schema;Find<TextBox>("ObjectName").Text="target";Find<TextBox>("RoleName").Text=role;
            await dialog.InspectAsync();uint original=dialog.AccessSnapshot!.Oid;
            check(dialog.AccessSnapshot.TargetIdentity?.ObjectOid==original,"Permissions UI retains identity from the effective-access inspection");
            await Sql($"ALTER TABLE {table} RENAME TO original; CREATE TABLE {table}(id int,value text)");
            await dialog.ApplyAsync();
            check(Find<TextBlock>("Feedback").Text.Contains("identity changed",StringComparison.Ordinal) && dialog.AccessSnapshotIsStale,"Permissions UI reports identity conflict and marks observation stale");
            check(!await Has(),"Permissions UI cannot grant to a same-name replacement after inspection");
            Find<TabControl>("PermissionTabs").SelectedIndex=0;
            foreach(bool small in new[]{false,true})
            {
                dialog.Width=small?650:760;dialog.Height=small?540:760;dialog.UpdateLayout();
                var element=(FrameworkElement)dialog.Content;
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
                var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var bounds=new Rect(0,0,element.ActualWidth,element.ActualHeight);dc.DrawRectangle(dialog.Background,null,bounds);dc.DrawRectangle(new VisualBrush(element),null,bounds);}bitmap.Render(drawing);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));System.IO.Directory.CreateDirectory("artifacts/review");using var file=System.IO.File.Create("artifacts/review/m719-identity-"+(small?"small":"normal")+".png");encoder.Save(file);
            }
            await dialog.InspectAsync();check(dialog.AccessSnapshot!.Oid!=original,"Permissions UI reinspection captures replacement identity");
            await dialog.ApplyAsync();check(await Has(),"Permissions UI can apply to the newly inspected object");
            Find<CheckBox>("ColumnScope").IsChecked=true;Find<TextBox>("ColumnName").Text="value";Find<ComboBox>("Privilege").SelectedItem="UPDATE";
            await dialog.InspectAsync();short? previous=dialog.AccessSnapshot!.TargetIdentity!.ColumnNumber;
            await Sql($"ALTER TABLE {table} DROP COLUMN value; ALTER TABLE {table} ADD COLUMN value text");
            await dialog.ApplyAsync();check(!await Has(true) && Find<TextBlock>("Feedback").Text.Contains("identity changed",StringComparison.Ordinal),"Permissions UI rejects recreated column before committing its new ACL");
            await dialog.InspectAsync();check(dialog.AccessSnapshot!.TargetIdentity!.ColumnNumber!=previous,"Permissions UI reinspection captures new column attnum");
            await dialog.ApplyAsync();check(await Has(true),"Permissions UI applies to the re-inspected column");
            await dialog.InspectAsync();
            uint recipientOid=dialog.AccessSnapshot!.RecipientIdentity!.Oid;
            await Sql($"ALTER ROLE {qr} RENAME TO {SqlIdentifier.Quote(role+"_old")}; CREATE ROLE {qr}");
            await dialog.ApplyAsync();
            check(Find<TextBlock>("Feedback").Text.Contains("recipient identity",StringComparison.Ordinal) && dialog.AccessSnapshotIsStale,"Permissions UI refuses same-name recipient replacement and marks snapshot stale");
            check(!await Has(true),"Permissions UI leaves replacement role without column authority");
            await dialog.InspectAsync();
            check(dialog.AccessSnapshot!.RecipientIdentity!.Oid!=recipientOid,"Permissions UI reinspection captures replacement recipient OID");
            await dialog.ApplyAsync();check(await Has(true),"Permissions UI applies to explicitly re-inspected recipient");
        }
        finally{dialog.Close();await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE; DROP ROLE IF EXISTS {qr}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(role+"_old")}");}
    }
}
