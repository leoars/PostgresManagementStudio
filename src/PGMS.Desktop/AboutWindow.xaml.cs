using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace PGMS.Desktop;

public partial class AboutWindow : Window
{
    private const int MaxFolderLevels = 6;

    /// <summary>Opens a web address or a local file; replaced by tests.</summary>
    internal Action<string> OpenTarget { get; set; } = Launch;

    public AboutWindow()
    {
        InitializeComponent();
        ProductVersion.Text = VersionText(typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    /// <summary>A build without a version set by the release process reports the SDK's default.</summary>
    internal static string VersionText(string? informational)
    {
        string version = informational?.Split('+')[0] ?? "";
        return version.Length == 0 || version == "1.0.0" ? "Development build" : "Version " + version;
    }

    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        Open(e.Uri.AbsoluteUri);
    }

    private void OpenDocument(object sender, RoutedEventArgs e)
    {
        string name = (string)((Hyperlink)sender).Tag;
        if (FindDocument(name) is { } path) Open(path);
        else LinkStatus.Text = $"{name} was not found next to PGMS.";
    }

    private void Open(string target)
    {
        try { LinkStatus.Text = ""; OpenTarget(target); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException) { LinkStatus.Text = "Could not open it: " + ex.Message; }
    }

    private static void Launch(string target)
    {
        var start = File.Exists(target) ? new ProcessStartInfo("notepad.exe") { ArgumentList = { target } } : new ProcessStartInfo(target) { UseShellExecute = true };
        Process.Start(start)?.Dispose();
    }

    /// <summary>Packages keep these files next to the program; a build run from the repository finds them at its root.</summary>
    internal static string? FindDocument(string name)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        for (int level = 0; folder is not null && level <= MaxFolderLevels; level++, folder = folder.Parent)
        {
            string path = Path.Combine(folder.FullName, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void CloseAbout(object sender, RoutedEventArgs e) => Close();
}
