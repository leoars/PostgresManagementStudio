using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class RestoreOwnershipDialogChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),owner="restore_ui_owner_"+suffix,reader="restore_ui_reader_"+suffix,source="restore_ui_source_"+suffix,target="restore_ui_target_"+suffix;
        string qo=SqlIdentifier.Quote(owner),qr=SqlIdentifier.Quote(reader);
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")??throw new Exception("Missing test tools.");
        string directory=Path.GetFullPath(Path.Combine("artifacts/restore-ui",suffix));Directory.CreateDirectory(directory);string archive=Path.Combine(directory,"source.backup");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        string As(string db,string? role=null)=>new NpgsqlConnectionStringBuilder(connection){Database=db,Username=role??"pgms_test",Pooling=false}.ConnectionString;
        bool sourceCreated=false,targetCreated=false;BackupDialog? dialog=null;
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE ROLE {qr} LOGIN");
            await Sql($"CREATE DATABASE {SqlIdentifier.Quote(source)} OWNER {qo}");sourceCreated=true;
            await Sql($"CREATE DATABASE {SqlIdentifier.Quote(target)}");targetCreated=true;
            await using(var db=new NpgsqlConnection(As(source,owner)))
            {await db.OpenAsync();await using var cmd=new NpgsqlCommand($"CREATE TABLE public.items(id int PRIMARY KEY,value text);INSERT INTO public.items VALUES(1,'UI ação');GRANT SELECT ON public.items TO {qr}",db);await cmd.ExecuteNonQueryAsync();}
            await new BackupService(As(source,owner),tools).BackupAsync(archive);
            dialog=new BackupDialog(true,As(target)){ShowInTaskbar=false,Opacity=0};dialog.Show();
            T Find<T>(string name)where T:FrameworkElement=>(T)dialog.FindName(name);
            check(Find<CheckBox>("RestoreOwner").IsChecked==false && Find<CheckBox>("RestoreAcl").IsChecked==false && Find<TextBox>("OptionsPreview").Text.Contains("--no-owner --no-privileges",StringComparison.Ordinal),"Restore UI defaults preserve ownership/ACL omission with reviewable flags");
            Find<CheckBox>("RestoreOwner").IsChecked=true;Find<CheckBox>("RestoreAcl").IsChecked=true;
            check(!Find<TextBox>("OptionsPreview").Text.Contains("--no-owner",StringComparison.Ordinal) && !Find<TextBox>("OptionsPreview").Text.Contains("--no-privileges",StringComparison.Ordinal) && Find<TextBox>("OptionsPreview").Text.Contains("--single-transaction --exit-on-error",StringComparison.Ordinal),"Restore UI ownership/ACL choices preserve transaction flags in preview");
            await using(var untouched=new NpgsqlConnection(As(target)))
            {await untouched.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT to_regclass('public.items') IS NULL",untouched);check((bool)(await cmd.ExecuteScalarAsync())!,"Changing restore options and preview performs no database writes");}
            foreach(string name in new[]{"RestoreOwner","RestoreAcl","OptionsPreview"})check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(name))),"Restore option has an accessible name: "+name);
            Find<TextBox>("ArchivePath").Text=archive;Find<TextBox>("ToolsPath").Text=tools;
            foreach(bool small in new[]{false,true})
            {
                dialog.Width=small?680:760;dialog.Height=small?510:550;
                foreach(bool bottom in new[]{false,true})
                {
                    var scroller=Find<ScrollViewer>("FormScroller");if(bottom)scroller.ScrollToBottom();else scroller.ScrollToTop();dialog.UpdateLayout();
                    var root=(FrameworkElement)dialog.Content;
                    var b=Find<Button>("RunButton");var point=b.TranslatePoint(new Point(),root);
                    check(point.Y>=0 && point.Y+b.ActualHeight<=root.ActualHeight,"Restore action stays in viewport: "+(small?"minimum":"normal")+"/"+(bottom?"bottom":"top"));
                    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var rect=new Rect(0,0,root.ActualWidth,root.ActualHeight);dc.DrawRectangle(dialog.Background,null,rect);dc.DrawRectangle(new VisualBrush(root),null,rect);}bitmap.Render(drawing);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/m741-restore-"+(small?"small":"normal")+"-"+(bottom?"bottom":"top")+".png");encoder.Save(file);
                }
            }
            Task pending=dialog.RunAsync();check(!Find<CheckBox>("RestoreOwner").IsEnabled && !Find<CheckBox>("RestoreAcl").IsEnabled && !Find<Button>("RunButton").IsEnabled,"Running restore freezes ownership/ACL choices and duplicate execution");
            await dialog.RunAsync();await pending;
            check(dialog.WasCompleted && Find<CheckBox>("RestoreOwner").IsEnabled,"Actual WPF restore completes and restores option controls");
            await using(var db=new NpgsqlConnection(As(target)))
            {await db.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT pg_catalog.pg_get_userbyid(relowner) FROM pg_catalog.pg_class WHERE oid='public.items'::regclass",db);check((string)(await cmd.ExecuteScalarAsync())! ==owner,"WPF restore applies the selected archive owner");}
            await using(var client=new NpgsqlConnection(As(target,reader)))
            {await client.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT value FROM public.items WHERE id=1",client);check((string)(await cmd.ExecuteScalarAsync())! =="UI ação","WPF restore applies archive reader privileges and exact data");}
            await dialog.RunAsync();check(!dialog.WasCompleted && Find<Button>("RunButton").IsEnabled && Find<CheckBox>("RestoreOwner").IsChecked==true,"Conflicting retry fails without erasing restore choices or disabling recovery controls");
        }
        finally
        {
            dialog?.Close();if(targetCreated)await Sql($"DROP DATABASE {SqlIdentifier.Quote(target)}");if(sourceCreated)await Sql($"DROP DATABASE {SqlIdentifier.Quote(source)}");await Sql($"DROP ROLE IF EXISTS {qr};DROP ROLE IF EXISTS {qo}");
        }
    }
}
