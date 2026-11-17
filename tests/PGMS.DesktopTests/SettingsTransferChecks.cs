using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>PGMS-072 (M1105): Tools ▸ Import and Export Settings, with files in a test folder and the dialogs replaced.</summary>
internal static class SettingsTransferChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string folder = Path.GetFullPath(Path.Combine("artifacts", "settings-transfer", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var window = new MainWindow { ShowInTaskbar = false }; window.Show();
        var options = new EnvironmentOptions(EnvironmentOptions.DefaultPath);
        var original = options.Load();
        try
        {
            await Idle();
            var menu = (MenuItem)window.FindName("ImportExportSettingsMenu");
            var tools = (ItemsControl)menu.Parent;
            check(tools.Items.IndexOf(menu) == tools.Items.IndexOf(window.FindName("CustomizeMenu")) - 1 && (string)menu.Header == "_Import and Export Settings…", "Tools ▸ Import and Export Settings… comes right before Customize… (M1106; Options… follows)");

            options.Update(d => d with { SelectTopRows = 321, QueryShortcuts = new Dictionary<string, string> { ["Ctrl+3"] = "SELECT now()" }, Window = new(10, 20, 800, 600, false), RecentFiles = [@"C:\secret\file.sql"] });
            string exported = Path.Combine(folder, "mine.pgmssettings");
            window.SettingsActionPicker = () => (MainWindow.SettingsAction.Export, false);
            window.SettingsFilePicker = save => save ? exported : null;
            check(window.ImportExportSettings() && File.Exists(exported), "Export writes the file");
            string json = File.ReadAllText(exported);
            check(json.Contains("\"Format\": \"PGMS settings\"", StringComparison.Ordinal) && json.Contains("321", StringComparison.Ordinal) && json.Contains("SELECT now()", StringComparison.Ordinal) && !json.Contains("secret", StringComparison.Ordinal) && json.Contains("\"Window\": null", StringComparison.Ordinal),
                "The file has the options but not the window position nor Recent Files");

            string backup = Path.Combine(folder, "backup.pgmssettings");
            window.SettingsActionPicker = () => (MainWindow.SettingsAction.Reset, true);
            window.SettingsFilePicker = save => save ? backup : null;
            check(window.ImportExportSettings() && File.Exists(backup), "Reset saves the current settings first when asked");
            var reset = options.Load();
            check(reset.SelectTop == EnvironmentOptions.DefaultSelectTopRows && reset.QueryShortcut("Ctrl+3") is null && reset.Window is { Width: 800 } && reset.RecentFiles is [@"C:\secret\file.sql"], "Reset brings the defaults back and keeps the window position and Recent Files");

            window.SettingsActionPicker = () => (MainWindow.SettingsAction.Import, false);
            window.SettingsFilePicker = save => save ? null : exported;
            check(window.ImportExportSettings(), "Import reads the file");
            var imported = options.Load();
            check(imported.SelectTop == 321 && imported.QueryShortcut("Ctrl+3") == "SELECT now()" && imported.Window is { Width: 800 } && imported.RecentFiles is [@"C:\secret\file.sql"], "Import restores the options and keeps this machine's window and Recent Files");

            string bad = Path.Combine(folder, "bad.pgmssettings");
            File.WriteAllText(bad, "{\"Format\":\"something else\",\"Version\":1}");
            window.SettingsFilePicker = save => save ? null : bad;
            check(!window.ImportExportSettings() && ((TextBlock)window.FindName("Status")).Text.StartsWith("Import and Export Settings failed: The file is not a PGMS settings file", StringComparison.Ordinal) && options.Load().SelectTop == 321,
                "A file that is not a settings file is refused and changes nothing");
            File.WriteAllText(bad, "not json");
            check(!window.ImportExportSettings() && options.Load().SelectTop == 321, "Invalid JSON is refused too");

            window.SettingsActionPicker = () => (MainWindow.SettingsAction.Import, true);
            window.SettingsFilePicker = save => save ? null : exported;
            check(!window.ImportExportSettings(), "Canceling the backup file cancels the import");
            window.SettingsActionPicker = () => null;
            check(!window.ImportExportSettings(), "Canceling the dialog does nothing");
        }
        finally
        {
            options.Update(_ => original);
            window.Close();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
