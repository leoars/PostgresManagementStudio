using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock.Layout;
using Microsoft.Win32;

namespace PGMS.Desktop;

/// <summary>PGMS-059, files and tabs as in SSMS 22.11 (piloted in M1076): Save All, Recent Files, Open File with New Connection,
/// Close All Tabs, Restore Closed Tab, the numbered tab list of the Window menu and Window ▸ Windows….</summary>
public partial class MainWindow
{
    public static readonly RoutedUICommand SaveAllCommand = new("Save All", nameof(SaveAllCommand), typeof(MainWindow));
    /// <summary>Raised by the SQL editor for its Ctrl+K, Ctrl+Z chord.</summary>
    public static readonly RoutedUICommand RestoreClosedTabCommand = new("Restore Closed Tab", nameof(RestoreClosedTabCommand), typeof(MainWindow));

    /// <summary>A closed query tab: a file reopens from disk; a query without file comes back with its text (memory only, this session).</summary>
    internal sealed record ClosedTab(string? FilePath, string? Text, string Title)
    {
        /// <summary>The tab was pinned when it closed; Restore Closed Tab pins it again (B1 review, M1137).</summary>
        public bool Pinned { get; init; }
    }
    internal const int MaximumClosedTabs = 20;
    private readonly List<ClosedTab> closedTabs = [];
    internal IReadOnlyList<ClosedTab> ClosedTabs => closedTabs;
    private readonly HashSet<QueryView> discardOnClose = [];
    /// <summary>Document tabs in order of use, most recent first (the Window menu numbers them this way, as SSMS does).</summary>
    private readonly List<LayoutContent> tabUse = [];
    private long windowChordDeadline;

    private void InitializeTabCommands()
    {
        CommandBindings.Add(new CommandBinding(SaveAllCommand, async (_, _) => await SaveAllAsync(), (_, e) => e.CanExecute = HasChangedQueries() && !closingWindow && !closeRequested));
        InputBindings.Add(new KeyBinding(SaveAllCommand, Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        CommandBindings.Add(new CommandBinding(RestoreClosedTabCommand, async (_, _) => await RestoreClosedTabAsync(), (_, e) => e.CanExecute = !closingWindow && !closeRequested));
        DockManager.ActiveContentChanged += (_, _) =>
        {
            if (DockManager.Layout.ActiveContent is LayoutDocument active) { tabUse.Remove(active); tabUse.Insert(0, active); }
        };
    }

    private IEnumerable<LayoutDocument> OpenDocuments() => DockManager.Layout.Descendents().OfType<LayoutDocument>().ToList();

    // ---- Recent Files -------------------------------------------------------------------------------------------

    /// <summary>Called after a query file is opened or saved; most recent first, kept to the count set in Tools ▸ Options.</summary>
    internal void RememberRecentFile(string path)
    {
        path = Path.GetFullPath(path);
        // Off the UI thread: the first JSON write of a process costs tens of milliseconds (M1132); the File menu reads it back after the write.
        EnvironmentSettings.UpdateInBackground(d => d with { RecentFiles = RecentFileList.Add(d.RecentFiles, path, d.RecentCount) });
    }

    private void FileMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        PrintMenu.IsEnabled = ActiveQuery is not null; // Page Setup stays available, as in SSMS.
        var settings = EnvironmentSettings.Load();
        var recent = (settings.RecentFiles ?? []).Take(settings.RecentCount).ToList();
        RecentFilesMenu.Items.Clear();
        for (int i = 0; i < recent.Count; i++)
        {
            string path = recent[i];
            string label = (i < 9 ? "_" + (i + 1) : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) + " " + RecentFileList.Abbreviate(path, 60).Replace("_", "__");
            var item = new MenuItem { Header = label, ToolTip = path, Tag = path };
            System.Windows.Automation.AutomationProperties.SetName(item, $"{i + 1} {path}");
            item.Click += async (_, _) => await OpenRecentFileAsync(path);
            RecentFilesMenu.Items.Add(item);
        }
        RecentFilesMenu.IsEnabled = recent.Count > 0;
        if (recent.Count == 0) RecentFilesMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
        SaveAllMenu.IsEnabled = HasChangedQueries() && !closingWindow && !closeRequested;
    }

    internal async Task OpenRecentFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            EnvironmentSettings.Update(d => d with { RecentFiles = RecentFileList.Remove(d.RecentFiles, path) });
            ShowError(new FileNotFoundException($"{path} no longer exists. It was removed from Recent Files."));
            return;
        }
        try { await OpenQueryFileAsync(path); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(ex); }
    }

    // ---- Open File with New Connection ----------------------------------------------------------------------------

    /// <summary>Picks the files for Open ▸ File with New Connection; tests replace it.</summary>
    internal Func<IReadOnlyList<string>>? PickFilesToOpen { get; set; }

    private async void OpenFileWithNewConnection(object sender, RoutedEventArgs e)
    {
        var files = PickSqlFiles();
        if (files.Count > 0) await OpenFilesWithNewConnectionAsync(files);
    }

    /// <summary>Opens each file in a tab connected to the server of the active query (or the database selected in Object Explorer),
    /// each with its own new session, as SSMS does; asks for the connection only when there is neither. Nothing is executed.</summary>
    internal async Task OpenFilesWithNewConnectionAsync(IReadOnlyList<string> files)
    {
        string? connection = ActiveQuery?.ConnectionString ?? SelectedConnection;
        foreach (var file in files)
        {
            if (closeRequested || shuttingDown) break;
            QueryView query;
            try { query = await OpenQueryFileAsync(file); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(ex); continue; }
            if (query.HasSession || query.IsBusy) continue; // already open in a connected tab
            connection ??= PickConnection();
            if (connection is null) return;
            try { await query.ConnectAsync(connection); Status.Text = "Opened with a new connection. Nothing was executed."; }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        }
        RefreshCommands();
    }

    // ---- Save All ---------------------------------------------------------------------------------------------------

    /// <summary>Saves every changed query tab in tab order; a query without a file asks for a name, and cancelling stops the rest, as in SSMS.</summary>
    internal async Task<bool> SaveAllAsync()
    {
        MountDeferredQueries();
        foreach (var query in OpenDocuments().Select(d => d.Content).OfType<QueryView>().Where(q => q.IsDirty && documents.ContainsKey(q)).ToList())
        {
            if (!await SaveAsync(query) || query.IsDirty) { Status.Text = "Save All stopped; the remaining queries were not saved."; return false; }
        }
        Status.Text = "All items saved";
        return true;
    }

    // ---- Close All Tabs ---------------------------------------------------------------------------------------------

    /// <summary>Answers the Close All Tabs question in tests (receives the changed item names).</summary>
    internal Func<IReadOnlyList<string>, MessageBoxResult>? CloseAllPrompt { get; set; }

    private async void CloseAllTabs(object sender, RoutedEventArgs e) => await CloseAllTabsAsync();

    /// <summary>One question lists every changed query (Save / Don't Save / Cancel); then each tab closes as usual
    /// (busy queries, open transactions and changed designers still ask on their own).</summary>
    internal async Task<bool> CloseAllTabsAsync()
    {
        var all = OpenDocuments().Where(d => !pinnedTabs.Contains(d)).ToList(); // pinned tabs stay (Window ▸ Pin Tab, M1121)
        MountDeferredQueries(all);
        var changed = all.Select(d => d.Content).OfType<QueryView>().Where(q => q.IsDirty).ToList();
        if (changed.Count > 0)
        {
            var names = changed.Select(q => documents[q].Title).ToList();
            var answer = CloseAllPrompt?.Invoke(names) ?? LocalizedMessageBox.Show(this, "Save changes to the following items?\n\n" + string.Join("\n", names), "PGMS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None) return false;
            if (answer == MessageBoxResult.Yes)
                foreach (var query in changed) if (!await SaveAsync(query) || query.IsDirty) return false;
            if (answer == MessageBoxResult.No) foreach (var query in changed) discardOnClose.Add(query);
        }
        bool closedAll = true;
        try
        {
            foreach (var document in all)
            {
                if (document.Content is QueryView query) { if (documents.ContainsKey(query)) closedAll &= await CloseQueryAsync(query); }
                else { document.Close(); closedAll &= document.Parent is null; }
            }
        }
        finally { discardOnClose.Clear(); }
        RefreshCommands();
        return closedAll;
    }

    // ---- Restore Closed Tab ---------------------------------------------------------------------------------------

    /// <summary>Remembers a query tab that is about to close (called after the close was accepted).</summary>
    private void RememberClosedTab(QueryView query, string title)
    {
        if (closingWindow || shuttingDown) return;
        string? text = query.FilePath is null ? query.Editor.Text : null;
        if (query.FilePath is null && string.IsNullOrWhiteSpace(text)) return;
        if (text is { Length: > SqlFileStore.MaximumBytes }) return; // like a file, too large to keep
        closedTabs.Insert(0, new ClosedTab(query.FilePath, text, title.TrimEnd('*')) { Pinned = documents.TryGetValue(query, out var document) && pinnedTabs.Contains(document) });
        if (closedTabs.Count > MaximumClosedTabs) closedTabs.RemoveAt(closedTabs.Count - 1);
    }

    private async void RestoreClosedTab(object sender, RoutedEventArgs e) => await RestoreClosedTabAsync();

    /// <summary>Reopens the most recently closed query tab, disconnected; nothing is executed.</summary>
    internal async Task<QueryView?> RestoreClosedTabAsync()
    {
        while (closedTabs.Count > 0)
        {
            var closed = closedTabs[0]; closedTabs.RemoveAt(0);
            if (closed.FilePath is { } path)
            {
                try
                {
                    var query = await OpenQueryFileAsync(path);
                    if (closed.Pinned && documents.TryGetValue(query, out var opened) && !pinnedTabs.Contains(opened)) SetTabPinned(opened, true);
                    Status.Text = "Closed tab restored from " + Path.GetFileName(path) + ". Not connected."; return query;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { ShowError(ex); return null; }
            }
            var restored = NewQuery(closed.Text, origin: QueryHistoryOrigin.Typed);
            restored.IsDirty = true;
            if (closed.Pinned) SetTabPinned(documents[restored], true);
            Status.Text = "Closed query restored with its text. Not connected; nothing was executed.";
            return restored;
        }
        Status.Text = "No closed tab to restore.";
        return null;
    }

    /// <summary>Ctrl+K, Ctrl+Z outside the SQL editor (the editor handles its own Ctrl+K chord).</summary>
    private bool HandleWindowChord(KeyEventArgs e)
    {
        if (ActiveQuery?.Editor.IsKeyboardFocusWithin == true) { windowChordDeadline = 0; return false; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return false;
        long now = Environment.TickCount64;
        if (windowChordDeadline > 0)
        {
            bool valid = now <= windowChordDeadline; windowChordDeadline = 0;
            if (valid && key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = RestoreClosedTabAsync(); return true; }
            if (valid && key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; ShowCodeSnippetsManager(); return true; }
        }
        if (key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) { windowChordDeadline = now + 2000; e.Handled = true; return true; }
        return false;
    }

    // ---- Window menu --------------------------------------------------------------------------------------------------

    private const string TabListTag = "tab-list";

    private void WindowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        WindowMenuPanesOpened();
        NavigationMenusOpened();
        RestoreClosedTabMenu.IsEnabled = closedTabs.Count > 0 && !closingWindow && !closeRequested;
        var open = TabsInUseOrder();
        CloseAllTabsMenu.IsEnabled = open.Count > 0 && !closingWindow && !closeRequested;
        foreach (var old in WindowMenu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, TabListTag)).ToList()) WindowMenu.Items.Remove(old);
        int at = WindowMenu.Items.IndexOf(WindowsDialogMenu);
        if (open.Count > 0) WindowMenu.Items.Insert(at++, new Separator { Tag = TabListTag });
        for (int i = 0; i < open.Count && i < 10; i++)
        {
            var document = open[i];
            string title = document.Title ?? "";
            var item = new MenuItem { Header = (i < 9 ? "_" + (i + 1) : "1_0") + " " + title.Replace("_", "__"), Tag = TabListTag, IsCheckable = false, IsChecked = document.IsActive };
            System.Windows.Automation.AutomationProperties.SetName(item, $"{i + 1} {title}");
            item.Click += (_, _) => ActivateDocument(document);
            WindowMenu.Items.Insert(at++, item);
        }
    }

    /// <summary>Open document tabs, the most recently used first; tabs never activated follow in tab order.</summary>
    internal IReadOnlyList<LayoutDocument> TabsInUseOrder()
    {
        var open = OpenDocuments().ToList();
        tabUse.RemoveAll(c => c is not LayoutDocument d || !open.Contains(d));
        return [.. tabUse.OfType<LayoutDocument>(), .. open.Where(d => !tabUse.Contains(d))];
    }

    private static void ActivateDocument(LayoutDocument document) { document.IsSelected = true; document.IsActive = true; }

    private void ShowWindowsDialog(object sender, RoutedEventArgs e)
    {
        var dialog = new WindowsDialog(TabsInUseOrder().Select(d => new WindowsDialog.Entry(d.Title?.TrimEnd('*') ?? "", (d.Content as QueryView)?.FilePath ?? "", d, d.Content is QueryView || IsDeferredQuery(d))).ToList(),
            ActivateDocument,
            async selected => { MountDeferredQueries(selected); foreach (var query in selected.Select(d => d.Content).OfType<QueryView>().Where(q => documents.ContainsKey(q))) if (!await SaveAsync(query)) return; },
            async selected => { MountDeferredQueries(selected); foreach (var document in selected) { if (document.Content is QueryView query) await CloseQueryAsync(query); else document.Close(); } })
        { Owner = this };
        dialog.ShowDialog();
    }
}

/// <summary>Recent Files list kept in environment.json: most recent first, no duplicates (case-insensitive paths).</summary>
internal static class RecentFileList
{
    internal const int DefaultCount = 10, MaximumCount = 24;

    internal static IReadOnlyList<string> Add(IReadOnlyList<string>? list, string path, int count) =>
        [path, .. (list ?? []).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).Take(Math.Max(0, Math.Min(count, MaximumCount) - 1))];

    internal static IReadOnlyList<string> Remove(IReadOnlyList<string>? list, string path) =>
        [.. (list ?? []).Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Shortens a long path in the middle, keeping the root and the last folders, as the SSMS menu shows them (<c>D:\...\_PGMS\docs\a.sql</c>).</summary>
    internal static string Abbreviate(string path, int maximum)
    {
        if (path.Length <= maximum) return path;
        string root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar);
        string tail = parts[^1];
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            string candidate = parts[i] + Path.DirectorySeparatorChar + tail;
            if (root.Length + 4 + candidate.Length > maximum) break;
            tail = candidate;
        }
        return root + "..." + Path.DirectorySeparatorChar + tail;
    }
}
