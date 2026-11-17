using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Help ▸ View Help and Release Notes (PGMS-076, M1082): the guide shipped with the package and the release list of the project, opened outside PGMS.</summary>
public partial class MainWindow
{
    public static RoutedUICommand ViewHelpCommand { get; } = new("View Help", "ViewHelp", typeof(MainWindow), [new KeyGesture(Key.F1, ModifierKeys.Control)]);
    internal const string ReleaseNotesAddress = "https://github.com/leoars/PostgresManagementStudio/releases";
    /// <summary>Opens a file or web address; replaced by tests.</summary>
    internal Action<string> OpenExternal { get; set; } = AboutWindow.Launch;

    private void ConfigureHelpCommands()
    {
        CommandBindings.Add(new CommandBinding(ViewHelpCommand, (_, _) => ViewHelp()));
    }

    /// <summary>The guide next to the program: LEIA-ME.md first in Portuguese Windows, READ-ME.md otherwise; a build from the repository opens README.md.</summary>
    internal static string? HelpDocument(CultureInfo culture)
    {
        string[] names = culture.TwoLetterISOLanguageName == "pt" ? ["LEIA-ME.md", "READ-ME.md", "README.pt-BR.md", "README.md"] : ["READ-ME.md", "LEIA-ME.md", "README.md", "README.pt-BR.md"];
        return names.Select(AboutWindow.FindDocument).FirstOrDefault(p => p is not null);
    }

    internal bool ViewHelp()
    {
        if (HelpDocument(CultureInfo.CurrentUICulture) is not { } path) { Status.Text = "The guide was not found next to PGMS."; return false; }
        return OpenOutside(path, "Guide opened");
    }

    internal bool ShowReleaseNotes() => OpenOutside(ReleaseNotesAddress, "Release notes opened in the browser");

    private bool OpenOutside(string target, string done)
    {
        try { OpenExternal(target); Status.Text = done; return true; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException) { ShowError(new IOException("Could not open it: " + ex.Message, ex)); return false; }
    }

    private void ViewHelpClicked(object sender, RoutedEventArgs e) => ViewHelp();
    private void ReleaseNotesClicked(object sender, RoutedEventArgs e) => ShowReleaseNotes();
}
