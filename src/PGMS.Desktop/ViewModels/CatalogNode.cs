using System.Collections.ObjectModel;
using System.ComponentModel;
using PGMS.PostgreSql;

namespace PGMS.Desktop.ViewModels;

public sealed class CatalogNode : INotifyPropertyChanged
{
    private readonly CatalogService service;
    private bool loaded;
    private readonly List<CatalogNode> allChildren = [];
    public string Filter { get; private set; } = "";
    public bool CanFilter => Entry.Kind is CatalogKind.Databases or CatalogKind.Schemas or CatalogKind.Tables or CatalogKind.Views or CatalogKind.MaterializedViews or CatalogKind.Sequences or CatalogKind.Functions or CatalogKind.Procedures or CatalogKind.Roles or CatalogKind.Columns or CatalogKind.Indexes or CatalogKind.Enums or CatalogKind.Domains;
    private bool busy;
    private CancellationTokenSource? loadCancellation;
    public bool IsBusy => busy;
    public bool WasCanceled { get; private set; }
    public void CancelLoad() => loadCancellation?.Cancel();
    private void NotifyLoadState()
    {
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new(nameof(Name)));
        PropertyChanged?.Invoke(this, new(nameof(FilterSummary)));
    }
    private string? error;
    public CatalogEntry Entry { get; }
    public CatalogNode? Parent { get; private set; }
    public CatalogNode? LoadTarget => Entry.CanExpand ? this : Parent;
    private bool expanded, selected;
    public bool IsExpanded { get => expanded; set { expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    public bool IsSelected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public string Name => Entry.Name + (Filter.Length > 0 ? " (filtered)" : "") + (busy ? " (loading)" : WasCanceled ? " (load canceled)" : Error is not null ? " (load failed)" : "");
    public string FilterSummary => (busy ? "Loading… " : WasCanceled ? "Load canceled; displayed items may be outdated. Refresh to retry. " : Error is not null ? "Load failed; displayed items may be outdated. Refresh to retry. " : "") + (loaded ? $"{allChildren.Count(n => Matches(n))} of {allChildren.Count} items. Filter applies to direct children only." : "Expand or refresh to load items.");
    private bool Matches(CatalogNode node) => node.Entry.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase);
    public void SetFilter(string text)
    {
        if (!CanFilter) return;
        Filter = text;
        if (loaded) ShowChildren();
        PropertyChanged?.Invoke(this, new(nameof(Name)));
        PropertyChanged?.Invoke(this, new(nameof(Filter)));
        PropertyChanged?.Invoke(this, new(nameof(FilterSummary)));
    }
    private void ShowChildren()
    {
        var visible = allChildren.Where(Matches).ToList();
        if (visible.Count == 0) visible.Add(new(new(CatalogKind.Column, allChildren.Count == 0 ? "(empty)" : "(no matching items)", Entry.Database, Entry.Schema), service) { Parent = this });
        foreach (var old in Children.Where(n => !visible.Contains(n)).ToArray()) Children.Remove(old);
        for (int i = 0; i < visible.Count; i++)
        {
            int current = Children.IndexOf(visible[i]);
            if (current < 0) Children.Insert(i, visible[i]);
            else if (current != i) Children.Move(current, i);
        }
        PropertyChanged?.Invoke(this, new(nameof(FilterSummary)));
    }
    public string? Error { get => error; private set { error = value; PropertyChanged?.Invoke(this, new(nameof(Error))); PropertyChanged?.Invoke(this, new(nameof(Name))); PropertyChanged?.Invoke(this, new(nameof(FilterSummary))); } }
    public ObservableCollection<CatalogNode> Children { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public CatalogNode(CatalogEntry entry, CatalogService service)
    {
        Entry = entry; this.service = service;
        if (entry.CanExpand) Children.Add(new(new(CatalogKind.Column, "Expand to load…", entry.Database, entry.Schema), service) { Parent = this });
    }

    public async Task LoadAsync(CancellationToken token = default, bool refresh = false)
    {
        if (busy || (loaded && !refresh && Error is null && !WasCanceled) || !Entry.CanExpand) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        loadCancellation = cancellation;
        token = cancellation.Token;
        busy = true; WasCanceled = false; Error = null; NotifyLoadState();
        try
        {
            var entries = await service.ChildrenAsync(Entry, token);
            token.ThrowIfCancellationRequested();
            var previous = allChildren.ToDictionary(n => n.Entry);
            var next = entries.Select(entry => previous.TryGetValue(entry, out var node) ? node : new CatalogNode(entry, service) { Parent = this }).ToList();
            allChildren.Clear(); allChildren.AddRange(next);
            loaded = true;
            ShowChildren();
        }
        catch (OperationCanceledException) { WasCanceled = true; }
        // Npgsql can wrap cancellation during connection startup in a transport exception.
        catch (Exception ex) when (token.IsCancellationRequested && ex is Npgsql.NpgsqlException or System.IO.IOException)
        {
            WasCanceled = true;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.IO.IOException)
        {
            Error = ex is Npgsql.PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message;
            if (!loaded)
            {
                Children.Clear();
                Children.Add(new(new(CatalogKind.Column, "Could not load. Collapse and expand to retry.", Entry.Database, Entry.Schema), service) { Parent = this });
            }
        }
        finally { busy = false; loadCancellation = null; NotifyLoadState(); }
    }
}
