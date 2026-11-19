using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>PGMS-082 (M1121): View ▸ Navigate Backward/Forward and All Windows, Window ▸ Pin Tab and Reset Current Object Explorer Details View.
/// Names and places from the SSMS 22.11 inventory (M1063); the behavior is inferred from Visual Studio, on which SSMS 22 is built.</summary>
public partial class MainWindow
{
    public static RoutedUICommand NavigateBackwardCommand { get; } = new("Navigate Backward", "NavigateBackward", typeof(MainWindow), [new KeyGesture(Key.OemMinus, ModifierKeys.Control, "Ctrl+-")]);
    public static RoutedUICommand NavigateForwardCommand { get; } = new("Navigate Forward", "NavigateForward", typeof(MainWindow), [new KeyGesture(Key.OemMinus, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+-")]);
    public static RoutedUICommand AllWindowsCommand { get; } = new("All Windows", "AllWindows", typeof(MainWindow), [new KeyGesture(Key.M, ModifierKeys.Shift | ModifierKeys.Alt, "Shift+Alt+M")]);

    private readonly NavigationHistory<LayoutDocument> navigation = new();
    private bool navigating;
    private List<LayoutAnchorable>? hiddenByAllWindows;
    private readonly HashSet<LayoutDocument> pinnedTabs = [];
    internal NavigationHistory<LayoutDocument> Navigation => navigation;

    private void ConfigureNavigation()
    {
        CommandBindings.Add(new CommandBinding(NavigateBackwardCommand, (_, e) => { NavigateBackward(); e.Handled = true; }, (_, e) => { PruneNavigation(); e.CanExecute = navigation.CanGoBack; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(NavigateForwardCommand, (_, e) => { NavigateForward(); e.Handled = true; }, (_, e) => { PruneNavigation(); e.CanExecute = navigation.CanGoForward; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(AllWindowsCommand, (_, e) => { ToggleAllWindows(); e.Handled = true; }, (_, e) => { e.CanExecute = IsFullScreen; e.Handled = true; }));
        DockManager.ActiveContentChanged += (_, _) => { if (DockManager.Layout.ActiveContent is LayoutDocument active) NoteDocumentActivated(active); };
    }

    // ---- Navigate Backward/Forward ------------------------------------------------------------------------------------

    /// <summary>A tab became active: the place left keeps its caret, and the tab is a new place.</summary>
    private void NoteDocumentActivated(LayoutDocument document)
    {
        if (navigating) return;
        RememberCaretOfCurrentPlace();
        navigation.Visit(document, CaretOf(document));
    }

    private void QueryCaretJumped(QueryView query, int from, int to)
    {
        if (navigating || !documents.TryGetValue(query, out var document)) return;
        if (navigation.Current?.Target != document) navigation.Visit(document, from); else navigation.UpdateCurrent(document, from);
        navigation.Visit(document, to);
    }

    private static int CaretOf(LayoutDocument document) => document.Content is QueryView query ? query.Editor.CaretOffset : 0;

    private void RememberCaretOfCurrentPlace()
    {
        if (navigation.Current is { Target: { Content: QueryView query } target }) navigation.UpdateCurrent(target, query.Editor.CaretOffset);
    }

    private void PruneNavigation()
    {
        if (navigation.Places.Any(p => p.Target.Parent is null)) navigation.Remove(d => d.Parent is null);
    }

    internal bool NavigateBackward() => Navigate(true);
    internal bool NavigateForward() => Navigate(false);

    /// <summary>Goes to the previous (or next) place: tab switches and caret jumps of <see cref="QueryView.NavigationJumpLines"/> lines or more.
    /// Closed tabs are skipped. Places live only in this session.</summary>
    private bool Navigate(bool back)
    {
        PruneNavigation();
        RememberCaretOfCurrentPlace();
        if ((back ? navigation.Back() : navigation.Forward()) is not { } place)
        {
            Status.Text = back ? "No earlier place to go back to." : "No later place to go forward to.";
            return false;
        }
        navigating = true;
        try
        {
            ActivateDocument(place.Target);
            if (place.Target.Content is QueryView query)
            {
                query.Editor.Select(Math.Clamp(place.Offset, 0, query.Editor.Document.TextLength), 0);
                query.Editor.TextArea.Caret.BringCaretToView();
                query.Editor.Focus();
            }
        }
        finally { navigating = false; }
        CommandManager.InvalidateRequerySuggested();
        return true;
    }

    // ---- All Windows ------------------------------------------------------------------------------------------------------

    /// <summary>View ▸ All Windows (Shift+Alt+M), available in Full Screen as in SSMS: hides every tool window to leave only the documents,
    /// and the same command shows them again. Leaving Full Screen also shows them. Returns true when the tool windows are shown.</summary>
    internal bool ToggleAllWindows()
    {
        if (!IsFullScreen) return true;
        if (hiddenByAllWindows is { } hidden) { ShowToolWindowsAgain(); Status.Text = "Tool windows shown."; return true; }
        hiddenByAllWindows = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Where(a => !a.IsHidden).ToList();
        foreach (var anchorable in hiddenByAllWindows) anchorable.Hide();
        Status.Text = "Tool windows hidden; Shift+Alt+M shows them again.";
        return false;
    }

    internal bool AreAllWindowsShown => hiddenByAllWindows is null;

    private void ShowToolWindowsAgain()
    {
        if (hiddenByAllWindows is not { } hidden) return;
        hiddenByAllWindows = null;
        foreach (var anchorable in hidden.Where(a => a.IsHidden)) anchorable.Show();
    }

    // ---- Pin Tab ------------------------------------------------------------------------------------------------------------

    private static readonly ImageSource PinIcon = CreatePinIcon();

    private static DrawingImage CreatePinIcon()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x56, 0x6B)), 1.2);
        var drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xDA, 0xE8, 0xF2)), pen, Geometry.Parse("M4,1 L10,1 9,2 9,7 11,9 3,9 5,7 5,2 Z M7,9 L7,14"));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    internal bool IsPinned(LayoutDocument document) => pinnedTabs.Contains(document);

    private void PinTabClicked(object sender, RoutedEventArgs e) => TogglePinTab();

    /// <summary>Window ▸ Pin Tab: the tab moves after the other pinned tabs at the start of its group, shows a pin and is kept by Close All Tabs.
    /// The same command unpins it. An unsaved query keeps the pin in its recovery draft, so a restored tab comes back pinned (B1 review, M1137).
    /// Returns whether the tab is now pinned.</summary>
    internal bool TogglePinTab(LayoutDocument? document = null)
    {
        document ??= ActiveLayoutContent() as LayoutDocument;
        if (document?.Parent is not LayoutDocumentPane) return false;
        bool pin = !pinnedTabs.Contains(document);
        if (IsDeferredQuery(document)) MountDeferredQuery(document); // its restored pin is applied first, then this command changes it
        SetTabPinned(document, pin);
        ActivateDocument(document);
        if (document.Content is QueryView query) query.MarkRecoveryChanged();
        Status.Text = pin ? "Tab pinned; Close All Tabs keeps it." : "Tab unpinned.";
        return pin;
    }

    /// <summary>Pins or unpins a tab without activating it: moves it after the other pinned tabs of its group and shows the pin.</summary>
    private void SetTabPinned(LayoutDocument document, bool pin)
    {
        if (document.Parent is not LayoutDocumentPane pane) return;
        pinnedTabs.RemoveWhere(d => d.Parent is null);
        if (pin) pinnedTabs.Add(document); else pinnedTabs.Remove(document);
        document.IconSource = pin ? PinIcon : null;
        int at = pane.Children.Count(c => c is LayoutDocument d && d != document && pinnedTabs.Contains(d));
        int from = pane.IndexOfChild(document);
        if (pin ? from > at : from < at) pane.MoveChild(from, at);
    }

    // ---- Reset Current Object Explorer Details View -----------------------------------------------------------------------

    private void ResetObjectDetailsViewClicked(object sender, RoutedEventArgs e) => ResetObjectDetailsView();

    /// <summary>Window ▸ Reset Current Object Explorer Details View: columns back to their order and widths, no sorting, no search text.</summary>
    internal bool ResetObjectDetailsView()
    {
        if (objectDetails is null) return false;
        objectDetails.ResetView();
        Status.Text = "Object Explorer Details view reset.";
        return true;
    }

    private void NavigationMenusOpened()
    {
        var content = ActiveLayoutContent();
        PinTabMenu.IsEnabled = content is LayoutDocument;
        PinTabMenu.IsChecked = content is LayoutDocument document && pinnedTabs.Contains(document);
        ResetObjectDetailsMenu.IsEnabled = objectDetails is not null && ReferenceEquals(content?.Content, objectDetails);
    }
}
