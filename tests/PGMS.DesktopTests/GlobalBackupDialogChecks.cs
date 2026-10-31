using System.IO;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;
using PGMS.PostgreSql;

internal static class GlobalBackupDialogChecks
{
    private static T Find<T>(Window window,string name)where T:FrameworkElement=>(T)window.FindName(name);
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        string suffix=Guid.NewGuid().ToString("N"),role="global_ui_"+suffix;
        string root=Path.GetFullPath(Path.Combine("artifacts/global-backup-ui",suffix));Directory.CreateDirectory(root);
        string tools=Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN")!,script=Path.Combine(root,"reviewed.sql");
        await File.WriteAllTextAsync(script,$"CREATE ROLE {SqlIdentifier.Quote(role)};");
        var dump=new BackupDialog(false,connection,true){ShowInTaskbar=false};var restore=new BackupDialog(true,connection,true){ShowInTaskbar=false};
        async Task<object?> Sql(string sql){await using var c=new NpgsqlConnection(connection);await c.OpenAsync();await using var command=new NpgsqlCommand(sql,c);return await command.ExecuteScalarAsync();}
        try
        {
            dump.Show();restore.Show();
            check(dump.Title=="Back Up Global Objects" && restore.Title=="Restore Global Objects","Global dialogs identify server-wide operations");
            check(!Find<ComboBox>(dump,"FormatSelector").IsEnabled && Find<StackPanel>(dump,"BackupScopeFields").Visibility==Visibility.Collapsed,"Global backup fixes SQL format and hides database content options");
            check(Find<TextBox>(dump,"OptionsPreview").Text.Contains("--no-role-passwords") && Find<TextBlock>(dump,"Description").Text.Contains("passwords are omitted"),"Global backup exposes password omission and exact tool scope");
            check(!Find<TextBox>(restore,"OptionsPreview").Text.Contains("--single-transaction") && Find<TextBlock>(restore,"FormatHelp").Text.Contains("remain committed"),"Global restore explains non-atomic execution before running");
            Find<TextBox>(dump,"ToolsPath").Text=tools;Find<TextBox>(dump,"ArchivePath").Text=Path.Combine(root,"globals.sql");
            await BackupFormatDialogChecks.Capture(dump,"m840-global-backup-normal",false,false,check);
            await dump.RunAsync();check(dump.WasCompleted && File.Exists(Path.Combine(root,"globals.sql")),"Global WPF backup runs pg_dumpall and publishes SQL");
            await dump.RunAsync();check(!dump.WasCompleted && Find<TextBox>(dump,"Feedback").Text.Contains("never overwritten"),"Global WPF retry preserves existing output and reports failure");
            Find<TextBox>(restore,"ToolsPath").Text=tools;Find<TextBox>(restore,"ArchivePath").Text=script;
            await restore.RunAsync();check(!restore.WasCompleted && Find<TextBox>(restore,"Feedback").Text.Contains("review checkbox"),"Global restore refuses execution before explicit script review");
            Find<CheckBox>(restore,"GlobalReviewed").IsChecked=true;
            await SequenceConfirmationChecks.RespondAsync(restore,restore.RunAsync,false,check);
            check(await Sql($"SELECT NOT EXISTS(SELECT FROM pg_roles WHERE rolname='{role}')") is true,"No confirmation does not create a global role");
            await BackupFormatDialogChecks.Capture(restore,"m840-global-restore-normal",false,false,check);
            await BackupFormatDialogChecks.Capture(restore,"m840-global-restore-small",true,true,check);
            await SequenceConfirmationChecks.RespondAsync(restore,restore.RunAsync,true,check);
            check(restore.WasCompleted && await Sql($"SELECT EXISTS(SELECT FROM pg_roles WHERE rolname='{role}')") is true,"Confirmed global WPF restore executes reviewed role SQL");
            check(Find<CheckBox>(restore,"GlobalReviewed").IsChecked==false && !Find<ComboBox>(restore,"FormatSelector").IsEnabled,"Global restore clears review and retains fixed format after completion");
            Find<CheckBox>(restore,"GlobalReviewed").IsChecked=true;Find<TextBox>(restore,"ArchivePath").Text=script+".changed";
            check(Find<CheckBox>(restore,"GlobalReviewed").IsChecked==false,"Changing global script path invalidates previous review");
            Find<TextBox>(restore,"ArchivePath").Text=script;Find<CheckBox>(restore,"GlobalReviewed").IsChecked=true;
            await SequenceConfirmationChecks.RespondAsync(restore,restore.RunAsync,true,check);
            check(!restore.WasCompleted && Find<TextBox>(restore,"Feedback").Text.Contains("Earlier statements may already be committed"),"Global restore conflict reports partial-execution risk and does not report success");
            string cancelScript=Path.Combine(root,"cancel.sql");await File.WriteAllTextAsync(cancelScript,"SELECT pg_sleep(30);");
            Find<TextBox>(restore,"ArchivePath").Text=cancelScript;Find<CheckBox>(restore,"GlobalReviewed").IsChecked=true;
            Task pending=SequenceConfirmationChecks.RespondAsync(restore,restore.RunAsync,true,check);
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(8);
                while(DateTime.UtcNow<deadline && (string)Find<Button>(restore,"CloseButton").Content!="Cancel")await Task.Delay(20);
                check(!Find<TextBox>(restore,"ArchivePath").IsEnabled && !Find<CheckBox>(restore,"GlobalReviewed").IsEnabled,"Running global restore freezes file and review controls");
                restore.Close();check(restore.IsVisible,"Closing during global restore preserves window until cancellation");
            }
            finally {Find<Button>(restore,"CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await pending;}
            check(!restore.WasCompleted && Find<TextBox>(restore,"ArchivePath").IsEnabled && Find<TextBox>(restore,"Feedback").Text.Contains("Earlier statements may already be committed"),"Global restore cancellation releases controls with honest partial-result feedback");
        }
        finally{dump.Close();restore.Close();await Sql($"DROP ROLE IF EXISTS {SqlIdentifier.Quote(role)}");}
    }
}
