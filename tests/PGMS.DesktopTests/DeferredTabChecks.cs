using System.IO;
using System.Windows;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>M1133: restored drafts whose tab is not shown keep only their text until the tab is selected; nothing visible changes.</summary>
internal static class DeferredTabChecks
{
    private static readonly string[] Texts =
    [
        "SELECT 'first draft';",
        "SELECT 'second draft' AS needle_two;\r\n-- line 2",
        "SELECT 'third draft' AS needle_three;",
    ];

    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "deferred-tabs", Guid.NewGuid().ToString("N")));
        string? previous = Environment.GetEnvironmentVariable("PGMS_STATE_DIRECTORY");
        try
        {
            await CheckActivationAsync(Path.Combine(root, "activation"), check);
            await CheckCommandsAsync(Path.Combine(root, "commands"), check);
            await CheckCloseAllAsync(Path.Combine(root, "close-all"), check);
            await CheckCloseTabAsync(Path.Combine(root, "close-tab"), check);
            await CheckPinnedAsync(Path.Combine(root, "pinned"), check);
        }
        finally { Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", previous); }
    }

    private static List<RecoveryDraft> Seed(string state)
    {
        var drafts = new List<RecoveryDraft>();
        using var source = new QueryRecoverySession(Path.Combine(state, "recovery"));
        for (int i = 0; i < Texts.Length; i++)
        {
            var draft = new RecoveryDraft(Guid.NewGuid(), $"draft-{i + 1}.sql", Texts[i], i == 2 ? SqlFileFormat.Utf16LittleEndian : SqlFileFormat.Utf8) { WordWrap = i == 1, CommandTimeoutSeconds = 30 + i };
            source.Store.Save(draft); drafts.Add(draft);
        }
        return drafts;
    }

    private static async Task<(MainWindow Window, DockingManager Dock)> OpenAsync(string state)
    {
        Environment.SetEnvironmentVariable("PGMS_STATE_DIRECTORY", state);
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0, Width = 1100, Height = 700 };
        window.Show(); await window.RecoveryCompletion; await Idle(window);
        return (window, (DockingManager)window.FindName("DockManager"));
    }

    private static LayoutDocument[] Tabs(DockingManager dock) => dock.Layout.Descendents().OfType<LayoutDocument>().ToArray();

    private static Task Idle(Window window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task CloseCleanAsync(MainWindow window, DockingManager dock)
    {
        window.MountDeferredQueries();
        foreach (var query in Tabs(dock).Select(d => d.Content).OfType<QueryView>()) query.IsDirty = false;
        window.Close(); await window.CloseCompletion;
    }

    private static string TextOf(QueryView query) => ((TextEditor)query.FindName("Editor")).Text;

    private static async Task CheckActivationAsync(string state, Action<bool, string> check)
    {
        var drafts = Seed(state);
        var (window, dock) = await OpenAsync(state);
        try
        {
            var tabs = Tabs(dock);
            var mounted = tabs.Where(d => d.Content is QueryView).ToArray();
            check(tabs.Length == 3 && mounted.Length == 1 && window.DeferredQueryCount == 2 && ReferenceEquals(dock.ActiveContent, mounted[0].Content),
                "Restore builds the editor only for the active tab; the other two tabs wait");
            check(((LayoutDocumentPane)mounted[0].Parent).IndexOfChild(mounted[0]) == 0 && window.EnvironmentSettings.Load().TabsSettings.InsertTabsToRight == false,
                "Restored tabs come back in their order: the first one restored (the active one) stays first, although new tabs go first by default (M1143)");
            check(tabs.All(d => d.Title.StartsWith("Recovered - draft-", StringComparison.Ordinal) && d.Title.EndsWith('*')),
                "Waiting tabs show the recovered title with the change mark");
            check(tabs.Where(window.IsDeferredQuery).All(d => Equals(d.ToolTip, d.Title.TrimEnd('*'))), "Waiting tabs have the tooltip of a disconnected query without file");
            var store = new QueryRecoveryStore(window.RecoveryDirectory!);
            var stored = store.ListIds().Select(store.Read).ToList();
            check(stored.Count == 3 && Texts.All(t => stored.Count(d => d.Text == t) == 1),
                "Every restored draft, shown or not, is already in this session's recovery store with its full text");
            check(window.CheckpointQueries() == 0 && await window.CheckpointQueriesAsync() == 0 && store.ListIds().Select(store.Read).All(d => Texts.Contains(d.Text)),
                "Checkpoints never rewrite a waiting draft, so the next recovery still has its text");
            check(window.TabsInUseOrder().Count == 3, "Window ▸ Windows… lists the waiting tabs");

            // Selecting a waiting tab builds its editor in place, with the restored state.
            var waiting = tabs.First(window.IsDeferredQuery);
            int index = ((LayoutDocumentPane)waiting.Parent).IndexOfChild(waiting);
            string title = waiting.Title;
            var before = mounted[0];
            waiting.IsActive = true; await Idle(window);
            var query = waiting.Content as QueryView;
            var draft = drafts.Single(d => "Recovered - " + d.Title + "*" == title);
            check(query is not null && ReferenceEquals(dock.ActiveContent, query) && TextOf(query) == draft.Text && query.IsDirty && query.FilePath is null && !query.HasSession
                && query.FileFormat == draft.Format && query.WordWrap == draft.WordWrap && query.CommandTimeoutSeconds == draft.CommandTimeoutSeconds,
                "Activating a waiting tab mounts its editor with the draft text, format, word wrap and timeout, dirty and disconnected");
            check(waiting.Title == title && ((LayoutDocumentPane)waiting.Parent).IndexOfChild(waiting) == index && window.DeferredQueryCount == 1,
                "The mounted tab keeps its title, change mark and place");
            check(query is not null && ((TextEditor)query.FindName("Editor")).IsVisible, "The mounted editor is the one shown");
            check(window.CheckpointQueries() == 0, "Mounting does not rewrite the draft");
            check(window.NavigateBackward() && ReferenceEquals(dock.ActiveContent, before.Content) && window.NavigateForward() && ReferenceEquals(dock.ActiveContent, query),
                "Navigate Backward/Forward goes between the first tab and the tab mounted on activation");

            // Pin Tab on a waiting tab moves it first and activates it, which mounts it.
            var other = Tabs(dock).Single(window.IsDeferredQuery);
            check(window.TogglePinTab(other) && window.IsPinned(other) && ((LayoutDocumentPane)other.Parent).IndexOfChild(other) == 0 && other.Content is QueryView
                && ReferenceEquals(dock.ActiveContent, other.Content) && window.DeferredQueryCount == 0, "Pin Tab on a waiting tab pins, moves and mounts it");
            window.TogglePinTab(other);
        }
        finally { await CloseCleanAsync(window, dock); }

        // A neighbour selected when the active tab closes is mounted.
        Seed(state + "-neighbour");
        (window, dock) = await OpenAsync(state + "-neighbour");
        try
        {
            var active = (QueryView)dock.ActiveContent;
            active.IsDirty = false;
            check(await window.CloseQueryAsync(active), "The active restored tab closes");
            await Idle(window);
            var shown = Tabs(dock).Where(d => d.IsSelected).ToArray();
            check(shown.Length == 1 && shown[0].Content is QueryView q && ((TextEditor)q.FindName("Editor")).IsVisible && window.DeferredQueryCount == 1,
                "The tab selected after a close is mounted before it is shown");
        }
        finally { await CloseCleanAsync(window, dock); }
    }

    private static async Task CheckCommandsAsync(string state, Action<bool, string> check)
    {
        Seed(state);
        var (window, dock) = await OpenAsync(state);
        try
        {
            check(MainWindow.SaveAllCommand.CanExecute(null, window), "Save All is available while only waiting tabs are unsaved");
            int next = Tabs(dock).Length + 1;
            var fresh = window.NewQueryTab();
            check(Tabs(dock).Single(d => ReferenceEquals(d.Content, fresh)).Title == $"SQLQuery{next}.sql", "A new query is numbered as if every restored tab had been built");
            fresh.IsDirty = false;
            var found = await window.FindAllInFilesAsync(new FileSearchRequest(new FileSearchOptions("needle_three"), FileSearchScope.AllOpenDocuments, null, "*.sql", true));
            await Idle(window);
            check(found.Count == 1 && found[0].Query is { } hit && TextOf(hit) == Texts[2] && window.DeferredQueryCount == 0,
                "Find in Files over all open documents mounts the waiting tabs and finds their text");
            var order = Tabs(dock).Select(d => d.Content).OfType<QueryView>().Select(TextOf).Where(Texts.Contains).ToArray();
            check(order.Length == 3, "Every restored draft is still open after Find in Files");
        }
        finally { await CloseCleanAsync(window, dock); }
    }

    private static async Task CheckCloseTabAsync(string state, Action<bool, string> check)
    {
        Seed(state);
        var (window, dock) = await OpenAsync(state);
        try
        {
            var waiting = Tabs(dock).First(window.IsDeferredQuery);
            string title = waiting.Title;
            var active = dock.ActiveContent;
            string? asked = null;
            window.CloseQueryPrompt = q => { asked = TextOf(q); return MessageBoxResult.Cancel; };
            waiting.Close(); await window.TabCloseCompletion; await Idle(window);
            var store = new QueryRecoveryStore(window.RecoveryDirectory!);
            check(asked is not null && Texts.Contains(asked) && waiting.Parent is not null && waiting.Title == title && ReferenceEquals(dock.ActiveContent, active) && store.ListIds().Count == 3,
                "Closing a waiting tab asks to save its text; Cancel keeps the tab, its draft and the active tab");
            window.CloseQueryPrompt = _ => MessageBoxResult.No;
            var other = Tabs(dock).First(window.IsDeferredQuery);
            other.Close(); await window.TabCloseCompletion; await Idle(window);
            check(other.Parent is null && Tabs(dock).Length == 2 && store.ListIds().Count == 2 && window.DeferredQueryCount == 0,
                "Closing a waiting tab with Don't Save removes the tab and its draft only");
        }
        finally { window.CloseQueryPrompt = null; await CloseCleanAsync(window, dock); }
    }

    /// <summary>B1 review (M1137): a pinned tab keeps the pin in its draft and comes back pinned, mounted or waiting, and after Restore Closed Tab.</summary>
    private static async Task CheckPinnedAsync(string state, Action<bool, string> check)
    {
        using (var source = new QueryRecoverySession(Path.Combine(state, "recovery")))
        {
            source.Store.Save(new RecoveryDraft(Guid.NewGuid(), "plain.sql", Texts[0], SqlFileFormat.Utf8));
            source.Store.Save(new RecoveryDraft(Guid.NewGuid(), "pinned.sql", Texts[1], SqlFileFormat.Utf8) { Pinned = true });
        }
        var (window, dock) = await OpenAsync(state);
        try
        {
            var tabs = Tabs(dock);
            var pinned = tabs.SingleOrDefault(d => d.Title == "Recovered - pinned.sql*");
            check(pinned is not null && window.IsPinned(pinned) && pinned.IconSource is not null && ((LayoutDocumentPane)pinned.Parent).IndexOfChild(pinned) == 0
                && tabs.Count(window.IsPinned) == 1, "A draft saved pinned is restored pinned, first in its group, with the pin icon");
            pinned!.IsActive = true; await Idle(window);
            check(pinned.Content is QueryView && window.IsPinned(pinned), "Mounting a restored pinned tab keeps the pin");
            var store = new QueryRecoveryStore(window.RecoveryDirectory!);
            var plain = Tabs(dock).Single(d => d.Title == "Recovered - plain.sql*");
            check(window.TogglePinTab(plain) && plain.Content is QueryView, "Pin Tab pins the other restored tab");
            check(window.CheckpointQueries() >= 1 && store.ListIds().Select(store.Read).Count(d => d.Pinned) == 2, "The next checkpoint writes the pin into the draft");
            check(!window.TogglePinTab(plain) && window.CheckpointQueries() >= 1 && store.ListIds().Select(store.Read).Count(d => d.Pinned) == 1, "Unpinning is written too");
            window.CloseQueryPrompt = _ => MessageBoxResult.No;
            check(await window.CloseQueryAsync((QueryView)pinned.Content), "The pinned tab closes with Don't Save");
            var reopened = await window.RestoreClosedTabAsync();
            await Idle(window);
            var reopenedTab = Tabs(dock).SingleOrDefault(d => ReferenceEquals(d.Content, reopened));
            check(reopenedTab is not null && window.IsPinned(reopenedTab) && ((LayoutDocumentPane)reopenedTab.Parent).IndexOfChild(reopenedTab) == 0, "Restore Closed Tab brings a pinned tab back pinned");
        }
        finally { window.CloseQueryPrompt = null; await CloseCleanAsync(window, dock); }
    }

    private static async Task CheckCloseAllAsync(string state, Action<bool, string> check)
    {
        Seed(state);
        var (window, dock) = await OpenAsync(state);
        IReadOnlyList<string>? asked = null;
        try
        {
            var titles = Tabs(dock).Select(d => d.Title).ToArray();
            window.CloseAllPrompt = names => { asked = names; return MessageBoxResult.No; };
            check(await window.CloseAllTabsAsync() && Tabs(dock).Length == 0, "Close All Tabs closes the waiting tabs too");
            check(asked is not null && asked.Count == 3 && titles.All(asked.Contains), "Close All Tabs asks once, naming every restored tab, mounted or not");
            check(window.DeferredQueryCount == 0 && new QueryRecoveryStore(window.RecoveryDirectory!).ListIds().Count == 0, "Discarded drafts leave the recovery store");
            check(window.ClosedTabs.Count == 3 && Texts.All(t => window.ClosedTabs.Any(c => c.Text == t)), "Restore Closed Tab keeps the text of every closed restored tab");
        }
        finally { await CloseCleanAsync(window, dock); }
    }
}
