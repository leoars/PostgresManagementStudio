using System.Windows;
using System.Windows.Input;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>Window ▸ Windows…, as in SSMS 22.11: the open tabs with Name and Path (sorted by Path), Activate, Save, Close Window(s) and OK.</summary>
public partial class WindowsDialog : Window
{
    public sealed record Entry(string Name, string Path, LayoutDocument Document, bool CanSave);

    private readonly List<Entry> entries;
    private readonly Action<LayoutDocument> activate;
    private readonly Func<IReadOnlyList<LayoutDocument>, Task> save, close;
    private bool byName;

    public WindowsDialog(IReadOnlyList<Entry> entries, Action<LayoutDocument> activate, Func<IReadOnlyList<LayoutDocument>, Task> save, Func<IReadOnlyList<LayoutDocument>, Task> close)
    {
        InitializeComponent();
        this.entries = [.. entries]; this.activate = activate; this.save = save; this.close = close;
        Fill();
        if (WindowList.Items.Count > 0) WindowList.SelectedIndex = 0;
        Loaded += (_, _) => WindowList.Focus();
    }

    internal IReadOnlyList<Entry> Shown => WindowList.Items.Cast<Entry>().ToList();
    internal IReadOnlyList<Entry> Selected => WindowList.SelectedItems.Cast<Entry>().ToList();

    private void Fill()
    {
        var keep = Selected.Select(e => e.Document).ToHashSet();
        var ordered = byName ? entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase) : entries.OrderBy(e => e.Path, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
        WindowList.ItemsSource = ordered.ToList();
        foreach (var entry in Shown.Where(e => keep.Contains(e.Document))) WindowList.SelectedItems.Add(entry);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ActivateButton.IsEnabled = Selected.Count == 1;
        SaveButton.IsEnabled = Selected.Any(e => e.CanSave);
        CloseWindowsButton.IsEnabled = Selected.Count > 0;
    }

    private void SelectionChangedHandler(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButtons();
    private void SortByName(object sender, RoutedEventArgs e) { byName = true; Fill(); }
    private void SortByPath(object sender, RoutedEventArgs e) { byName = false; Fill(); }

    internal void ActivateSelected(object sender, RoutedEventArgs e)
    {
        if (Selected is not [var entry]) return;
        activate(entry.Document);
        Finish();
    }

    private void ActivateOnDoubleClick(object sender, MouseButtonEventArgs e) => ActivateSelected(sender, e);

    internal async void Save(object sender, RoutedEventArgs e) => await SaveSelectedAsync();
    internal async Task SaveSelectedAsync()
    {
        var selected = Selected.Where(x => x.CanSave).Select(x => x.Document).ToList();
        if (selected.Count == 0) return;
        IsEnabled = false;
        try { await save(selected); StatusText.Text = "Saved the selected items."; }
        finally { IsEnabled = true; }
        foreach (var entry in entries.ToList()) entries[entries.IndexOf(entry)] = entry with { Name = entry.Document.Title?.TrimEnd('*') ?? entry.Name };
        Fill();
    }

    internal async void CloseWindows(object sender, RoutedEventArgs e) => await CloseSelectedAsync();
    internal async Task CloseSelectedAsync()
    {
        var selected = Selected.Select(x => x.Document).ToList();
        if (selected.Count == 0) return;
        IsEnabled = false;
        try { await close(selected); }
        finally { IsEnabled = true; }
        entries.RemoveAll(x => x.Document.Parent is null);
        Fill();
        StatusText.Text = selected.All(d => d.Parent is null) ? "Closed." : "Some items stayed open.";
    }

    private void Ok(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); } // shown without ShowDialog
    }
}
