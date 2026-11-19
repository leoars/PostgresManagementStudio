using System.Windows;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Edit ▸ Bookmarks and Edit ▸ Outlining (PGMS-057, M1080): forwarded to the active query; Clear Bookmarks clears every query after confirming, as in SSMS.</summary>
public partial class MainWindow
{
    /// <summary>Confirms Clear Bookmarks; tests replace it.</summary>
    internal Func<bool>? ConfirmClearBookmarks { get; set; }

    private void ConfigureBookmarkCommands()
    {
        foreach (var (command, run, canRun) in SqlBookmarkCommands.ViewCommands)
            CommandBindings.Add(new CommandBinding(command, (_, _) => { if (ActiveQuery is { } query) run(query); }, (_, e) => e.CanExecute = ActiveQuery is { } query && !closingWindow && canRun(query)));
        CommandBindings.Add(new CommandBinding(SqlBookmarkCommands.ClearBookmarks, (_, _) => ClearAllBookmarks(), (_, e) => e.CanExecute = !closingWindow && documents.Keys.Any(q => q.BookmarkCount > 0)));
    }

    internal bool ClearAllBookmarks()
    {
        if (!documents.Keys.Any(q => q.BookmarkCount > 0)) return false;
        bool confirmed = ConfirmClearBookmarks?.Invoke() ?? LocalizedMessageBox.Show(this, "Are you sure you want to delete all of the bookmark(s)?", "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!confirmed) return false;
        foreach (var query in documents.Keys) query.ClearBookmarks();
        return true;
    }
}
