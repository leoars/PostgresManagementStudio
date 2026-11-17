using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Import and Export Settings (PGMS-072, M1105): the Tools ▸ Options settings (environment.json) written to or read from a
/// .pgmssettings file, or reset to the defaults. The window position and the Recent Files list stay with the machine and are never exported;
/// connections, passwords and AI settings live in other files and are not part of it. External Tools (M1106) also stay with the machine: they
/// name programs of this computer, and a file from someone else must not add commands that run them.</summary>
public static class SettingsTransfer
{
    public const string FormatName = "PGMS settings", Extension = ".pgmssettings";
    public sealed record SettingsFile(string Format, int Version, DateTime ExportedUtc, EnvironmentOptions.Document Settings);

    private static EnvironmentOptions.Document Portable(EnvironmentOptions.Document document) => document with { Window = null, RecentFiles = null, ExternalTools = null };

    public static void Export(EnvironmentOptions options, string path)
    {
        var file = new SettingsFile(FormatName, 1, DateTime.UtcNow, Portable(options.Load()));
        File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>Reads and checks a settings file; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    public static EnvironmentOptions.Document Read(string path)
    {
        SettingsFile? file;
        try { file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException("The file is not a PGMS settings file: " + ex.Message, ex); }
        if (file is not { Format: FormatName, Version: 1, Settings: { Version: 1 } settings }) throw new InvalidDataException("The file is not a PGMS settings file of a version this PGMS reads.");
        return Portable(settings);
    }

    /// <summary>Replaces the Options settings with <paramref name="settings"/>, keeping this machine's window position and Recent Files.</summary>
    public static void Apply(EnvironmentOptions options, EnvironmentOptions.Document settings) =>
        options.Update(current => Portable(settings) with { Window = current.Window, RecentFiles = current.RecentFiles, ExternalTools = current.ExternalTools });

    public static void Reset(EnvironmentOptions options) => Apply(options, new EnvironmentOptions.Document());
}

public partial class MainWindow
{
    public enum SettingsAction { Export, Import, Reset }

    /// <summary>Replaceable in tests: the file to export to (save) or import from (open), or null when canceled.</summary>
    internal Func<bool, string?> SettingsFilePicker { get; set; } = save =>
    {
        Microsoft.Win32.FileDialog dialog = save ? new Microsoft.Win32.SaveFileDialog { FileName = "PGMS" + SettingsTransfer.Extension } : new Microsoft.Win32.OpenFileDialog();
        dialog.Filter = "PGMS settings (*.pgmssettings)|*.pgmssettings|All files (*.*)|*.*"; dialog.DefaultExt = SettingsTransfer.Extension;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };
    /// <summary>Replaceable in tests: the choice of the dialog (action, and whether to save the current settings first), or null when canceled.</summary>
    internal Func<(SettingsAction Action, bool SaveCurrent)?>? SettingsActionPicker { get; set; }

    private void ImportExportSettings(object sender, RoutedEventArgs e) => ImportExportSettings();

    /// <summary>The SSMS wizard in one dialog: Export, Import or Reset; before Import and Reset the current settings can be saved to a file first.
    /// Returns true when something was written.</summary>
    internal bool ImportExportSettings()
    {
        if ((SettingsActionPicker is { } pick ? pick() : AskSettingsAction()) is not { } choice) return false;
        try
        {
            if (choice.Action == SettingsAction.Export)
            {
                if (SettingsFilePicker(true) is not { } target) return false;
                SettingsTransfer.Export(EnvironmentSettings, target);
                Status.Text = "Settings exported to " + target + ". The window position and Recent Files are not included.";
                return true;
            }
            EnvironmentOptions.Document? imported = null;
            if (choice.Action == SettingsAction.Import)
            {
                if (SettingsFilePicker(false) is not { } source) return false;
                imported = SettingsTransfer.Read(source);
            }
            if (choice.SaveCurrent)
            {
                if (SettingsFilePicker(true) is not { } backup) return false;
                SettingsTransfer.Export(EnvironmentSettings, backup);
            }
            if (imported is not null) SettingsTransfer.Apply(EnvironmentSettings, imported); else SettingsTransfer.Reset(EnvironmentSettings);
            ApplyFontsAndColors(); ApplyCustomization();
            Status.Text = imported is not null ? "Settings imported. New query tabs use them; fonts and colors changed at once." : "All settings were reset to the defaults.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Status.Text = "Import and Export Settings failed: " + ex.Message; return false; }
    }

    private (SettingsAction Action, bool SaveCurrent)? AskSettingsAction()
    {
        var export = new RadioButton { Content = "_Export selected environment settings", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var import = new RadioButton { Content = "_Import selected environment settings", Margin = new Thickness(0, 4, 0, 4) };
        var reset = new RadioButton { Content = "_Reset all settings", Margin = new Thickness(0, 4, 0, 4) };
        var save = new CheckBox { Content = "_Save my current settings to a file first", IsChecked = true, Margin = new Thickness(20, 8, 0, 0), IsEnabled = false };
        import.Checked += (_, _) => save.IsEnabled = true; reset.Checked += (_, _) => save.IsEnabled = true; export.Checked += (_, _) => save.IsEnabled = false;
        var ok = new Button { Content = "_Next >", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var dialog = new Window
        {
            Title = "Import and Export Settings Wizard", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16), MinWidth = 420,
                Children =
                {
                    new TextBlock { Text = "What do you want to do?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) },
                    export, import, reset, save,
                    new TextBlock { Text = "Settings are the Tools ▸ Options pages. Connections, passwords, AI settings, the window position and Recent Files are not included.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 12, 0, 12), Foreground = SystemColors.GrayTextBrush },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
                }
            }
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return null;
        return (export.IsChecked == true ? SettingsAction.Export : import.IsChecked == true ? SettingsAction.Import : SettingsAction.Reset, save.IsChecked == true);
    }
}
