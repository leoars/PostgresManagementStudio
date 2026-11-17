using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Edit ▸ Go To ▸ Go To Last Edit Location (Ctrl+Shift+Backspace; PGMS-078, M1103): the tab edited last, at the place of the change.</summary>
public partial class MainWindow
{
    private void ConfigureEditorConveniences()
    {
        CommandBindings.Add(new CommandBinding(SqlEditorCommands.GoToLastEditLocation, (_, e) => { GoToLastEditLocation(); e.Handled = true; }, (_, e) => { e.CanExecute = LastEditedQuery() is not null; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(SqlEditorCommands.GoToLastEditLocation, Key.Back, ModifierKeys.Control | ModifierKeys.Shift));
    }

    private QueryView? LastEditedQuery() => OpenQueries().Where(q => q.HasLastEdit).MaxBy(q => q.LastEditStamp);

    /// <summary>Activates the query tab changed last and puts the caret where that change ended. Returns the tab, or null when nothing was edited.</summary>
    internal QueryView? GoToLastEditLocation()
    {
        if (LastEditedQuery() is not { } query) { Status.Text = "No edit to go back to."; return null; }
        if (OpenDocuments().FirstOrDefault(d => ReferenceEquals(d.Content, query)) is { } document) document.IsActive = true;
        query.GoToLastEdit();
        return query;
    }
}
