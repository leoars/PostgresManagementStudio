using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class BackupFormatDialogChecks
{
    private static T Find<T>(Window w,string name)where T:FrameworkElement=>(T)w.FindName(name);
    internal static async Task Capture(Window w,string name,bool small,bool bottom,Action<bool,string> check)
    {
        w.Width=small?680:760;w.Height=small?510:550;var scroll=Find<ScrollViewer>(w,"FormScroller");if(bottom)scroll.ScrollToBottom();else scroll.ScrollToTop();await w.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);w.UpdateLayout();var root=(FrameworkElement)w.Content;
        foreach(string action in new[]{"RunButton","CloseButton"}){var b=Find<Button>(w,action);var p=b.TranslatePoint(new Point(),root);check(p.Y>=-0.1 && p.Y+b.ActualHeight<=root.ActualHeight+0.1,"Backup format action fits "+name+"/"+action);}
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){var r=new Rect(0,0,root.ActualWidth,root.ActualHeight);dc.DrawRectangle(w.Background,null,r);dc.DrawRectangle(new VisualBrush(root),null,r);}bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory("artifacts/review");using var file=File.Create("artifacts/review/m756-"+name+".png");encoder.Save(file);
    }
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N")[..12],owner="format_ui_owner_"+suffix,reader="format_ui_reader_"+suffix,source="format_ui_source_"+suffix,target="format_ui_target_"+suffix,schemaTarget="format_ui_schema_"+suffix;
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")??throw new Exception("Missing isolated tools.");
        string folder=Path.GetFullPath(Path.Combine("artifacts/format-ui","cópias_密碼_"+suffix));Directory.CreateDirectory(folder);string archive=Path.Combine(folder,"cópia_密碼.sql");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();var databases=new List<string>();bool roles=false;BackupDialog? dump=null,restore=null;
        async Task Sql(string sql){await using var c=new NpgsqlCommand(sql,admin);await c.ExecuteNonQueryAsync();}
        async Task Create(string db,string role){await Sql($"CREATE DATABASE {SqlIdentifier.Quote(db)} OWNER {SqlIdentifier.Quote(role)}");databases.Add(db);}
        string As(string db,string role="pgms_test")=>new NpgsqlConnectionStringBuilder(connection){Database=db,Username=role,Pooling=false}.ConnectionString;
        async Task<object?> Scalar(string db,string sql){await using var c=new NpgsqlConnection(As(db));await c.OpenAsync();await using var cmd=new NpgsqlCommand(sql,c);return await cmd.ExecuteScalarAsync();}
        try
        {
            await Sql($"CREATE ROLE {SqlIdentifier.Quote(owner)} LOGIN;CREATE ROLE {SqlIdentifier.Quote(reader)} LOGIN");roles=true;await Create(source,owner);await Create(target,"pgms_test");await Create(schemaTarget,"pgms_test");
            await using(var c=new NpgsqlConnection(As(source,owner))){await c.OpenAsync();await using var cmd=new NpgsqlCommand($"CREATE TABLE public.items(id integer GENERATED ALWAYS AS IDENTITY,value text);INSERT INTO public.items(value) VALUES('UI ação 密碼');GRANT SELECT ON public.items TO {SqlIdentifier.Quote(reader)}",c);await cmd.ExecuteNonQueryAsync();}
            dump=new BackupDialog(false,As(source,owner)){ShowInTaskbar=false};dump.Show();
            check((BackupFormat)Find<ComboBox>(dump,"FormatSelector").SelectedValue==BackupFormat.Custom && Find<TextBox>(dump,"OptionsPreview").Text=="pg_dump --format=custom","Backup UI preserves custom/schema-and-data default with actual options preview");
            Find<ComboBox>(dump,"ScopeSelector").SelectedValue=BackupDataScope.DataOnly;check(Find<TextBox>(dump,"OptionsPreview").Text.Contains("--data-only") && Find<TextBlock>(dump,"FormatHelp").Text.Contains("compatible tables"),"Data-only UI explains the required destination schema");Find<ComboBox>(dump,"ScopeSelector").SelectedValue=BackupDataScope.All;
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Tar;check(Find<TextBox>(dump,"OptionsPreview").Text=="pg_dump --format=tar" && !Find<StackPanel>(dump,"SqlBackupFields").IsVisible,"Tar UI previews archive options without inapplicable SQL ownership controls");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.PlainSql;check(Find<TextBox>(dump,"OptionsPreview").Text.Contains("--format=plain --no-owner --no-privileges") && Find<StackPanel>(dump,"SqlBackupFields").IsVisible,"Plain SQL UI omits ownership/ACL by default and exposes explicit choices");
            Find<CheckBox>(dump,"SqlDumpOwner").IsChecked=true;Find<CheckBox>(dump,"SqlDumpAcl").IsChecked=true;Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Custom;check(Find<TextBox>(dump,"OptionsPreview").Text=="pg_dump --format=custom","Returning to archive mode does not pass ignored SQL-only choices");Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.PlainSql;
            check(Find<CheckBox>(dump,"SqlDumpOwner").IsChecked==true && Find<CheckBox>(dump,"SqlDumpAcl").IsChecked==true && !Find<TextBox>(dump,"OptionsPreview").Text.Contains("--no-owner"),"Returning to SQL preserves the user's ownership and ACL choices");
            check((string)Find<Label>(dump,"FileLabel").Content=="_Output file:","Backup labels the destination as an output file for every format");
            foreach(string name in new[]{"FormatSelector","ScopeSelector","SqlDumpOwner","SqlDumpAcl","OptionsPreview"})check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(dump,name))),"Backup format field has an accessible name: "+name);
            Find<TextBox>(dump,"ArchivePath").Text=archive;Find<TextBox>(dump,"ToolsPath").Text=tools;await Capture(dump,"sql-backup-normal",false,false,check);await Capture(dump,"sql-backup-small",true,true,check);
            Task pending=dump.RunAsync();check(!Find<ComboBox>(dump,"FormatSelector").IsEnabled && !Find<ComboBox>(dump,"ScopeSelector").IsEnabled && !Find<CheckBox>(dump,"SqlDumpOwner").IsEnabled,"Running SQL backup freezes format/content/ownership choices");await dump.RunAsync();await pending;check(dump.WasCompleted && File.Exists(archive),"WPF creates an actual SQL dump using the selected options");
            restore=new BackupDialog(true,As(target)){ShowInTaskbar=false};restore.Show();Find<CheckBox>(restore,"RestoreOwner").IsChecked=true;Find<CheckBox>(restore,"RestoreAcl").IsChecked=true;Find<ComboBox>(restore,"FormatSelector").SelectedValue=BackupFormat.PlainSql;
            check(!Find<StackPanel>(restore,"RestoreFields").IsVisible && !Find<StackPanel>(restore,"BackupScopeFields").IsVisible && Find<TextBox>(restore,"OptionsPreview").Text.StartsWith("psql --no-psqlrc --single-transaction") && (string)Find<Button>(restore,"RunButton").Content=="Run SQL","SQL restore UI selects psql and hides archive-only choices");
            check(Find<TextBlock>(restore,"FormatHelp").Text.Contains("Explicit COMMIT") && Find<TextBlock>(restore,"Description").Text.Contains("trusted SQL script"),"SQL restore explains script authority and explicit transaction boundaries");
            check((string)Find<Label>(restore,"FileLabel").Content=="_SQL script:" && System.Windows.Automation.AutomationProperties.GetName(Find<TextBox>(restore,"ArchivePath"))=="SQL script file","SQL restore identifies its input consistently in visible and accessible labels");
            Find<TextBox>(restore,"ArchivePath").Text=archive;Find<TextBox>(restore,"ToolsPath").Text=tools;await Capture(restore,"sql-restore-normal",false,false,check);await Capture(restore,"sql-restore-small",true,true,check);
            pending=restore.RunAsync();check(!Find<ComboBox>(restore,"FormatSelector").IsEnabled,"Running SQL restore freezes its tool selection");await restore.RunAsync();await pending;check(restore.WasCompleted && Find<ComboBox>(restore,"FormatSelector").IsEnabled,"WPF SQL restore completes and releases controls");
            check((string)(await Scalar(target,"SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='public.items'::regclass"))! ==owner,"WPF SQL roundtrip applies the ownership selected when dumping");
            await using(var c=new NpgsqlConnection(As(target,reader))){await c.OpenAsync();await using var cmd=new NpgsqlCommand("SELECT value FROM public.items WHERE id=1",c);check((string)(await cmd.ExecuteScalarAsync())! =="UI ação 密碼","WPF SQL roundtrip preserves exact data and actual reader privileges");}
            // Restore Database… without a database (the Databases folder): the destination is chosen, and a new name creates it first.
            string fresh="pgms_restore_new_"+Guid.NewGuid().ToString("N")[..8];
            var chooser=new BackupDialog(true,As("postgres"),chooseDatabase:true){ShowInTaskbar=false};chooser.Show();
            try
            {
                await chooser.LoadDatabasesAsync();
                check(Find<StackPanel>(chooser,"DatabaseFields").IsVisible && Find<TextBlock>(chooser,"Target").Text.Contains("Database: chosen below") && Find<TextBlock>(chooser,"Description").Text.Contains("chosen above") && ((IEnumerable<string>)Find<ComboBox>(chooser,"DatabasePicker").ItemsSource).Contains(target),
                    "Restore without a database asks for the destination and lists the databases");
                Find<ComboBox>(chooser,"FormatSelector").SelectedValue=BackupFormat.PlainSql;Find<TextBox>(chooser,"ArchivePath").Text=archive;Find<TextBox>(chooser,"ToolsPath").Text=tools;
                await chooser.RunAsync();
                check(!chooser.WasCompleted && Find<TextBox>(chooser,"Feedback").Text.StartsWith("Choose the database to restore into",StringComparison.Ordinal),"Without a destination nothing runs, not even into the login database");
                string? asked=null;chooser.ConfirmCreateDatabase=name=>{asked=name;return false;};
                Find<ComboBox>(chooser,"DatabasePicker").Text=fresh;
                await chooser.RunAsync();
                check(asked==fresh && !chooser.WasCompleted && await Scalar("postgres",$"SELECT 1 FROM pg_database WHERE datname='{fresh}'") is null,"A new name asks before creating the database; No creates nothing");
                chooser.ConfirmCreateDatabase=_=>true;databases.Add(fresh);
                await chooser.RunAsync();
                check(chooser.WasCompleted && chooser.Database==fresh && (string?)await Scalar(fresh,"SELECT value FROM public.items WHERE id=1")=="UI ação 密碼","Yes creates the database and restores into it, not into the login database");
            }
            finally{chooser.Close();}
            await restore.RunAsync();check(!restore.WasCompleted && Find<ComboBox>(restore,"FormatSelector").IsEnabled && (BackupFormat)Find<ComboBox>(restore,"FormatSelector").SelectedValue==BackupFormat.PlainSql,"Conflicting SQL retry reports failure and preserves format for recovery");
            Find<ComboBox>(restore,"FormatSelector").SelectedValue=BackupFormat.Custom;check(Find<StackPanel>(restore,"RestoreFields").IsVisible && Find<CheckBox>(restore,"RestoreOwner").IsChecked==true && !Find<TextBox>(restore,"OptionsPreview").Text.Contains("--no-owner"),"Returning to archive restore preserves its independent ownership choice");
            string schemaArchive=Path.Combine(folder,"schema_密碼.sql");Find<ComboBox>(dump,"ScopeSelector").SelectedValue=BackupDataScope.SchemaOnly;Find<TextBox>(dump,"ArchivePath").Text=schemaArchive;await dump.RunAsync();check(dump.WasCompleted && Find<TextBox>(dump,"OptionsPreview").Text.Contains("--schema-only"),"WPF creates the explicitly selected schema-only SQL backup");await new BackupService(As(schemaTarget),tools).RestoreSqlAsync(schemaArchive);check(Convert.ToInt64(await Scalar(schemaTarget,"SELECT count(*) FROM public.items"))==0,"WPF schema-only choice omits row data in a real restore");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Custom;Find<ComboBox>(dump,"CompressionSelector").SelectedValue=BackupCompression.Gzip;Find<ComboBox>(dump,"CompressionLevelSelector").SelectedItem=9;
            check(Find<TextBox>(dump,"OptionsPreview").Text.Contains("--compress=gzip:9") && Find<StackPanel>(dump,"CompressionLevelFields").IsVisible,"Gzip UI previews the selected real compression level");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.PlainSql;
            check(!Find<StackPanel>(dump,"CompressionFields").IsVisible && !Find<TextBox>(dump,"OptionsPreview").Text.Contains("--compress"),"Plain SQL hides compression and omits unsupported compressed input");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Tar;
            check(!Find<StackPanel>(dump,"CompressionFields").IsVisible && !Find<TextBox>(dump,"OptionsPreview").Text.Contains("--compress"),"Tar hides incompatible compression");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Custom;
            check((int)Find<ComboBox>(dump,"CompressionLevelSelector").SelectedItem==9 && Find<TextBox>(dump,"OptionsPreview").Text.Contains("--compress=gzip:9"),"Returning to archive mode preserves gzip choice and level");
            foreach(string name in new[]{"CompressionSelector","CompressionLevelSelector"})check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(dump,name))),"Compression field has an accessible name: "+name);
            await Capture(dump,"m846-compression-normal",false,false,check);await Capture(dump,"m846-compression-small",true,true,check);
            Find<ComboBox>(dump,"CompressionSelector").SelectedValue=BackupCompression.Default;
            string directoryTarget="directory_ui_"+suffix;await Create(directoryTarget,"pgms_test");
            string directoryArchive=Path.Combine(folder,"diretório_密碼");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Directory;check(Find<TextBox>(dump,"Feedback").Text.Length==0,"Changing format clears prior operation status");Find<ComboBox>(dump,"ScopeSelector").SelectedValue=BackupDataScope.All;Find<ComboBox>(dump,"JobsSelector").SelectedItem=2;
            check(Find<StackPanel>(dump,"JobsFields").IsVisible && Find<TextBox>(dump,"OptionsPreview").Text=="pg_dump --format=directory --jobs=2","Directory UI exposes selected parallel jobs in actual preview");
            check(System.Windows.Automation.AutomationProperties.GetName(Find<ComboBox>(dump,"JobsSelector"))=="Parallel backup jobs" && (string)Find<Label>(dump,"FileLabel").Content=="_New output directory:","Directory output and jobs have explicit accessible labels");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Custom;
            check(!Find<StackPanel>(dump,"JobsFields").IsVisible && !Find<TextBox>(dump,"OptionsPreview").Text.Contains("--jobs"),"Archive format hides and omits incompatible parallel dump jobs");
            Find<ComboBox>(dump,"FormatSelector").SelectedValue=BackupFormat.Directory;
            check((int)Find<ComboBox>(dump,"JobsSelector").SelectedItem==2,"Returning to directory format preserves selected jobs");
            Find<TextBox>(dump,"ArchivePath").Text=directoryArchive;
            await Capture(dump,"m844-directory-normal",false,false,check);await Capture(dump,"m844-directory-small",true,true,check);
            pending=dump.RunAsync();check(!Find<ComboBox>(dump,"JobsSelector").IsEnabled,"Parallel jobs freeze while directory backup runs");await dump.RunAsync();await pending;
            check(dump.WasCompleted && File.Exists(Path.Combine(directoryArchive,"toc.dat")),"WPF creates actual Unicode directory archive with two jobs");
            await dump.RunAsync();check(!dump.WasCompleted && Find<ComboBox>(dump,"JobsSelector").IsEnabled && File.Exists(Path.Combine(directoryArchive,"toc.dat")),"Existing directory refusal preserves backup and restores editable jobs");
            restore.Close();restore=new BackupDialog(true,As(directoryTarget)){ShowInTaskbar=false};restore.Show();
            Find<ComboBox>(restore,"FormatSelector").SelectedValue=BackupFormat.Directory;Find<TextBox>(restore,"ArchivePath").Text=directoryArchive;Find<TextBox>(restore,"ToolsPath").Text=tools;
            check(!Find<StackPanel>(restore,"JobsFields").IsVisible && Find<StackPanel>(restore,"RestoreFields").IsVisible && Find<TextBox>(restore,"OptionsPreview").Text.Contains("--single-transaction"),"Directory restore remains serial and exposes ownership choices");
            await Capture(restore,"m844-directory-restore-small",true,false,check);await restore.RunAsync();
            check(restore.WasCompleted && (string)(await Scalar(directoryTarget,"SELECT value FROM public.items WHERE id=1"))! =="UI ação 密碼","WPF directory roundtrip restores exact Unicode data");
            Find<TextBox>(restore,"ArchivePath").Text=directoryArchive+"-changed";
            check(Find<TextBox>(restore,"Feedback").Text.Length==0 && restore.WasCompleted,"Changing proposal clears stale success while retaining completed restore for explorer refresh");
            Find<TextBox>(restore,"Feedback").Text="Previous failure";Find<TextBox>(restore,"ToolsPath").Text=tools+Path.DirectorySeparatorChar;
            check(Find<TextBox>(restore,"Feedback").Text.Length==0,"Changing tool path clears stale failure feedback");
            string canceledArchive=Path.Combine(folder,"canceled-directory");Find<TextBox>(dump,"ArchivePath").Text=canceledArchive;
            await using(var blocker=new NpgsqlConnection(As(source,owner)))
            {
                await blocker.OpenAsync();await using var transaction=await blocker.BeginTransactionAsync();
                await using(var command=new NpgsqlCommand("LOCK TABLE public.items IN ACCESS EXCLUSIVE MODE",blocker,transaction))await command.ExecuteNonQueryAsync();
                pending=dump.RunAsync();bool blocked=false;
                try
                {
                    var deadline=DateTime.UtcNow.AddSeconds(15);
                    while(DateTime.UtcNow<deadline && !pending.IsCompleted)
                    {
                        blocked=(bool)(await Scalar(source,"SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND application_name='PGMS.BackupRestore' AND cardinality(pg_blocking_pids(pid))>0)"))!;
                        if(blocked)break;await Task.Delay(40);
                    }
                    check(blocked,"Directory dump is observed waiting on a real server lock before cancellation");
                    dump.Close();check(dump.IsVisible && !Find<ComboBox>(dump,"JobsSelector").IsEnabled,"Closing a blocked dump keeps the window and jobs protected until cancellation");
                }
                finally{Find<Button>(dump,"CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await pending.WaitAsync(TimeSpan.FromSeconds(15));}
                check(!dump.WasCompleted && Find<ComboBox>(dump,"JobsSelector").IsEnabled && Find<TextBox>(dump,"Feedback").Text.Contains("Backup canceled"),"Canceling blocked parallel dump releases controls with cancellation feedback");
                check(!Directory.Exists(canceledArchive) && !Directory.EnumerateFileSystemEntries(folder,"PGMS-*.partial").Any(),"Canceled parallel dump leaves no published or partial directory");
                await transaction.RollbackAsync();
                bool disconnected=false;var disconnectDeadline=DateTime.UtcNow.AddSeconds(10);
                while(DateTime.UtcNow<disconnectDeadline)
                {
                    disconnected=(bool)(await Scalar(source,"SELECT NOT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND application_name='PGMS.BackupRestore')"))!;
                    if(disconnected)break;await Task.Delay(40);
                }
                check(disconnected,"Canceled dump sessions disappear after the blocking transaction releases its lock");
            }
            uint uiLargeObject=Convert.ToUInt32(await Scalar(source,"SELECT lo_from_bytea(0,decode('0011ff80','hex'))"));
            Find<ComboBox>(dump,"LargeObjectsSelector").SelectedValue=BackupLargeObjects.Include;
            check(Find<TextBox>(dump,"OptionsPreview").Text.Contains("--large-objects"),"Large-object UI previews explicit database-wide inclusion");
            Find<ComboBox>(dump,"ScopeSelector").SelectedValue=BackupDataScope.SchemaOnly;
            check(!Find<StackPanel>(dump,"LargeObjectsFields").IsVisible && !Find<TextBox>(dump,"OptionsPreview").Text.Contains("--large-objects"),"Schema-only hides and omits incompatible large-object choice");
            Find<ComboBox>(dump,"ScopeSelector").SelectedValue=BackupDataScope.All;
            check((BackupLargeObjects)Find<ComboBox>(dump,"LargeObjectsSelector").SelectedValue==BackupLargeObjects.Include && Find<TextBox>(dump,"OptionsPreview").Text.Contains("--large-objects"),"Returning to data preserves the explicit large-object choice");
            check(System.Windows.Automation.AutomationProperties.GetName(Find<ComboBox>(dump,"LargeObjectsSelector"))=="Backup large objects" && !Find<StackPanel>(restore,"LargeObjectsFields").IsVisible,"Large-object selection is accessible and absent from restore");
            await Capture(dump,"m855-large-objects-normal",false,false,check);await Capture(dump,"m855-large-objects-small",true,true,check);
            await Scalar(source,"CREATE SCHEMA other_excluded;CREATE TABLE other_excluded.items(value text)");
            Find<CheckBox>(dump,"LimitSchema").IsChecked=true;Find<TextBox>(dump,"SchemaFilterName").Text="";
            check(!Find<Button>(dump,"RunButton").IsEnabled,"Enabled schema filter refuses empty exact name before execution");
            Find<TextBox>(dump,"SchemaFilterName").Text="public";
            check(Find<Button>(dump,"RunButton").IsEnabled && Find<TextBox>(dump,"OptionsPreview").Text.Contains("include schema \"public\""),"Schema filter preview names the exact selected schema");
            check(System.Windows.Automation.AutomationProperties.GetName(Find<TextBox>(dump,"SchemaFilterName"))=="Exact backup schema name" && !Find<StackPanel>(restore,"SchemaFilterFields").IsVisible,"Schema filter is accessible and absent from restore");
            await Capture(dump,"m853-schema-normal",false,false,check);await Capture(dump,"m853-schema-small",true,true,check);
            Find<ComboBox>(dump,"CompressionSelector").SelectedValue=BackupCompression.Gzip;Find<ComboBox>(dump,"CompressionLevelSelector").SelectedItem=9;
            pending=dump.RunAsync();check(!Find<ComboBox>(dump,"CompressionSelector").IsEnabled && !Find<ComboBox>(dump,"CompressionLevelSelector").IsEnabled,"Running compressed backup freezes method and level");check(!Find<CheckBox>(dump,"LimitSchema").IsEnabled && !Find<TextBox>(dump,"SchemaFilterName").IsEnabled,"Running filtered backup freezes its exact selection");check(!Find<ComboBox>(dump,"LargeObjectsSelector").IsEnabled,"Running backup freezes large-object scope");await pending;
            check(!dump.WasCompleted && !Directory.Exists(canceledArchive) && Find<TextBox>(dump,"Feedback").Text.Contains("permission denied"),"Large-object inclusion refuses unreadable objects without publishing a backup");
            await Scalar(source,$"GRANT SELECT ON LARGE OBJECT {uiLargeObject} TO {SqlIdentifier.Quote(owner)}");await dump.RunAsync();
            check(dump.WasCompleted && File.Exists(Path.Combine(canceledArchive,"toc.dat")),"Explicit retry after permission grant creates a complete directory archive");
            check(Directory.EnumerateFiles(canceledArchive,"*.gz").Any(),"WPF gzip choice produces actual compressed directory data");
            string compressedUiTarget="compressed_ui_"+suffix;await Create(compressedUiTarget,"pgms_test");
            bool schemaConflict=false;try{await new BackupService(As(compressedUiTarget),tools).RestoreDirectoryAsync(canceledArchive);}catch(IOException){schemaConflict=true;}
            check(schemaConflict && (bool)(await Scalar(compressedUiTarget,"SELECT to_regclass('public.items') IS NULL"))!,"Selected public schema restore refuses existing schema without importing data");
            await Scalar(compressedUiTarget,"DROP SCHEMA public");await new BackupService(As(compressedUiTarget),tools).RestoreDirectoryAsync(canceledArchive);
            check((string)(await Scalar(compressedUiTarget,"SELECT value FROM public.items WHERE id=1"))! =="UI ação 密碼","WPF gzip level 9 output roundtrips exact Unicode data");
            check((bool)(await Scalar(compressedUiTarget,"SELECT NOT EXISTS(SELECT FROM pg_namespace WHERE nspname='other_excluded')"))!,"WPF schema-limited backup excludes the unrelated schema on real restore");
            check((string)(await Scalar(compressedUiTarget,$"SELECT encode(lo_get({uiLargeObject}::oid),'hex')"))! =="0011ff80","WPF explicit large-object inclusion restores exact binary bytes outside schema membership");
            Find<ComboBox>(dump,"SelectionKindSelector").SelectedIndex=1;
            check(!Find<Button>(dump,"RunButton").IsEnabled,"Table selection requires an exact table name");
            Find<TextBox>(dump,"TableFilterName").Text="items";
            Find<ComboBox>(dump,"LargeObjectsSelector").SelectedValue=BackupLargeObjects.Default;
            check(Find<TextBox>(dump,"OptionsPreview").Text.Contains("include table \"public\".\"items\""),"Table preview qualifies the literal name");
            check(Find<TextBlock>(dump,"SelectionHelp").Text.Contains("must already exist"),"Table selection explains external prerequisites");
            foreach(string field in new[]{"SelectionKindSelector","TableFilterName"})check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(Find<FrameworkElement>(dump,field))),"Table field is accessible: "+field);
            await Capture(dump,"m857-table-normal",false,false,check);await Capture(dump,"m857-table-small",true,true,check);
            string tableUiArchive=Path.Combine(folder,"table-ui"),tableUiTarget="table_ui_"+suffix;await Create(tableUiTarget,"pgms_test");
            Find<TextBox>(dump,"ArchivePath").Text=tableUiArchive;
            pending=dump.RunAsync();check(!Find<ComboBox>(dump,"SelectionKindSelector").IsEnabled && !Find<TextBox>(dump,"TableFilterName").IsEnabled,"Running table backup freezes selection");await pending;
            check(dump.WasCompleted,"WPF creates selected table directory archive");
            await new BackupService(As(tableUiTarget),tools).RestoreDirectoryAsync(tableUiArchive);
            check((string)(await Scalar(tableUiTarget,"SELECT value FROM public.items WHERE id=1"))! =="UI ação 密碼" && Convert.ToInt64(await Scalar(tableUiTarget,"SELECT count(*) FROM pg_largeobject_metadata"))==0,"WPF exact table roundtrip preserves rows and default omits large objects");
            string scopedUiTarget="scope_ui_"+suffix;await Create(scopedUiTarget,"pgms_test");
            restore.Close();restore=new BackupDialog(true,As(scopedUiTarget)){ShowInTaskbar=false};restore.Show();
            Find<TextBox>(restore,"ArchivePath").Text=tableUiArchive;Find<TextBox>(restore,"ToolsPath").Text=tools;Find<ComboBox>(restore,"FormatSelector").SelectedValue=BackupFormat.Directory;
            Find<ComboBox>(restore,"RestoreScopeSelector").SelectedValue=BackupDataScope.SchemaOnly;
            check(Find<TextBox>(restore,"OptionsPreview").Text.Contains("--schema-only"),"Restore UI previews definitions-only choice");
            await Capture(restore,"m859-scope-normal",false,false,check);await Capture(restore,"m859-scope-small",true,true,check);
            pending=restore.RunAsync();check(!Find<ComboBox>(restore,"RestoreScopeSelector").IsEnabled,"Running restore freezes content scope");await pending;
            check(restore.WasCompleted && Convert.ToInt64(await Scalar(scopedUiTarget,"SELECT count(*) FROM public.items"))==0,"WPF restores definitions without data");
            Find<ComboBox>(restore,"RestoreScopeSelector").SelectedValue=BackupDataScope.DataOnly;
            check(Find<TextBlock>(restore,"RestoreScopeHelp").Text.Contains("appended") && Find<TextBox>(restore,"OptionsPreview").Text.Contains("--data-only"),"Data-only restore explains existing rows and previews actual flag");
            Find<ComboBox>(restore,"FormatSelector").SelectedValue=BackupFormat.PlainSql;
            check(!Find<ComboBox>(restore,"RestoreScopeSelector").IsVisible && !Find<TextBox>(restore,"OptionsPreview").Text.Contains("--data-only"),"SQL restore omits archive content controls");
            Find<ComboBox>(restore,"FormatSelector").SelectedValue=BackupFormat.Directory;
            check((BackupDataScope)Find<ComboBox>(restore,"RestoreScopeSelector").SelectedValue==BackupDataScope.DataOnly,"Returning to archive preserves restore scope");
            await restore.RunAsync();check(restore.WasCompleted && (string)(await Scalar(scopedUiTarget,"SELECT value FROM public.items WHERE id=1"))! =="UI ação 密碼","WPF data-only restore loads exact Unicode row");
            check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(Find<ComboBox>(restore,"RestoreScopeSelector"))),"Restore scope has accessible name");
        }
        finally{dump?.Close();restore?.Close();foreach(string db in databases.AsEnumerable().Reverse())await Sql($"DROP DATABASE {SqlIdentifier.Quote(db)}");if(roles)await Sql($"DROP ROLE {SqlIdentifier.Quote(reader)};DROP ROLE {SqlIdentifier.Quote(owner)}");}
    }
}
