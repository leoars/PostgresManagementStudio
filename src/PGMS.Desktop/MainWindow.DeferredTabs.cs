using System.ComponentModel;
using System.Windows.Controls;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>M1133 (gate B7): a restored draft whose tab is not shown keeps only its text until the tab is selected or activated;
/// then the editor (AvalonEdit with highlighting, outline and the rest of the query window) is built in place, in the same tab.
/// Title, change mark, order, connection (none) and path (none) are the tab's own from the start; the draft was written to this
/// session's recovery store before the tab appeared, as for a mounted tab. Anything that needs the editor of every tab
/// (Save All, Close All, closing the window, Find in Files, Window ▸ Windows… actions) mounts the waiting tabs first.</summary>
public partial class MainWindow
{
    private sealed class DeferredQuery(RecoveryDraft draft, EnvironmentOptions.Document newTabSettings)
    {
        public RecoveryDraft Draft { get; } = draft;
        /// <summary>Options when the draft was restored: new-window settings stay what an immediately built tab would have had.</summary>
        public EnvironmentOptions.Document NewTabSettings { get; } = newTabSettings;
        /// <summary>The document read off the UI thread after the restore, when it finished before the tab is shown.</summary>
        public Task<PreparedQueryDocument?>? Preparation { get; set; }
        public PropertyChangingEventHandler? Changing { get; set; }
        public PropertyChangedEventHandler? Changed { get; set; }
        public EventHandler<CancelEventArgs>? Closing { get; set; }
    }

    /// <summary>Shown in a waiting tab's place; never visible, because selecting the tab mounts the editor first.</summary>
    private sealed class DeferredQueryPlaceholder : Border;

    private readonly Dictionary<LayoutDocument, DeferredQuery> deferredQueries = [];
    private readonly Dictionary<LayoutDocument, long> queryTabOrder = [];
    private long queryTabStamp;

    /// <summary>Restored query tabs that have not been shown yet (tests and diagnostics).</summary>
    internal int DeferredQueryCount => deferredQueries.Count;

    internal bool IsDeferredQuery(LayoutDocument document) => deferredQueries.ContainsKey(document);

    private void NoteQueryTabCreated(LayoutDocument document) => queryTabOrder[document] = ++queryTabStamp;

    /// <summary>Recovery ids of every query tab, mounted or waiting: both hold a draft in the store.</summary>
    private IEnumerable<Guid> OpenRecoveryIds() => documents.Keys.Select(q => q.RecoveryId).Concat(deferredQueries.Values.Select(d => d.Draft.Id));

    /// <summary>A waiting tab is a restored draft, so it is always unsaved.</summary>
    private bool HasChangedQueries() => deferredQueries.Count > 0 || documents.Keys.Any(q => q.IsDirty);

    private List<LayoutDocument> QueryTabsInCreationOrder() =>
        [.. documents.Values.Concat(deferredQueries.Keys).Distinct().OrderBy(d => queryTabOrder.GetValueOrDefault(d, long.MaxValue))];

    private List<QueryView> QueriesInCreationOrder() =>
        [.. documents.OrderBy(p => queryTabOrder.GetValueOrDefault(p.Value, long.MaxValue)).Select(p => p.Key)];

    private void ApplyRecoveredState(QueryView query, RecoveryDraft draft)
    {
        query.FileFormat = draft.Format; query.WordWrap = draft.WordWrap; query.ShowWhiteSpace = draft.ShowWhiteSpace; query.CommandTimeoutSeconds = draft.CommandTimeoutSeconds; query.IsDirty = true;
        documents[query].Title = draft.Title + "*";
        if (draft.Pinned && !pinnedTabs.Contains(documents[query])) SetTabPinned(documents[query], true);
        checkpointRevisions[query.RecoveryId] = query.RecoveryRevision;
    }

    /// <summary>Adds the tab of a restored draft (already in the store) without building its editor.</summary>
    private void AddDeferredQuery(RecoveryDraft draft)
    {
        // The tab takes the same number an immediately built tab would have taken, so the next new query is numbered as before.
        ++sequence;
        var document = new LayoutDocument { Title = draft.Title + "*", ContentId = Guid.NewGuid().ToString(), Content = new DeferredQueryPlaceholder() };
        var deferred = new DeferredQuery(draft, EnvironmentSettings.Load()) { Preparation = PrepareDeferredQuery(draft.Text) };
        // Mounted before the tab becomes selected or active, so the docking manager shows and activates the editor itself.
        deferred.Changing = (_, e) => { if (e.PropertyName is nameof(LayoutContent.IsSelected) or nameof(LayoutContent.IsActive) && !document.IsSelected) MountDeferredQuery(document); };
        deferred.Changed = (_, e) =>
        {
            if (e.PropertyName is not (nameof(LayoutContent.IsSelected) or nameof(LayoutContent.IsActive)) || !(document.IsSelected || document.IsActive)) return;
            var query = MountDeferredQuery(document);
            if (document.IsActive && !ReferenceEquals(DockManager.ActiveContent, query)) DockManager.ActiveContent = query;
        };
        deferred.Closing = (_, e) =>
        {
            e.Cancel = true;
            _ = CloseQueryAsync(MountDeferredQuery(document));
        };
        document.PropertyChanging += deferred.Changing;
        document.PropertyChanged += deferred.Changed;
        document.Closing += deferred.Closing;
        deferredQueries.Add(document, deferred);
        NoteQueryTabCreated(document);
        AddDocument(document);
        if (draft.Pinned) SetTabPinned(document, true); // restored pinned, before its editor exists (B1 review, M1137)
        if (document.IsSelected || document.IsActive) { MountDeferredQuery(document); return; } // an empty pane selects its first tab
        UpdateConnectionTexts();
        RefreshCommands();
    }

    /// <summary>Reads a waiting draft into a document on a worker (highlighting states and outline), so showing the tab later only attaches it.</summary>
    private static Task<PreparedQueryDocument?> PrepareDeferredQuery(string text) => Task.Run(() =>
    {
        // Only a shortcut: if reading fails, mounting puts the text in directly.
        try { return (PreparedQueryDocument?)PreparedQueryDocument.Create(text); }
        catch (Exception) { return null; }
    });

    /// <summary>Builds the editor of a waiting tab in place; the tab keeps its title, place, pin and history entries.</summary>
    private QueryView MountDeferredQuery(LayoutDocument document)
    {
        if (document.Content is QueryView mounted) return mounted;
        var deferred = deferredQueries[document];
        deferredQueries.Remove(document);
        document.PropertyChanging -= deferred.Changing;
        document.PropertyChanged -= deferred.Changed;
        document.Closing -= deferred.Closing;
        // A preparation still running is not awaited: the text goes in directly, as for a new tab.
        var prepared = deferred.Preparation is { IsCompletedSuccessfully: true } done ? done.Result : null;
        var draft = deferred.Draft;
        var query = NewQuery(prepared is null ? draft.Text : null, recoveryId: draft.Id, activate: false, prepared: prepared, origin: QueryHistoryOrigin.Typed,
            deferredTab: document, newTabSettings: deferred.NewTabSettings);
        ApplyRecoveredState(query, draft);
        UpdateConnectionTexts();
        RefreshCommands();
        return query;
    }

    /// <summary>Mounts every waiting tab (or those among <paramref name="tabs"/>), in tab creation order.</summary>
    internal void MountDeferredQueries(IEnumerable<LayoutDocument>? tabs = null)
    {
        var wanted = tabs?.ToHashSet();
        foreach (var document in QueryTabsInCreationOrder().Where(d => deferredQueries.ContainsKey(d) && (wanted is null || wanted.Contains(d))))
            MountDeferredQuery(document);
    }
}
