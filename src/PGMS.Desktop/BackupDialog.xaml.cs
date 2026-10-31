using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public partial class BackupDialog : Window
{
    private sealed record FormatChoice(BackupFormat Value,string Label);
    private sealed record LargeObjectChoice(BackupLargeObjects Value,string Label);
    private sealed record CompressionChoice(BackupCompression Value,string Label);
    private sealed record ScopeChoice(BackupDataScope Value,string Label);
    private bool ready;
    private readonly bool restore;
    private readonly bool globals;
    private readonly string connectionString;
    private CancellationTokenSource? cancellation;
    public bool WasCompleted { get; private set; }

    public BackupDialog(bool restore, string connectionString, bool globals=false)
    {
        InitializeComponent();
        this.restore = restore;
        this.globals = globals;
        this.connectionString = connectionString;
        var c = new NpgsqlConnectionStringBuilder(connectionString);
        Title = restore ? "Restore Database" : "Back Up Database";
        if(globals)Title=restore?"Restore Global Objects":"Back Up Global Objects";
        RunButton.Content = restore ? "Restore" : "Back Up";
        Target.Text = $"{Title}\nServer: {c.Host}:{c.Port}    Database: {c.Database}    User: {c.Username}";
        FormatSelector.ItemsSource=restore?new[]{new FormatChoice(BackupFormat.Custom,"Custom or tar archive"),new(BackupFormat.PlainSql,"Plain SQL script"),new(BackupFormat.Directory,"Directory archive")}
            :new[]{new FormatChoice(BackupFormat.Custom,"Custom archive"),new(BackupFormat.Tar,"Tar archive"),new(BackupFormat.PlainSql,"Plain SQL script"),new(BackupFormat.Directory,"Directory archive")};FormatSelector.SelectedIndex=0;
        ScopeSelector.ItemsSource=new[]{new ScopeChoice(BackupDataScope.All,"Schema and data"),new(BackupDataScope.SchemaOnly,"Schema only"),new(BackupDataScope.DataOnly,"Data only")};ScopeSelector.SelectedIndex=0;
        RestoreScopeSelector.ItemsSource=ScopeSelector.ItemsSource;RestoreScopeSelector.SelectedIndex=0;
        LargeObjectsSelector.ItemsSource=new[]{new LargeObjectChoice(BackupLargeObjects.Default,"Tool default"),new(BackupLargeObjects.Include,"Include all large objects"),new(BackupLargeObjects.Exclude,"Exclude large objects")};LargeObjectsSelector.SelectedIndex=0;
        CompressionSelector.ItemsSource=new[]{new CompressionChoice(BackupCompression.Default,"Tool default"),new(BackupCompression.None,"No compression"),new(BackupCompression.Gzip,"Gzip")};CompressionSelector.SelectedIndex=0;
        CompressionLevelSelector.ItemsSource=Enumerable.Range(1,9);CompressionLevelSelector.SelectedItem=6;
        JobsSelector.ItemsSource=Enumerable.Range(1,32);JobsSelector.SelectedIndex=0;
        if(globals){FormatSelector.SelectedValue=BackupFormat.PlainSql;FormatSelector.IsEnabled=false;}
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (Directory.Exists(root)) ToolsPath.Text = Directory.GetDirectories(root).OrderByDescending(p => int.TryParse(Path.GetFileName(p), out int version) ? version : 0).Select(p => Path.Combine(p, "bin")).FirstOrDefault(p => File.Exists(Path.Combine(p, "pg_dump.exe"))) ?? "";
        SelectionKindSelector.SelectedIndex=0;
        ready=true;
        UpdateOptions();
    }
    private BackupFormat Format=>((FormatChoice)FormatSelector.SelectedItem).Value;
    private BackupCompression Compression=>Format is BackupFormat.Custom or BackupFormat.Directory?((CompressionChoice)CompressionSelector.SelectedItem).Value:BackupCompression.Default;
    private BackupDataScope Scope=>((ScopeChoice)ScopeSelector.SelectedItem).Value;
    private BackupOptions DumpOptions=>new(Format,Scope,Format==BackupFormat.PlainSql && SqlDumpOwner.IsChecked==true,Format==BackupFormat.PlainSql && SqlDumpAcl.IsChecked==true){Jobs=Format==BackupFormat.Directory?(int)JobsSelector.SelectedItem:1,Compression=Compression,CompressionLevel=Compression==BackupCompression.Gzip?(int)CompressionLevelSelector.SelectedItem:null,LargeObjects=Scope==BackupDataScope.SchemaOnly?BackupLargeObjects.Default:((LargeObjectChoice)LargeObjectsSelector.SelectedItem).Value,Selection=!restore && !globals && LimitSchema.IsChecked==true?(SelectionKindSelector.SelectedIndex==1?BackupSelection.ForTables([new(SchemaFilterName.Text,TableFilterName.Text)]):new BackupSelection([SchemaFilterName.Text])):null};
    private RestoreOptions Options=>new(RestoreOwner.IsChecked==true,RestoreAcl.IsChecked==true){Scope=restore && !globals && Format!=BackupFormat.PlainSql?((ScopeChoice)RestoreScopeSelector.SelectedItem).Value:BackupDataScope.All};
    private void OptionsChanged(object sender,RoutedEventArgs e){ClearPreviousFeedback();if(OptionsPreview is not null)UpdateOptions();}
    private void FileChanged(object sender,System.Windows.Controls.TextChangedEventArgs e){ClearPreviousFeedback();if(GlobalReviewed is not null)GlobalReviewed.IsChecked=false;}
    private void SchemaFilterChanged(object sender,System.Windows.Controls.TextChangedEventArgs e){ClearPreviousFeedback();if(ready)UpdateOptions();}
    private void ClearPreviousFeedback(){if(ready && cancellation is null)Feedback.Clear();}
    private void UpdateOptions()
    {
        if(!ready)return;
        LargeObjectsFields.Visibility=!restore && !globals && Scope!=BackupDataScope.SchemaOnly?Visibility.Visible:Visibility.Collapsed;
        SchemaFilterFields.Visibility=!restore && !globals?Visibility.Visible:Visibility.Collapsed;
        SchemaNameFields.Visibility=LimitSchema.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        TableNameFields.Visibility=SelectionKindSelector.SelectedIndex==1?Visibility.Visible:Visibility.Collapsed;
        SelectionHelp.Text=SelectionKindSelector.SelectedIndex==1?"Enter exact schema and table names without SQL quotes. Wildcards are literal. The target schema and external dependencies must already exist before restore. Large objects are not included automatically.":"Enter the name without SQL quotes. Wildcards are literal. Dependencies outside this schema and large objects are not included automatically. Restoring schema definitions requires the selected schema to be absent, including public.";
        SchemaFilterName.IsEnabled=cancellation is null && LimitSchema.IsChecked==true;
        RestoreScopeHelp.Text=Options.Scope==BackupDataScope.DataOnly?"Requires compatible existing tables. Rows are appended; existing rows are not removed. Constraints and triggers remain active.":"Definitions create objects in the destination. Existing objects are not dropped; conflicts cause an error.";
        bool sql=Format==BackupFormat.PlainSql;
        bool directory=Format==BackupFormat.Directory;
        CompressionFields.Visibility=!restore && !globals && Format is BackupFormat.Custom or BackupFormat.Directory?Visibility.Visible:Visibility.Collapsed;
        CompressionLevelFields.Visibility=Compression==BackupCompression.Gzip?Visibility.Visible:Visibility.Collapsed;
        JobsFields.Visibility=!restore && directory && !globals?Visibility.Visible:Visibility.Collapsed;
        RunButton.Content=restore?(sql?"Run SQL":"Restore"):"Back Up";
        FileLabel.Content=directory?(restore?"_Archive directory:":"_New output directory:"):restore?(sql?"_SQL script:":"_Archive file:"):"_Output file:";
        System.Windows.Automation.AutomationProperties.SetName(ArchivePath,directory?(restore?"Archive directory":"New backup output directory"):restore?(sql?"SQL script file":"Archive file"):"Backup output file");
        BackupScopeFields.Visibility=restore?Visibility.Collapsed:Visibility.Visible;RestoreFields.Visibility=restore && !sql?Visibility.Visible:Visibility.Collapsed;SqlBackupFields.Visibility=!restore && sql?Visibility.Visible:Visibility.Collapsed;
        Description.Text=restore
            ?sql?"Run a trusted SQL script with psql in this existing database. Ownership and privileges follow the script contents. Existing objects are not dropped automatically.":"Restore an archive into this existing database in one transaction. Choose content below. Definitions require absent target objects; data-only requires compatible existing tables. Existing objects are not dropped. Choose whether to restore archive ownership and object privileges below."
            :"Back up this database using the selected format and content. Choose a new destination; existing files and directories are not overwritten. Server roles and tablespaces are not included.";
        FormatHelp.Text=restore
            ?sql?"psql requests one transaction and stops on errors. Explicit COMMIT, reconnect or transaction commands in a script can commit earlier changes. Review the script before running it.":"pg_restore reads custom, tar and directory archives. Restore errors before COMMIT roll back the transaction."
            :Scope==BackupDataScope.DataOnly?"Data-only restore requires compatible tables already present in the destination.":sql?"Plain SQL is restored with psql. Choose ownership and privilege commands now; they cannot be omitted later by archive restore options.":"Custom, tar and directory archives use pg_restore. Ownership and privilege choices are made when restoring.";
        try
        {
            var dumpOptions=DumpOptions;
            OptionsPreview.Text=restore?(sql?"psql "+string.Join(" ",BackupService.SqlRestoreArguments()):"pg_restore "+string.Join(" ",Options.ToArguments())):"pg_dump "+string.Join(" ",dumpOptions.ToArguments())+(dumpOptions.Selection is null?"":" --filter <generated UTF-8 file>\n"+dumpOptions.Selection.FilterText);
            RunButton.IsEnabled=cancellation is null;
        }
        catch(ArgumentException ex){OptionsPreview.Text=ex.Message;RunButton.IsEnabled=false;}
        if(globals)
        {
            BackupScopeFields.Visibility=RestoreFields.Visibility=SqlBackupFields.Visibility=Visibility.Collapsed;
            GlobalReviewed.Visibility=restore?Visibility.Visible:Visibility.Collapsed;
            RunButton.Content=restore?"Restore Globals":"Back Up Globals";
            Description.Text=restore?"Run a trusted, reviewed global SQL script against this server. This affects roles and tablespaces shared by all databases. Existing objects are not dropped or skipped automatically.":"Back up server roles, memberships and tablespaces to SQL. Database contents are not included. Role passwords are omitted and must be configured separately after restore.";
            FormatHelp.Text=restore?"Earlier statements may remain committed after an error or cancellation. Reconcile the existing bootstrap role and grantors; tablespace directories must exist and be empty. Inspect the destination before retrying.":"Choose a new output file. Tablespace paths are saved as absolute paths; review them before restoring elsewhere.";
            OptionsPreview.Text=restore?"psql --no-psqlrc --set ON_ERROR_STOP=1 (no single transaction)":"pg_dumpall --globals-only --no-role-passwords --encoding=UTF8";
        }
    }

    private void Browse(object sender, RoutedEventArgs e)
    {
        if (Format==BackupFormat.Directory)
        {
            var folder = new OpenFolderDialog { Title=restore?"Select PostgreSQL directory archive":"Select parent folder for a new directory archive" };
            if(folder.ShowDialog(this)==true)ArchivePath.Text=restore?folder.FolderName:Path.Combine(folder.FolderName,"backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            return;
        }
        if (restore)
        {
            var picker = new OpenFileDialog { Filter = Format==BackupFormat.PlainSql?"SQL script (*.sql)|*.sql|All files (*.*)|*.*":"PostgreSQL archive (*.backup;*.dump;*.tar)|*.backup;*.dump;*.tar|All files (*.*)|*.*" };
            if (picker.ShowDialog(this) == true) ArchivePath.Text = picker.FileName;
        }
        else
        {
            var picker = new SaveFileDialog { Filter = Format switch{BackupFormat.PlainSql=>"SQL script (*.sql)|*.sql",BackupFormat.Tar=>"PostgreSQL tar archive (*.tar)|*.tar",_=>"PostgreSQL custom archive (*.backup)|*.backup"}, DefaultExt = Format switch{BackupFormat.PlainSql=>".sql",BackupFormat.Tar=>".tar",_=>".backup"}, OverwritePrompt = false };
            picker.FileOk += (_, args) =>
            {
                if (!File.Exists(picker.FileName)) return;
                args.Cancel = true;
                MessageBox.Show(this, "Choose a new file name. Existing backups are never overwritten.", "Back Up Database", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            if (picker.ShowDialog(this) == true) ArchivePath.Text = picker.FileName;
        }
    }
    private void BrowseTools(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select PostgreSQL bin folder" };
        if (picker.ShowDialog(this) == true) ToolsPath.Text = picker.FolderName;
    }
    private async void Run(object sender, RoutedEventArgs e)=>await RunAsync();
    internal async Task RunAsync()
    {
        if(cancellation is not null)return;
        if (string.IsNullOrWhiteSpace(ArchivePath.Text) || string.IsNullOrWhiteSpace(ToolsPath.Text)) { Feedback.Text = "Select a backup path and the PostgreSQL bin folder."; return; }
        WasCompleted = false;
        if(globals && restore)
        {
            if(GlobalReviewed.IsChecked!=true){Feedback.Text="Review the global SQL script and destination, then confirm the review checkbox.";return;}
            if(MessageBox.Show(this,"Restore global objects on this server? Earlier statements can remain committed if a later command fails.\n\n"+Target.Text+"\n\nScript: "+ArchivePath.Text,Title,MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        }
        cancellation = new CancellationTokenSource();
        LimitSchema.IsEnabled=SchemaFilterName.IsEnabled=SelectionKindSelector.IsEnabled=TableFilterName.IsEnabled=false;
        GlobalReviewed.IsEnabled=false;
        RunButton.IsEnabled = ArchivePath.IsEnabled = ToolsPath.IsEnabled = BrowseButton.IsEnabled = ToolsButton.IsEnabled = false;
        RestoreScopeSelector.IsEnabled=RestoreOwner.IsEnabled=RestoreAcl.IsEnabled=false;
        LargeObjectsSelector.IsEnabled=CompressionSelector.IsEnabled=CompressionLevelSelector.IsEnabled=JobsSelector.IsEnabled=FormatSelector.IsEnabled=ScopeSelector.IsEnabled=SqlDumpOwner.IsEnabled=SqlDumpAcl.IsEnabled=false;
        CloseButton.Content = "Cancel";
        Progress.Visibility = Visibility.Visible;
        Feedback.Text = restore ? "Restoring database…" : "Creating backup…";
        try
        {
            var service = new BackupService(connectionString, ToolsPath.Text);
            if(globals){if(restore)await service.RestoreGlobalsAsync(ArchivePath.Text,cancellation.Token);else await service.BackupGlobalsAsync(ArchivePath.Text,cancellation.Token);}
            else if (restore){if(Format==BackupFormat.PlainSql)await service.RestoreSqlAsync(ArchivePath.Text,cancellation.Token);else if(Format==BackupFormat.Directory)await service.RestoreDirectoryAsync(ArchivePath.Text,Options,cancellation.Token);else await service.RestoreWithOptionsAsync(ArchivePath.Text,Options,cancellation.Token);}
            else await service.BackupWithOptionsAsync(ArchivePath.Text,DumpOptions,cancellation.Token);
            WasCompleted = true;
            Feedback.Text = restore ? "Restore completed successfully." : "Backup completed successfully.\n" + Path.GetFullPath(ArchivePath.Text);
        }
        catch (OperationCanceledException) { Feedback.Text = globals && restore?"Global restore canceled. Earlier statements may already be committed. Inspect the destination before retrying.":restore ? "Restore canceled. Check the destination before retrying: cancellation can arrive after the server commits." : "Backup canceled. No destination was published."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException) { Feedback.Text = ex.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            RunButton.IsEnabled = ArchivePath.IsEnabled = ToolsPath.IsEnabled = BrowseButton.IsEnabled = ToolsButton.IsEnabled = true;
            RestoreScopeSelector.IsEnabled=RestoreOwner.IsEnabled=RestoreAcl.IsEnabled=true;
            LargeObjectsSelector.IsEnabled=CompressionSelector.IsEnabled=CompressionLevelSelector.IsEnabled=JobsSelector.IsEnabled=FormatSelector.IsEnabled=ScopeSelector.IsEnabled=SqlDumpOwner.IsEnabled=SqlDumpAcl.IsEnabled=true;
            if(globals)FormatSelector.IsEnabled=false;
            LimitSchema.IsEnabled=SelectionKindSelector.IsEnabled=TableFilterName.IsEnabled=true;SchemaFilterName.IsEnabled=LimitSchema.IsChecked==true;
            GlobalReviewed.IsEnabled=true;if(globals && restore)GlobalReviewed.IsChecked=false;
            CloseButton.Content = "Close"; Progress.Visibility = Visibility.Collapsed;
        }
    }
    private void CloseOrCancel(object sender, RoutedEventArgs e) { if (cancellation is not null) cancellation.Cancel(); else Close(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (cancellation is not null) { e.Cancel = true; Feedback.Text = "Cancel the operation before closing this window."; } }
}
