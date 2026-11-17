using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>View ▸ Error List (Ctrl+\, E) and View ▸ Output (Ctrl+Alt+O), PGMS-079 (M1104). Error List shows the error of the last run of every
/// open query tab, with the position the server reported when there is one; double-click goes there. Output is the session log: each status message
/// of the main window and each execution, with the time. Both are docked at the bottom, as Visual Studio places them; both live only in memory.</summary>
public partial class MainWindow
{
    private const string ErrorListId = "errorlist", OutputId = "output";
    public sealed record ErrorListRow(QueryView Query, string Code, string Description, string File, int? Line, int? Column)
    {
        public string LineText => Line?.ToString(CultureInfo.InvariantCulture) ?? "";
        public string ColumnText => Column?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private DockPanel? errorListContent, outputContent;
    private DataGrid? errorGrid;
    private TextBlock? errorCount;
    private TextBox? outputText;
    private bool outputHooked;
    private const int OutputLineLimit = 5000;
    private readonly List<string> outputLines = [];

    internal IReadOnlyList<ErrorListRow> ErrorListRows => (IReadOnlyList<ErrorListRow>?)errorGrid?.ItemsSource ?? [];
    internal string ErrorListCountText => errorCount?.Text ?? "";
    internal IReadOnlyList<string> OutputLines => outputLines;
    internal TextBox OutputBox { get { _ = OutputContent; return outputText!; } }

    private object? ToolContentFor(string id) => id switch { ErrorListId => ErrorListContent, OutputId => OutputContent, _ => null };

    internal DockPanel ErrorListContent
    {
        get
        {
            if (errorListContent is not null) return errorListContent;
            errorGrid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.None
            };
            foreach (var (header, property, width) in new[] { ("Code", nameof(ErrorListRow.Code), 70.0), ("Description", nameof(ErrorListRow.Description), 0), ("File", nameof(ErrorListRow.File), 160), ("Line", nameof(ErrorListRow.LineText), 50), ("Column", nameof(ErrorListRow.ColumnText), 60) })
                errorGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width) });
            AutomationProperties.SetName(errorGrid, "Error List");
            errorGrid.MouseDoubleClick += (_, e) => { if (errorGrid.SelectedItem is ErrorListRow row) { GoToError(row); e.Handled = true; } };
            errorGrid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && errorGrid.SelectedItem is ErrorListRow row) { GoToError(row); e.Handled = true; } };
            errorCount = new TextBlock { Margin = new Thickness(4, 2, 4, 4), Text = "0 Errors" };
            AutomationProperties.SetName(errorCount, "Error count");
            DockPanel.SetDock(errorCount, Dock.Top);
            errorListContent = new DockPanel { Background = Brushes.White, Children = { errorCount, errorGrid } };
            DockManager.ActiveContentChanged += (_, _) => RefreshErrorList();
            RefreshErrorList();
            return errorListContent;
        }
    }

    internal DockPanel OutputContent
    {
        get
        {
            if (outputContent is not null) return outputContent;
            outputText = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Text = string.Join(Environment.NewLine, outputLines) };
            AutomationProperties.SetName(outputText, "Output");
            var clear = new Button { Content = "Clear All", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
            clear.Click += (_, _) => ClearOutput();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 4), Children = { new TextBlock { Text = "Show output from: PGMS", VerticalAlignment = VerticalAlignment.Center }, clear } };
            DockPanel.SetDock(bar, Dock.Top);
            outputContent = new DockPanel { Background = Brushes.White, Children = { bar, outputText } };
            return outputContent;
        }
    }

    /// <summary>Starts the Output log at window start: status messages are recorded from then on, also while the pane is closed.</summary>
    private void HookOutput()
    {
        if (outputHooked) return;
        outputHooked = true;
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(Status, (_, _) => { if (!string.IsNullOrWhiteSpace(Status.Text)) WriteOutput(Status.Text); });
    }

    /// <summary>Adds a line to Output with the time, keeping the last 5,000.</summary>
    internal void WriteOutput(string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');
        outputLines.Add(line);
        if (outputLines.Count > OutputLineLimit) outputLines.RemoveRange(0, outputLines.Count - OutputLineLimit);
        if (outputText is { } box)
        {
            if (outputLines.Count == OutputLineLimit) box.Text = string.Join(Environment.NewLine, outputLines);
            else box.AppendText((box.Text.Length == 0 ? "" : Environment.NewLine) + line);
            box.ScrollToEnd();
        }
    }

    internal void ClearOutput() { outputLines.Clear(); if (outputText is { } box) box.Clear(); }

    private void NoteExecutionForToolWindows(QueryView view)
    {
        if (view.LastExecution is { } run)
        {
            string tab = documents.TryGetValue(view, out var d) ? d.Title.TrimEnd('*') : "query";
            string rows = run.Rows is { } r ? $", {r:N0} row{(r == 1 ? "" : "s")}" : "";
            WriteOutput($"{tab}: {run.Outcome} in {run.Elapsed.TotalMilliseconds:N0} ms{rows}." + (view.LastError is { } error ? $" {(error.Code.Length > 0 ? error.Code + ": " : "")}{error.Message}" : ""));
        }
        // The position is set by the same run; read it once the run is fully noted.
        Dispatcher.BeginInvoke(RefreshErrorList, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>One row per open query tab whose last run failed.</summary>
    internal void RefreshErrorList()
    {
        if (errorGrid is null) return;
        var rows = new List<ErrorListRow>();
        foreach (var (view, document) in documents)
        {
            if (view.LastError is not { } error) continue;
            int? line = null, column = null;
            if (view.IssueRange is { } range && range.Start <= view.Editor.Document.TextLength) { var location = view.Editor.Document.GetLocation(range.Start); line = location.Line; column = location.Column; }
            rows.Add(new ErrorListRow(view, error.Code, error.Message, document.Title.TrimEnd('*'), line, column));
        }
        errorGrid.ItemsSource = rows;
        errorCount!.Text = rows.Count == 1 ? "1 Error" : $"{rows.Count:N0} Errors";
    }

    /// <summary>Activates the tab of the row and, when the server gave a position, puts the caret there.</summary>
    internal void GoToError(ErrorListRow row)
    {
        if (!documents.TryGetValue(row.Query, out var document)) { RefreshErrorList(); return; }
        document.IsActive = true;
        if (row.Query.HasIssue) row.Query.GoToIssue(); else row.Query.Editor.Focus();
    }

    private void ShowErrorList(object sender, RoutedEventArgs e) => ShowToolWindow(ErrorListId);
    private void ShowOutput(object sender, RoutedEventArgs e) => ShowToolWindow(OutputId);

    /// <summary>Docks Error List or Output at the bottom (once, as a pair) and activates it.</summary>
    internal void ShowToolWindow(string id)
    {
        var anchorable = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id);
        if (anchorable is null)
        {
            anchorable = new LayoutAnchorable { Title = id == ErrorListId ? "Error List" : "Output", ContentId = id, Content = ToolContentFor(id), CanClose = false, CanHide = true, FloatingHeight = 220 };
            var sibling = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId is ErrorListId or OutputId && a.Parent is LayoutAnchorablePane);
            if (sibling?.Parent is LayoutAnchorablePane pane) pane.Children.Add(anchorable);
            else anchorable.AddToLayout(DockManager, AnchorableShowStrategy.Bottom);
        }
        else if (anchorable.Content is null) anchorable.Content = ToolContentFor(id);
        if (id == ErrorListId) RefreshErrorList();
        anchorable.Show(); anchorable.IsActive = true;
    }

    private long toolChordDeadline;

    /// <summary>Ctrl+\, E (Error List) and Ctrl+Alt+O (Output), as in SSMS 22.11. Not while the editor waits for the second key of Ctrl+K.</summary>
    private bool HandleToolWindowShortcut(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return false;
        long now = Environment.TickCount64;
        if (toolChordDeadline > 0)
        {
            bool valid = now <= toolChordDeadline; toolChordDeadline = 0;
            if (valid && key == Key.E && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control) { ShowToolWindow(ErrorListId); e.Handled = true; return true; }
            if (key == Key.Escape) { e.Handled = true; return true; }
        }
        if (key is Key.OemBackslash or Key.Oem5 && Keyboard.Modifiers == ModifierKeys.Control && ActiveQuery?.CommentChordPending != true) { toolChordDeadline = now + 2000; e.Handled = true; return true; }
        if (key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) { ShowToolWindow(OutputId); e.Handled = true; return true; }
        return false;
    }
}
