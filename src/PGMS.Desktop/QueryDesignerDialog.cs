using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Design Query in Editor (Ctrl+Shift+Q), the modal Query Designer of SSMS 22.11 (docs/MARCO-M1047.md): three panes and OK/Cancel. It only writes text for the editor; nothing runs.</summary>
public sealed class QueryDesignerDialog : Window
{
    private readonly string? initial;
    private readonly TaskCompletionSource ready = new();
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Button okButton = new() { Content = "OK", Width = 76, Margin = new Thickness(0, 0, 8, 0) }, cancelButton = new() { Content = "Cancel", Width = 76 };

    /// <summary>The designer's SQL after OK. Empty when no table was added, so the editor is left alone.</summary>
    public string ResultSql { get; private set; } = "";
    public QueryDesignerControl Designer { get; }
    /// <summary>Done when the SQL has been read into the panes.</summary>
    public Task Ready => ready.Task;
    public string FeedbackText => feedback.Text;
    internal bool AddTableOnOpen { get; set; } = true;
    internal Button OkButton => okButton;

    /// <param name="sql">The selected statement, or null to start empty with Add Table.</param>
    public QueryDesignerDialog(string connection, string? sql)
    {
        initial = sql;
        Title = "Query Designer";
        Width = 645; Height = 680; MinWidth = 420; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
        Designer = new QueryDesignerControl(connection, queryDialog: true);
        Designer.Feedback += text => feedback.Text = text;
        var bar = new DockPanel { Background = System.Windows.SystemColors.ControlBrush, LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 14, 8) };
        buttons.Children.Add(okButton); buttons.Children.Add(cancelButton);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons); bar.Children.Add(feedback);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(Designer);
        Content = root;
        okButton.Click += async (_, _) => await AcceptAsync();
        cancelButton.Click += (_, _) => DialogResult = false;
        Loaded += async (_, _) => await FirstLoadAsync();
        Closed += (_, _) => Designer.Shutdown();
    }

    private async Task FirstLoadAsync()
    {
        try
        {
            await Designer.LoadAsync(initial ?? "");
            if (initial is null && AddTableOnOpen) Designer.ShowAddTable();
        }
        finally { ready.TrySetResult(); }
    }

    /// <summary>OK: what is pending in the panes is taken first. Refusing the SQL in Query Definitions Differ keeps the dialog open.</summary>
    public async Task<bool> AcceptAsync()
    {
        await Designer.LeaveSqlFirstAsync();
        if (Designer.SqlPutBack) return false;
        ResultSql = Designer.IsRepresentable && Designer.Sources.Count == 0 ? "" : Designer.Sql;
        if (IsVisible && DialogHost) DialogResult = true;
        return true;
    }

    /// <summary>False when the dialog is driven without ShowDialog, by the tests.</summary>
    internal bool DialogHost { get; set; } = true;
}

/// <summary>Choose Target Table for Insert Results, as in SSMS 22.11.</summary>
internal static class ChooseTargetDialog
{
    public static DiagramTableRef? Show(Window? owner, IReadOnlyList<DiagramTableRef> relations)
    {
        var list = new ListBox { Height = 110, DisplayMemberPath = nameof(DiagramTableRef.DisplayName), ItemsSource = relations, SelectedIndex = relations.Count > 0 ? 0 : -1 };
        AutomationProperties.SetName(list, "Choose a target");
        var ok = new Button { Content = "OK", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
        var dialog = new Window
        {
            Title = "Choose Target Table for Insert Results", Width = 430, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock { Text = "Insert Results inserts the query result into the target you supply.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) },
                    new TextBlock { Text = "Choose a target:", Margin = new Thickness(0, 0, 0, 4) },
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } }
                }
            }
        };
        ok.Click += (_, _) => dialog.DialogResult = list.SelectedItem is not null;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) dialog.DialogResult = true; };
        return dialog.ShowDialog() == true ? list.SelectedItem as DiagramTableRef : null;
    }
}
