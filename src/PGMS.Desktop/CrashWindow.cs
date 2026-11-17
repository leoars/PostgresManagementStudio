using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PGMS.Desktop;

/// <summary>
/// The dialog shown after an unexpected error on the interface thread (PGMS-081): what happened, where the log is, the
/// details to copy, a link to report the issue, and the choice between continuing and closing PGMS. Continue is the default
/// because open queries keep their text; Close PGMS goes through the normal closing, which still offers to save files.
/// </summary>
internal sealed class CrashWindow : Window
{
    internal const string IssuesAddress = "https://github.com/leoars/PostgresManagementStudio/issues";

    private readonly string? logFile;
    internal TextBlock Summary { get; }
    internal TextBlock LogLocation { get; }
    internal TextBox Details { get; }
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    internal Button ContinueButton { get; }
    internal Button CloseButton { get; }
    internal Button CopyButton { get; }
    internal Button OpenLogButton { get; }
    internal Button ReportButton { get; }
    internal CrashReporter.Choice Choice { get; private set; } = CrashReporter.Choice.Continue;

    /// <summary>Opens a web address or a folder; replaced by tests.</summary>
    internal Action<string> OpenTarget { get; set; } = AboutWindow.Launch;

    /// <summary>Puts text on the clipboard; replaced by tests.</summary>
    internal Action<string> CopyText { get; set; } = Clipboard.SetText;

    internal CrashWindow(Exception error, string? logFile)
    {
        this.logFile = logFile;
        Title = "PGMS - Unexpected Error";
        Width = 620; Height = 460; MinWidth = 440; MinHeight = 320;
        ShowInTaskbar = true; ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty, "Pgms.DialogBackground");
        SetResourceReference(ForegroundProperty, "Pgms.PrimaryText");

        var heading = new TextBlock { Text = "PGMS hit an unexpected error.", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) };
        Summary = new TextBlock { Text = $"{error.GetType().Name}: {CrashReporter.Mask(error.Message)}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        LogLocation = new TextBlock
        {
            Text = logFile is null ? "The details could not be saved to a log file. Copy them below before closing this window." : $"The details were saved to {logFile}.",
            TextWrapping = TextWrapping.Wrap,
        };
        var advice = new TextBlock
        {
            Text = "You can continue working; your open queries keep their text. If PGMS behaves oddly, save your work and close it. When reporting the issue, attach the log and say what you were doing.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8),
        };
        Details = new TextBox
        {
            Text = CrashReporter.Report("Unhandled error on the interface thread", error, DateTimeOffset.Now),
            IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(Details, "Error details");

        CopyButton = MakeButton("_Copy Details", (_, _) => Copy());
        OpenLogButton = MakeButton("Open _Log Folder", (_, _) => Open(Path.GetDirectoryName(this.logFile!)!));
        OpenLogButton.IsEnabled = logFile is not null;
        ReportButton = MakeButton("_Report Issue", (_, _) => Open(IssuesAddress));
        ContinueButton = MakeButton("C_ontinue", (_, _) => Finish(CrashReporter.Choice.Continue));
        ContinueButton.IsDefault = true; ContinueButton.IsCancel = true;
        CloseButton = MakeButton("Close _PGMS", (_, _) => Finish(CrashReporter.Choice.Close));

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        tools.Children.Add(CopyButton); tools.Children.Add(OpenLogButton); tools.Children.Add(ReportButton);
        var choices = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        choices.Children.Add(ContinueButton); choices.Children.Add(CloseButton);

        var top = new StackPanel();
        top.Children.Add(heading); top.Children.Add(Summary); top.Children.Add(LogLocation); top.Children.Add(advice);
        var bottom = new StackPanel();
        bottom.Children.Add(tools); bottom.Children.Add(Status); bottom.Children.Add(choices);
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(Details);
        Content = root;
    }

    /// <summary>Shows the dialog over the active window and returns the user's choice.</summary>
    internal static CrashReporter.Choice Ask(Exception error, string? logFile)
    {
        var window = new CrashWindow(error, logFile);
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && owner != window) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Choice;
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
        button.Click += click;
        return button;
    }

    private void Finish(CrashReporter.Choice choice)
    {
        Choice = choice;
        Close();
    }

    private void Copy()
    {
        try { CopyText(Details.Text); Status.Text = "The details were copied."; }
        catch (Exception ex) when (ex is COMException or ExternalException) { Status.Text = "Could not copy: " + ex.Message; }
    }

    private void Open(string target)
    {
        try { Status.Text = ""; OpenTarget(target); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException) { Status.Text = "Could not open it: " + ex.Message; }
    }
}
