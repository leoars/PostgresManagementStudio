using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Window ▸ Show Results Pane, Split Tab and Next/Previous Pane for a query (PGMS-069, M1092).</summary>
public partial class QueryView
{
    private GridLength? hiddenResultsHeight;
    private Grid? splitGrid;

    /// <summary>The second view of the document shown by Split Tab, or null.</summary>
    public SqlTextEditor? SplitEditor { get; private set; }
    public bool IsResultsPaneVisible => OutputTabs.Visibility == Visibility.Visible;
    public bool IsSplit => SplitEditor is not null;

    /// <summary>Ctrl+R: hides or shows the Results/Messages area; running a query shows it again, as in SSMS.</summary>
    public void SetResultsPaneVisible(bool visible)
    {
        if (visible == IsResultsPaneVisible || OutputTabs.Parent is not Grid grid) return;
        var row = grid.RowDefinitions[2];
        var splitter = grid.Children.OfType<GridSplitter>().FirstOrDefault();
        if (visible)
        {
            row.MinHeight = 200; row.Height = hiddenResultsHeight ?? new GridLength(2, GridUnitType.Star);
            grid.RowDefinitions[1].Height = new GridLength(5);
            OutputTabs.Visibility = Visibility.Visible;
            if (splitter is not null) splitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (OutputTabs.IsKeyboardFocusWithin) Editor.Focus();
            hiddenResultsHeight = row.Height;
            row.MinHeight = 0; row.Height = new GridLength(0);
            grid.RowDefinitions[1].Height = new GridLength(0);
            OutputTabs.Visibility = Visibility.Collapsed;
            if (splitter is not null) splitter.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Split Tab: a second view of the same document above the editor; edits in either show in both. Unsplit removes it.</summary>
    public void SetSplit(bool split)
    {
        if (split == IsSplit || (split ? Editor.Parent : splitGrid?.Parent) is not DockPanel host) return;
        if (split)
        {
            var view = new SqlTextEditor
            {
                Document = Editor.Document, SyntaxHighlighting = Editor.SyntaxHighlighting, FontFamily = Editor.FontFamily, FontSize = Editor.FontSize,
                ShowLineNumbers = Editor.ShowLineNumbers, Padding = Editor.Padding, WordWrap = Editor.WordWrap, IsReadOnly = Editor.IsReadOnly,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            ApplyEditorAppearance(view, appearance, Editor.FontSize);
            view.Options.ShowSpaces = Editor.Options.ShowSpaces; view.Options.ShowTabs = Editor.Options.ShowTabs;
            System.Windows.Automation.AutomationProperties.SetName(view, "SQL editor (split)");
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 });
            host.Children.Remove(Editor);
            Grid.SetRow(view, 0); Grid.SetRow(Editor, 2);
            var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.LightSteelBlue };
            Grid.SetRow(splitter, 1);
            grid.Children.Add(view); grid.Children.Add(splitter); grid.Children.Add(Editor);
            host.Children.Add(grid);
            view.ScrollToVerticalOffset(Editor.VerticalOffset);
            splitGrid = grid; SplitEditor = view;
        }
        else
        {
            bool focusMain = SplitEditor!.IsKeyboardFocusWithin;
            splitGrid!.Children.Remove(Editor);
            host.Children.Remove(splitGrid);
            Grid.SetRow(Editor, 0);
            host.Children.Add(Editor);
            SplitEditor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
            SplitEditor = null; splitGrid = null;
            if (focusMain) Editor.Focus();
        }
    }

    /// <summary>The panes F6 cycles through: the split view, the editor and the results area when shown.</summary>
    private IEnumerable<FrameworkElement> Panes()
    {
        if (SplitEditor is { } split) yield return split;
        yield return Editor;
        if (IsResultsPaneVisible && OutputTabs.SelectedContent is FrameworkElement results) yield return results;
    }

    /// <summary>F6 / Shift+F6: moves the keyboard focus to the next or previous pane; false when there is only one.</summary>
    public bool MovePane(bool forward)
    {
        var panes = Panes().ToList();
        if (panes.Count < 2) return false;
        int current = panes.FindIndex(p => p.IsKeyboardFocusWithin);
        int next = current < 0 ? 0 : (current + (forward ? 1 : panes.Count - 1)) % panes.Count;
        var target = panes[next];
        if (target is ICSharpCode.AvalonEdit.TextEditor editor) { editor.TextArea.Focus(); return true; }
        // The results area: its first focusable control (grid, text, messages).
        if (FirstFocusable(target) is { } first) first.Focus();
        if (!target.IsKeyboardFocusWithin) OutputTabs.Focus();
        return true;
    }

    private static UIElement? FirstFocusable(DependencyObject root)
    {
        // Prefer the content (a grid, the text results, the messages) over the export buttons around it.
        UIElement? fallback = null;
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is Control { Focusable: true, IsVisible: true, IsEnabled: true } control)
            {
                if (control is DataGrid or System.Windows.Controls.Primitives.TextBoxBase or ItemsControl and not ComboBox and not Menu) return control;
                fallback ??= control;
            }
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
        return fallback;
    }
}
