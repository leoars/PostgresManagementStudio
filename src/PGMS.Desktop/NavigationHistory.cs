namespace PGMS.Desktop;

/// <summary>Places visited for View ▸ Navigate Backward/Forward (PGMS-082, M1121): a document tab and, for a query tab, a caret offset.
/// Like a browser history: visiting a new place after going back drops the places ahead of it.</summary>
internal sealed class NavigationHistory<T> where T : class
{
    internal const int Capacity = 100;
    internal sealed record Place(T Target, int Offset);

    private readonly List<Place> places = [];
    private int index = -1;

    public IReadOnlyList<Place> Places => places;
    public int Index => index;
    public Place? Current => index >= 0 && index < places.Count ? places[index] : null;
    public bool CanGoBack => index > 0;
    public bool CanGoForward => index >= 0 && index < places.Count - 1;

    /// <summary>A new place; the same target near the current offset only updates it.</summary>
    public void Visit(T target, int offset, Func<int, int, bool>? near = null)
    {
        if (Current is { } current && ReferenceEquals(current.Target, target) && (current.Offset == offset || near?.Invoke(current.Offset, offset) == true))
        { places[index] = current with { Offset = offset }; return; }
        if (index < places.Count - 1) places.RemoveRange(index + 1, places.Count - index - 1);
        places.Add(new Place(target, offset));
        if (places.Count > Capacity) places.RemoveAt(0);
        index = places.Count - 1;
    }

    /// <summary>Remembers where the caret is now in the current place, so going forward again returns there.</summary>
    public void UpdateCurrent(T target, int offset)
    {
        if (Current is { } current && ReferenceEquals(current.Target, target)) places[index] = current with { Offset = offset };
    }

    public Place? Back() => CanGoBack ? places[--index] : null;
    public Place? Forward() => CanGoForward ? places[++index] : null;

    /// <summary>Drops every place of a target that no longer exists (a closed tab); consecutive places of one target merge.</summary>
    public void Remove(Func<T, bool> gone)
    {
        var keep = new List<Place>();
        int newIndex = -1;
        for (int i = 0; i < places.Count; i++)
        {
            if (gone(places[i].Target)) { if (i == index) newIndex = keep.Count - 1; continue; }
            if (keep.Count > 0 && ReferenceEquals(keep[^1].Target, places[i].Target) && keep[^1].Offset == places[i].Offset) { if (i == index) newIndex = keep.Count - 1; continue; }
            keep.Add(places[i]);
            if (i == index) newIndex = keep.Count - 1;
        }
        places.Clear(); places.AddRange(keep);
        index = places.Count == 0 ? -1 : Math.Clamp(newIndex < 0 ? 0 : newIndex, 0, places.Count - 1);
    }
}
