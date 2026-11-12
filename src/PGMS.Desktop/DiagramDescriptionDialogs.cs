using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Description Property (PGMS-045), as SSMS 22.11 opens it from the "…" of Description in the Properties window (pilotagem de 11/11/2026,
/// docs/MARCO-M1062.md): "Description:", a box of several lines, OK only after a change, Cancel. PGMS adds what it edits, the 4 KiB limit and,
/// when it applies, why the description will not reach the database and the text kept in the diagram but not applied.</summary>
public sealed class DescriptionDialog : Window
{
    private readonly string? original;
    public TextBox Box { get; }
    public Button OkButton { get; }
    public TextBlock Counter { get; }
    public TextBlock Notice { get; }
    public Button UseSavedButton { get; }
    public Button DiscardButton { get; }
    /// <summary>The user chose to forget the description kept in the diagram but not applied.</summary>
    public bool DiscardUnapplied { get; private set; }
    /// <summary>The description as typed, with line breaks as \n and the ends trimmed; null removes it.</summary>
    public string? Value => Normalize(Box.Text);
    public bool Changed => Value != original;

    internal static string? Normalize(string text) { string value = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim(); return value.Length == 0 ? null : value; }

    public DescriptionDialog(string subject, string? current, string? notice, DiagramDescription? unapplied)
    {
        original = current;
        Title = "Description Property"; Width = 520; Height = 380; MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip; Background = Frozen("#F0F0F0");
        var root = new DockPanel { Margin = new Thickness(12) };
        var heading = new TextBlock { Text = subject, Foreground = Frozen("#505050"), Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetName(heading, "Object");
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var label = new TextBlock { Text = "Description:", Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(label, Dock.Top); root.Children.Add(label);

        Notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Frozen("#8A4B00"), Margin = new Thickness(0, 8, 0, 0), Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible, Text = notice ?? "" };
        AutomationProperties.SetName(Notice, "Notice");
        UseSavedButton = new Button { Content = "_Use Diagram Text", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 8, 0) };
        DiscardButton = new Button { Content = "_Discard Diagram Text", Padding = new Thickness(8, 1, 8, 1) };
        var savedPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = unapplied is null ? Visibility.Collapsed : Visibility.Visible };
        if (unapplied is not null)
        {
            var savedText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Frozen("#8A4B00"),
                Text = "Kept in the diagram but not applied in the database (only the owner of the table can apply it): " + (unapplied.Text.Length == 0 ? "remove the description." : "“" + unapplied.Text + "”")
            };
            AutomationProperties.SetName(savedText, "Not applied");
            savedPanel.Children.Add(savedText);
            savedPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { UseSavedButton, DiscardButton } });
        }
        UseSavedButton.Click += (_, _) => { Box!.Text = unapplied!.Text; Box.Focus(); Box.CaretIndex = Box.Text.Length; };
        DiscardButton.Click += (_, _) => { DiscardUnapplied = true; DiscardButton.IsEnabled = false; UseSavedButton.IsEnabled = false; Refresh(); };

        OkButton = new Button { Content = "OK", Width = 86, Height = 24, IsDefault = false, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = "Cancel", Width = 86, Height = 24, IsCancel = true };
        Counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen("#505050") };
        AutomationProperties.SetName(Counter, "Size");
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(Counter, Dock.Left); DockPanel.SetDock(cancel, Dock.Right); DockPanel.SetDock(OkButton, Dock.Right);
        buttons.Children.Add(Counter); buttons.Children.Add(cancel); buttons.Children.Add(OkButton);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        DockPanel.SetDock(savedPanel, Dock.Bottom); root.Children.Add(savedPanel);
        DockPanel.SetDock(Notice, Dock.Bottom); root.Children.Add(Notice);

        // Enter starts a new line, as in the SSMS box; OK is a click or Alt+O away.
        Box = new TextBox { Text = (current ?? "").Replace("\n", Environment.NewLine, StringComparison.Ordinal), AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(Box, "Description");
        root.Children.Add(Box);
        Content = root;
        Box.TextChanged += (_, _) => Refresh();
        OkButton.Click += (_, _) => { if (IsLoaded) DialogResult = true; };
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
        Refresh();
    }

    private void Refresh()
    {
        string? error = DiagramDescriptions.LimitError(Value);
        int bytes = Value is null ? 0 : Encoding.UTF8.GetByteCount(Value);
        Counter.Text = error ?? $"{bytes} of {DiagramDescriptions.MaxBytes} bytes";
        Counter.Foreground = error is null ? Frozen("#505050") : Frozen("#A1260D");
        OkButton.IsEnabled = error is null && (Changed || DiscardUnapplied);
    }

    internal static Brush Frozen(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}

/// <summary>Export for AI (PGMS-045): the Markdown of the diagram, to copy into a system prompt, a CLAUDE.md or an AGENTS.md, or to save as a file.
/// Not in SSMS.</summary>
public sealed class DiagramExportDialog : Window
{
    public TextBox Text { get; }
    public TextBlock Note { get; }
    public Button CopyButton { get; }
    public Button SaveButton { get; }
    internal Action<string>? ClipboardOverride { get; set; }
    internal Func<string?>? SavePathOverride { get; set; }
    public string? SavedTo { get; private set; }

    public DiagramExportDialog(string markdown, string diagramName, string? note)
    {
        Title = "Export for AI"; Width = 820; Height = 600; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip; Background = DescriptionDialog.Frozen("#F0F0F0");
        var root = new DockPanel { Margin = new Thickness(12) };
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Text = "The tables of this diagram as Markdown, read now from the catalog: columns, keys, relationships, indexes and descriptions. Paste it into a system prompt, a CLAUDE.md or an AGENTS.md, so an AI agent reads the model before writing SQL."
        };
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        Note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = DescriptionDialog.Frozen("#8A4B00"), Margin = new Thickness(0, 0, 0, 6), Text = note ?? "", Visibility = note is null ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetName(Note, "Notice");
        DockPanel.SetDock(Note, Dock.Top); root.Children.Add(Note);
        CopyButton = new Button { Content = "_Copy", Width = 86, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
        SaveButton = new Button { Content = "_Save As…", Width = 96, Height = 24 };
        var close = new Button { Content = "Close", Width = 86, Height = 24, IsCancel = true };
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(CopyButton, Dock.Left); DockPanel.SetDock(SaveButton, Dock.Left); DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(CopyButton); buttons.Children.Add(SaveButton); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        Text = new TextBox { Text = markdown.Replace("\n", Environment.NewLine, StringComparison.Ordinal), IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(Text, "Markdown");
        root.Children.Add(Text);
        Content = root;
        CopyButton.Click += (_, _) =>
        {
            if (ClipboardOverride is { } copy) copy(markdown);
            else Clipboard.SetText(markdown.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
            CopyButton.Content = "Copied";
        };
        SaveButton.Click += (_, _) =>
        {
            string? path = SavePathOverride is { } choose ? choose() : Choose(diagramName);
            if (path is null) return;
            try { File.WriteAllText(path, markdown, new UTF8Encoding(false)); SavedTo = path; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "PGMS", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
    }

    private string? Choose(string diagramName)
    {
        string file = string.Concat(diagramName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*", DefaultExt = ".md", FileName = file + ".md" };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}

/// <summary>The optional function pgms.diagram_markdown(name) (PGMS-045): the script to review, then Create (or Update) or Remove. Not in SSMS.</summary>
public sealed class MarkdownFunctionDialog : Window
{
    public enum Choice { None, Create, Remove }
    public Choice Chosen { get; private set; }
    public TextBox Script { get; }
    public TextBlock Status { get; }
    public Button CreateButton { get; }
    public Button RemoveButton { get; }

    public MarkdownFunctionDialog(bool installed, string diagramName)
    {
        Title = "AI Export Function"; Width = 820; Height = 600; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip; Background = DescriptionDialog.Frozen("#F0F0F0");
        var root = new DockPanel { Margin = new Thickness(12) };
        string call = "SELECT pgms.diagram_markdown('" + diagramName.Replace("'", "''", StringComparison.Ordinal) + "');";
        Status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = (installed ? "The function pgms.diagram_markdown is installed in this database." : "The function pgms.diagram_markdown is not installed in this database.")
                + " It is optional: an AI agent connected to the database runs " + call + " and gets the same Markdown as Export for AI, read from the catalog."
                + " It is read only, runs with the privileges of the caller (who needs SELECT on pgms.diagrams) and is created in the schema pgms. Review the script before you run it."
        };
        AutomationProperties.SetName(Status, "Status");
        DockPanel.SetDock(Status, Dock.Top); root.Children.Add(Status);
        CreateButton = new Button { Content = installed ? "_Update" : "_Create", Width = 86, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
        RemoveButton = new Button { Content = "_Remove", Width = 86, Height = 24, Margin = new Thickness(0, 0, 8, 0), IsEnabled = installed };
        var save = new Button { Content = "Save to _File…", Width = 106, Height = 24 };
        var close = new Button { Content = "Close", Width = 86, Height = 24, IsCancel = true };
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(save, Dock.Left); DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(RemoveButton, Dock.Right); DockPanel.SetDock(CreateButton, Dock.Right);
        buttons.Children.Add(save); buttons.Children.Add(close); buttons.Children.Add(RemoveButton); buttons.Children.Add(CreateButton);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        Script = new TextBox { Text = DiagramMarkdown.CreateFunctionSql.Replace("\n", Environment.NewLine, StringComparison.Ordinal), IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(Script, "Script");
        root.Children.Add(Script);
        Content = root;
        CreateButton.Click += (_, _) => { Chosen = Choice.Create; if (IsLoaded) DialogResult = true; };
        // Remove shows its script in the box first; the second click runs it.
        RemoveButton.Click += (_, _) =>
        {
            if (Script.Text != DiagramMarkdown.DropFunctionSql) { Script.Text = DiagramMarkdown.DropFunctionSql; RemoveButton.Content = "Confirm _Remove"; CreateButton.IsEnabled = false; return; }
            Chosen = Choice.Remove; if (IsLoaded) DialogResult = true;
        };
        save.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", DefaultExt = ".sql", FileName = "pgms.diagram_markdown.sql" };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, Script.Text, new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "PGMS", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
    }
}

/// <summary>Install Diagram Support with the SSMS question, the script PGMS runs and, unchecked by default, the optional function for AI agents (PGMS-045).</summary>
public sealed class InstallDiagramSupportDialog : Window
{
    public CheckBox FunctionBox { get; }
    public TextBox Script { get; }
    public Button YesButton { get; }

    public InstallDiagramSupportDialog(string question)
    {
        Title = "PGMS"; Width = 640; Height = 520; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip; Background = DescriptionDialog.Frozen("#F0F0F0");
        var root = new DockPanel { Margin = new Thickness(12) };
        var text = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(text, "Question");
        DockPanel.SetDock(text, Dock.Top); root.Children.Add(text);
        FunctionBox = new CheckBox
        {
            Margin = new Thickness(0, 0, 0, 8), IsChecked = false,
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Also create the function pgms.diagram_markdown(name), so an AI agent connected to this database can read a diagram as Markdown (optional)" }
        };
        AutomationProperties.SetName(FunctionBox, "Also create the function pgms.diagram_markdown");
        DockPanel.SetDock(FunctionBox, Dock.Top); root.Children.Add(FunctionBox);
        YesButton = new Button { Content = "_Yes", Width = 86, Height = 24, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var no = new Button { Content = "_No", Width = 86, Height = 24, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { YesButton, no } };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        Script = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(Script, "Script");
        root.Children.Add(Script);
        Content = root;
        void Show() => Script.Text = (DiagramService.InstallSql + (FunctionBox.IsChecked == true ? "\n" + DiagramMarkdown.CreateFunctionSql : "")).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        FunctionBox.Checked += (_, _) => Show(); FunctionBox.Unchecked += (_, _) => Show();
        Show();
        YesButton.Click += (_, _) => { if (IsLoaded) DialogResult = true; };
    }
}
