using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Code Snippets Manager… (Ctrl+K, Ctrl+B; PGMS-082, M1122). The SSMS 22.11 dialog (629×470, M1063) was only seen to exist;
/// the layout follows the Visual Studio one it comes from: folders and snippets on the left, the selected snippet and its location on the right,
/// Import…, Remove and the folder of the user's snippets. Only .snippet files in the user's folder can be removed; PGMS's own are built in.</summary>
public sealed class CodeSnippetsManagerDialog : Window
{
    private readonly string userDirectory;
    private readonly TreeView tree = new() { MinWidth = 230 };
    private readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock kind = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox location = new() { IsReadOnly = true, Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox code = new() { IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 8, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 120 };
    private readonly Button removeButton = new() { Content = "_Remove", MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
    private readonly TextBlock status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    /// <summary>Picks the file to import; tests replace it. Null means Cancel.</summary>
    internal Func<string?>? PickFile { get; set; }
    /// <summary>Confirms removing a file (receives its path); tests replace it.</summary>
    internal Func<string, bool>? ConfirmRemove { get; set; }
    internal TreeView Tree => tree;
    internal string StatusText => status.Text;
    internal string LocationText => location.Text;

    public CodeSnippetsManagerDialog(string? userDirectory = null)
    {
        this.userDirectory = userDirectory ?? SqlTemplateCatalog.UserSnippetsDirectory;
        Title = "Code Snippets Manager";
        Width = 629; Height = 470; MinWidth = 520; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        AutomationProperties.SetName(tree, "Snippets");
        AutomationProperties.SetName(location, "Location");
        AutomationProperties.SetName(code, "Code");
        var importButton = new Button { Content = "_Import…", MinWidth = 80 };
        var folderButton = new Button { Content = "Open _Folder", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Opens the folder of your snippets; .snippet files placed there appear in Insert Snippet and Surround With" };
        var closeButton = new Button { Content = "OK", MinWidth = 80, IsDefault = true, IsCancel = true };
        importButton.Click += (_, _) => Import();
        removeButton.Click += (_, _) => RemoveSelected();
        folderButton.Click += (_, _) => OpenFolder();
        closeButton.Click += (_, _) => Close();
        tree.SelectedItemChanged += (_, _) => ShowSelected();

        var details = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        var top = new StackPanel { Children = { title, description, kind, new TextBlock { Text = "Location:", Margin = new Thickness(0, 8, 0, 0) }, location } };
        DockPanel.SetDock(top, Dock.Top); details.Children.Add(top); details.Children.Add(code);
        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        middle.Children.Add(tree); Grid.SetColumn(details, 1); middle.Children.Add(details);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { importButton, removeButton, folderButton } };
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(closeButton, Dock.Right); bottom.Children.Add(closeButton); bottom.Children.Add(left);
        var root = new DockPanel { Margin = new Thickness(12) };
        var heading = new TextBlock { Text = "Snippets offered by Insert Snippet (Ctrl+K, Ctrl+X) and Surround With (Ctrl+K, Ctrl+S):", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom); DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(heading); root.Children.Add(bottom); root.Children.Add(status); root.Children.Add(middle);
        Content = root;
        Fill();
    }

    /// <summary>Folders in alphabetical order, each with its snippets; the user's files are marked by their location.</summary>
    internal void Fill(string? select = null)
    {
        tree.Items.Clear();
        foreach (var group in SqlTemplateCatalog.Snippets(userDirectory).GroupBy(s => s.Folder).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var folder = new TreeViewItem { Header = group.Key, IsExpanded = true };
            foreach (var snippet in group.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new TreeViewItem { Header = snippet.Title, Tag = snippet };
                AutomationProperties.SetName(item, snippet.Title);
                folder.Items.Add(item);
            }
            tree.Items.Add(folder);
        }
        if (select is not null && tree.Items.OfType<TreeViewItem>().SelectMany(f => f.Items.OfType<TreeViewItem>()).FirstOrDefault(i => string.Equals((i.Tag as SqlSnippet)?.SourceFile, select, StringComparison.OrdinalIgnoreCase)) is { } chosen)
            chosen.IsSelected = true;
        ShowSelected();
    }

    internal SqlSnippet? Selected => (tree.SelectedItem as TreeViewItem)?.Tag as SqlSnippet;

    private void ShowSelected()
    {
        var snippet = Selected;
        title.Text = snippet?.Title ?? "Select a snippet to see it.";
        description.Text = snippet?.Description ?? "";
        kind.Text = snippet is null ? "" : "Type: " + string.Join(", ", new[] { snippet.IsExpansion ? "Expansion" : null, snippet.IsSurroundsWith ? "SurroundsWith" : null }.OfType<string>());
        location.Text = snippet is null ? "" : snippet.SourceFile ?? "Built into PGMS";
        code.Text = snippet?.Code ?? "";
        removeButton.IsEnabled = snippet?.SourceFile is not null;
    }

    /// <summary>Copies a .snippet file into the user's folder (a name already there gets a number) and selects it. Returns the new path.</summary>
    internal string? Import()
    {
        string? source = PickFile is not null ? PickFile() : PickWithDialog();
        if (source is null) return null;
        try
        {
            string destination = CodeSnippetFiles.Import(source, userDirectory);
            Fill(destination);
            status.Text = $"Imported {Path.GetFileName(destination)}.";
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { status.Text = ex.Message; return null; }
    }

    private string? PickWithDialog()
    {
        var dialog = new OpenFileDialog { Title = "Import Code Snippet", Filter = "Code snippets (*.snippet)|*.snippet|All files (*.*)|*.*" };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>Deletes the .snippet file of the selected snippet from the user's folder, after confirmation; every snippet in that file goes.</summary>
    internal bool RemoveSelected()
    {
        if (Selected?.SourceFile is not { } file) return false;
        bool confirmed = ConfirmRemove?.Invoke(file) ?? LocalizedMessageBox.Show(this, $"Delete the file {file}?\n\nEvery snippet in this file is removed from Insert Snippet and Surround With.", "Code Snippets Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return false;
        try { CodeSnippetFiles.Remove(file, userDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { status.Text = ex.Message; return false; }
        Fill();
        status.Text = $"Removed {Path.GetFileName(file)}.";
        return true;
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(userDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + userDirectory + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { status.Text = ex.Message; }
    }
}

/// <summary>File operations of the Code Snippets Manager, kept apart from the window for tests.</summary>
internal static class CodeSnippetFiles
{
    internal const long MaxBytes = 1024 * 1024;

    /// <summary>Checks that the file holds at least one snippet PGMS can read, then copies it to the root of the user's folder.</summary>
    internal static string Import(string source, string userDirectory)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException($"{source} does not exist.");
        if (info.Length > MaxBytes) throw new InvalidDataException($"{info.Name} is larger than 1 MiB; it was not imported.");
        if (SqlSnippet.Load(File.ReadAllText(source), "").Count == 0) throw new InvalidDataException($"{info.Name} has no code snippet PGMS can read (CodeSnippet with Title and Code); it was not imported.");
        Directory.CreateDirectory(userDirectory);
        string name = Path.GetFileNameWithoutExtension(source), destination = Path.Combine(userDirectory, name + ".snippet");
        for (int n = 2; File.Exists(destination); n++) destination = Path.Combine(userDirectory, $"{name} ({n}).snippet");
        File.Copy(source, destination);
        return destination;
    }

    /// <summary>Deletes a snippet file, only when it is inside the user's folder.</summary>
    internal static void Remove(string file, string userDirectory)
    {
        string root = Path.GetFullPath(userDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only snippet files in your snippets folder can be removed.");
        File.Delete(file);
    }
}
