using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>
/// Tools ▸ Options ▸ Environment ▸ Query Shortcuts (PGMS-072, M1097). As in SSMS 22.11, Alt+F1 and Ctrl+1…Ctrl+0 run the text set for them in the
/// active query's session; selected text is appended after a space (SSMS passes it as the procedure's parameter). PGMS has no defaults: sp_help,
/// sp_who and sp_lock have no PostgreSQL counterpart with the same call shape. A shortcut without text does nothing.
/// </summary>
public partial class MainWindow
{
    internal static string? QueryShortcutName(Key key, Key systemKey, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Alt && (key == Key.System ? systemKey : key) == Key.F1) return "Alt+F1";
        if (modifiers == ModifierKeys.Control && key is >= Key.D0 and <= Key.D9) return "Ctrl+" + (char)('0' + (key - Key.D0));
        return null;
    }

    /// <summary>The SQL a shortcut runs: its text, then the selection after a space.</summary>
    internal static string QueryShortcutSql(string text, string? selection) => string.IsNullOrWhiteSpace(selection) ? text : text + " " + selection;

    /// <summary>Runs the shortcut in the active query tab. False when it has no text, or there is no connected, idle query tab.</summary>
    internal async Task<bool> RunQueryShortcutAsync(string shortcut)
    {
        if (EnvironmentSettings.Load().QueryShortcut(shortcut) is not { } text) return false;
        if (ActiveQuery is not { } query) return false;
        if (!query.IsConnected || query.IsBusy) { Status.Text = $"Query shortcut {shortcut} needs a connected query that is not running."; return false; }
        await query.ExecuteShortcutAsync(QueryShortcutSql(text, query.SelectedSql));
        return true;
    }

    private async Task<bool> HandleQueryShortcutAsync(KeyEventArgs e)
    {
        if (ActiveQuery is null || QueryShortcutName(e.Key, e.SystemKey, Keyboard.Modifiers) is not { } shortcut || EnvironmentSettings.Load().QueryShortcut(shortcut) is null) return false;
        e.Handled = true;
        await RunQueryShortcutAsync(shortcut);
        return true;
    }
}
