using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Find Results pane (PGMS-066, M1091), after the SSMS 22.11 one: the search line on top, matches grouped by file, the counts at the bottom.
/// Double click or Enter opens the file at the match.</summary>
public sealed class FindResultsView : UserControl
{
    public TextBlock Header { get; } = new() { Margin = new Thickness(6, 4, 6, 4), TextWrapping = TextWrapping.Wrap };
    public TreeView Tree { get; } = new() { BorderThickness = new Thickness(0) };
    public TextBlock Summary { get; } = new() { Margin = new Thickness(6, 4, 6, 4) };
    /// <summary>Opens a match; set by the main window.</summary>
    public Func<FileMatch, Task>? OpenMatch { get; set; }
    public IReadOnlyList<FileMatch> Matches { get; private set; } = [];

    public FindResultsView()
    {
        AutomationProperties.SetName(Tree, "Find Results");
        var dock = new DockPanel();
        DockPanel.SetDock(Header, Dock.Top); DockPanel.SetDock(Summary, Dock.Bottom);
        dock.Children.Add(Header); dock.Children.Add(Summary); dock.Children.Add(Tree);
        Content = dock;
        Tree.MouseDoubleClick += async (_, e) => { if (Tree.SelectedItem is TreeViewItem { Tag: FileMatch m }) { e.Handled = true; await Open(m); } };
        Tree.KeyDown += async (_, e) => { if (e.Key == Key.Enter && Tree.SelectedItem is TreeViewItem { Tag: FileMatch m }) { e.Handled = true; await Open(m); } };
    }

    private async Task Open(FileMatch match) { if (OpenMatch is { } open) await open(match); }

    /// <summary>Shows a finished search: <paramref name="header"/> describes it, the counts follow SSMS.</summary>
    public void Show(string header, IReadOnlyList<FileMatch> matches, int filesSearched, bool truncated, string? note = null)
    {
        Matches = matches;
        Header.Text = header;
        Tree.Items.Clear();
        foreach (var group in matches.GroupBy(m => (m.Path, m.DisplayName)))
        {
            var file = new TreeViewItem { Header = $"{group.Key.DisplayName} ({group.Count()})", IsExpanded = true, ToolTip = group.Key.Path.Length > 0 ? group.Key.Path : null };
            foreach (var match in group)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = $"({match.Line},{match.Column})", Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(0, 0, 8, 0), MinWidth = 64 });
                row.Children.Add(new TextBlock { Text = match.LineText.Trim(), FontFamily = new FontFamily("Consolas") });
                var item = new TreeViewItem { Header = row, Tag = match };
                AutomationProperties.SetName(item, $"{match.LineText.Trim()}. File: {match.DisplayName}. Line: {match.Line}. Col: {match.Column}");
                file.Items.Add(item);
            }
            Tree.Items.Add(file);
        }
        int files = matches.Select(m => (m.Path, m.DisplayName)).Distinct().Count();
        Summary.Text = $"Matching lines: {matches.Count}  Matching files: {files}  Total files searched: {filesSearched}" + (truncated ? $"  (stopped at {FileSearch.MaximumMatches} matches)" : "") + (note is null ? "" : "  " + note);
    }

    /// <summary>Shows the outcome of Replace All.</summary>
    public void ShowReplace(string header, IReadOnlyList<(string DisplayName, string Path, int Count)> files, int filesSearched, string? note)
    {
        Matches = [];
        Header.Text = header;
        Tree.Items.Clear();
        foreach (var (name, path, count) in files)
            Tree.Items.Add(new TreeViewItem { Header = $"{name} ({count})", ToolTip = path.Length > 0 ? path : null });
        Summary.Text = $"Total replaced: {files.Sum(f => f.Count)}  Matching files: {files.Count}  Total files searched: {filesSearched}" + (note is null ? "" : "  " + note);
    }
}
