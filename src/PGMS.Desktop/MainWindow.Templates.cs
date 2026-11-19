using System.IO;
using System.Windows;
using System.Windows.Input;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>Template Explorer, Specify Values for Template Parameters, Insert Snippet and Surround With (PGMS-065, M1083).</summary>
public partial class MainWindow
{
    private const string TemplateExplorerId = "templates";
    public static RoutedUICommand SpecifyTemplateParametersCommand { get; } = new("Specify Values for Template Parameters", "SpecifyTemplateParameters", typeof(MainWindow), [new KeyGesture(Key.M, ModifierKeys.Control | ModifierKeys.Shift)]);
    private TemplateExplorerView? templateExplorer;
    /// <summary>Folder of the user's templates; tests point it at a temporary folder.</summary>
    internal string? TemplatesDirectory { get; set; }
    /// <summary>Asks the template parameter values; replaced by tests. Null means Cancel.</summary>
    internal Func<IReadOnlyList<SqlTemplateParameter>, IReadOnlyDictionary<string, string>?>? TemplateParametersPrompt { get; set; }

    internal TemplateExplorerView TemplateExplorer => templateExplorer ??= new TemplateExplorerView(TemplatesDirectory)
    {
        OpenTemplate = t => _ = OpenTemplateAsync(t),
        OpenFolder = path => OpenOutside(path, "Templates folder opened")
    };

    private void ConfigureTemplateCommands()
    {
        CommandBindings.Add(new CommandBinding(SpecifyTemplateParametersCommand, (_, _) => SpecifyTemplateParameters(), (_, e) => e.CanExecute = ActiveQuery is { Editor.IsReadOnly: false }));
    }

    private void ShowTemplateExplorer(object sender, RoutedEventArgs e) => ShowTemplateExplorer();
    /// <summary>Docks Template Explorer on the right, as SSMS does, and focuses it.</summary>
    internal void ShowTemplateExplorer()
    {
        var anchorable = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == TemplateExplorerId);
        if (anchorable is null)
        {
            anchorable = new LayoutAnchorable { Title = "Template Explorer", ContentId = TemplateExplorerId, Content = TemplateExplorer, CanClose = false, CanHide = true, FloatingWidth = 280 };
            anchorable.AddToLayout(DockManager, AnchorableShowStrategy.Right);
        }
        else if (anchorable.Content is null) anchorable.Content = TemplateExplorer;
        TemplateExplorer.Reload();
        anchorable.Show(); anchorable.IsActive = true;
        SizeNewPane(anchorable);
        TemplateExplorer.FocusTarget.Focus();
        FocusToolContent(TemplateExplorer, () => TemplateExplorer.FocusTarget);
    }

    /// <summary>A new query tab with the template text, connected like New Query (to the active query's or the Object Explorer server).</summary>
    internal async Task<QueryView> OpenTemplateAsync(SqlTemplate template)
    {
        string? connection = ActiveQuery?.ConnectionString ?? SelectedConnection;
        var query = NewQuery(template.Text);
        Status.Text = $"Template {template.Name} opened; Ctrl+Shift+M fills its parameters.";
        if (connection is null) return query;
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        return query;
    }

    internal int? SpecifyTemplateParameters()
    {
        if (ActiveQuery is not { } query) return null;
        return query.SpecifyTemplateParameters(TemplateParametersPrompt ?? (parameters =>
        {
            var dialog = new TemplateParametersDialog(parameters) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Values : null;
        }));
    }

    private void SpecifyTemplateParametersClicked(object sender, RoutedEventArgs e) => SpecifyTemplateParameters();
    private void InsertSnippetClicked(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) { query.Editor.Focus(); query.ShowSnippetPicker(false); } }
    private void SurroundWithClicked(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) { query.Editor.Focus(); query.ShowSnippetPicker(true); } }
}
