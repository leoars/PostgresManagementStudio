using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-064 (M1100): lz4 and zstd levels in Back Up, restore jobs and Choose Items… in Restore.</summary>
internal static class RestoreChoiceChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        static T Find<T>(Window w, string name) where T : class => (T)w.FindName(name);
        string tools = Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN") ?? throw new InvalidOperationException("Missing isolated tools.");
        var dump = new BackupDialog(false, connection) { ShowInTaskbar = false }; dump.Show(); await Idle();
        BackupDialog? restore = null;
        string folder = Path.Combine(Path.GetTempPath(), "pgms-restore-choice-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var compression = Find<ComboBox>(dump, "CompressionSelector"); var level = Find<ComboBox>(dump, "CompressionLevelSelector"); var preview = Find<TextBox>(dump, "OptionsPreview");
            compression.SelectedValue = BackupCompression.Lz4; await Idle();
            check(level.Items.Count == 12 && (int)level.SelectedItem == 1 && preview.Text.Contains("--compress=lz4:1", StringComparison.Ordinal) && Find<StackPanel>(dump, "CompressionLevelFields").IsVisible, "LZ4 offers levels 1 to 12, starting at the pg_dump default 1");
            compression.SelectedValue = BackupCompression.Zstd; await Idle();
            check(level.Items.Count == 22 && (int)level.SelectedItem == 3 && preview.Text.Contains("--compress=zstd:3", StringComparison.Ordinal) && Find<TextBlock>(dump, "CompressionLevelHelp").Text.Contains("pg_dump 16", StringComparison.Ordinal), "Zstandard offers levels 1 to 22, starting at 3, and says it needs pg_dump 16");
            level.SelectedItem = 19; await Idle();
            check(preview.Text.Contains("--compress=zstd:19", StringComparison.Ordinal), "The chosen zstd level reaches pg_dump");
            compression.SelectedValue = BackupCompression.Gzip; await Idle();
            check(level.Items.Count == 9 && (int)level.SelectedItem == 6 && preview.Text.Contains("--compress=gzip:6", StringComparison.Ordinal), "Back to Gzip the levels are 1 to 9 again, at 6");
            compression.SelectedValue = BackupCompression.None; await Idle();
            check(!Find<StackPanel>(dump, "CompressionLevelFields").IsVisible && preview.Text.Contains("--compress=none", StringComparison.Ordinal), "No compression has no level");

            string archive = Path.Combine(folder, "schema.backup");
            await new BackupService(connection, tools).BackupWithOptionsAsync(archive, new(BackupFormat.Custom, BackupDataScope.SchemaOnly));
            restore = new BackupDialog(true, connection) { ShowInTaskbar = false }; restore.Show(); await Idle();
            var restorePreview = Find<TextBox>(restore, "OptionsPreview");
            check(Find<ComboBox>(restore, "RestoreJobsSelector").SelectedItem is 1 && restorePreview.Text.Contains("--single-transaction", StringComparison.Ordinal) && Find<TextBlock>(restore, "RestoreJobsHelp").Text.Contains("single transaction", StringComparison.Ordinal),
                "Restore starts with one job in a single transaction");
            Find<ComboBox>(restore, "RestoreJobsSelector").SelectedItem = 4; await Idle();
            check(restorePreview.Text.Contains("--jobs=4", StringComparison.Ordinal) && !restorePreview.Text.Contains("--single-transaction", StringComparison.Ordinal) && Find<TextBlock>(restore, "RestoreJobsHelp").Text.Contains("does not run in one transaction", StringComparison.Ordinal),
                "Four jobs drop the single transaction and the page says what that means");
            var chosenBox = Find<CheckBox>(restore, "RestoreChosenItems");
            check(!Find<Button>(restore, "ChooseItemsButton").IsEnabled, "Choose Items… is off until Restore only chosen items is checked");
            chosenBox.IsChecked = true; await Idle();
            check(Find<Button>(restore, "ChooseItemsButton").IsEnabled && !Find<Button>(restore, "RunButton").IsEnabled && restorePreview.Text.Contains("at least one item", StringComparison.Ordinal), "Checked without items, nothing can run yet");
            Find<TextBox>(restore, "ArchivePath").Text = archive; Find<TextBox>(restore, "ToolsPath").Text = tools;
            IReadOnlyList<ArchiveItem>? offered = null;
            restore.PickArchiveItems = (items, _) => { offered = items; return null; };
            await restore.ChooseItemsAsync(); await Idle();
            check(offered is { Count: > 0 } && restore.ChosenItems is null, "Choose Items… lists the archive and Cancel keeps nothing chosen: " + offered?.Count);
            restore.PickArchiveItems = (items, _) => items.Take(1).ToList();
            await restore.ChooseItemsAsync(); await Idle();
            check(restore.ChosenItems?.Count == 1 && restorePreview.Text.Contains("--use-list=", StringComparison.Ordinal) && Find<Button>(restore, "RunButton").IsEnabled && Find<TextBlock>(restore, "RestoreItemsHelp").Text.StartsWith("1 of " + offered!.Count.ToString("N0"), StringComparison.Ordinal),
                "One chosen item enables Restore with a --use-list file: " + Find<TextBlock>(restore, "RestoreItemsHelp").Text);
            Find<TextBox>(restore, "ArchivePath").Text = archive + ".other"; await Idle();
            check(restore.ChosenItems is null, "Changing the archive forgets the chosen items");
            Find<TextBox>(restore, "ArchivePath").Text = Path.Combine(folder, "missing.backup");
            restore.PickArchiveItems = (_, _) => throw new InvalidOperationException("must not be shown");
            await restore.ChooseItemsAsync(); await Idle();
            check(Find<TextBox>(restore, "Feedback").Text.Contains("could not be listed", StringComparison.Ordinal), "A missing archive is reported, not listed");

            var all = offered!;
            var list = new ArchiveItemsDialog(all, all.Take(2).ToList()) { ShowInTaskbar = false }; list.Show(); await Idle();
            check(list.Boxes.Count(b => b.IsChecked == true) == Math.Min(2, all.Count), "The items dialog reopens with the earlier choice");
            list.Filter.Text = "SCHEMA"; await Idle();
            list.SetShown(true); list.Filter.Text = ""; await Idle();
            int schemas = all.Count(i => i.Description.Contains("SCHEMA", StringComparison.OrdinalIgnoreCase));
            check(list.Boxes.Count(b => b.IsChecked == true) >= Math.Min(2, all.Count) && list.Boxes.Count(b => b.Visibility == Visibility.Visible) == all.Count, "Select All with a filter checks only the items shown: " + schemas);
            list.Filter.Text = ""; list.SetShown(false); list.Accept(); await Idle();
            check(list.IsVisible && list.Chosen.Count == 0, "OK with nothing chosen keeps the dialog open");
            list.Boxes.First().IsChecked = true; list.Accept(); await Idle();
            check(!list.IsVisible && list.Chosen.Count == 1, "OK returns the checked items");
        }
        finally
        {
            dump.Close(); restore?.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
