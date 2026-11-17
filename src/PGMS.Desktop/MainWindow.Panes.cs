using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;

namespace PGMS.Desktop;

/// <summary>Window menu panes and layouts, View ▸ Full Screen (PGMS-069, M1092).</summary>
public partial class MainWindow
{
    public static RoutedUICommand NextPaneCommand { get; } = new("Next Pane", "NextPane", typeof(MainWindow), [new KeyGesture(Key.F6)]);
    public static RoutedUICommand PreviousPaneCommand { get; } = new("Previous Pane", "PreviousPane", typeof(MainWindow), [new KeyGesture(Key.F6, ModifierKeys.Shift)]);
    public static RoutedUICommand ShowResultsPaneCommand { get; } = new("Show Results Pane", "ShowResultsPane", typeof(MainWindow), [new KeyGesture(Key.R, ModifierKeys.Control)]);
    public static RoutedUICommand FullScreenCommand { get; } = new("Full Screen", "FullScreen", typeof(MainWindow), [new KeyGesture(Key.Enter, ModifierKeys.Shift | ModifierKeys.Alt)]);
    private (WindowState State, WindowStyle Style, ResizeMode Resize)? beforeFullScreen;
    private WindowLayoutStore? windowLayouts;
    internal WindowLayoutStore WindowLayouts => windowLayouts ??= new WindowLayoutStore(Path.Combine(Path.GetDirectoryName(layoutFile)!, "window-layouts.json"));
    /// <summary>Asks the name in Save Window Layout; replaced by tests. Null means Cancel.</summary>
    internal Func<string, string?>? LayoutNamePrompt { get; set; }
    public bool IsFullScreen => beforeFullScreen is not null;

    private void ConfigurePaneCommands()
    {
        CommandBindings.Add(new CommandBinding(NextPaneCommand, (_, _) => ActiveQuery?.MovePane(true), (_, e) => e.CanExecute = ActiveQuery is not null));
        CommandBindings.Add(new CommandBinding(PreviousPaneCommand, (_, _) => ActiveQuery?.MovePane(false), (_, e) => e.CanExecute = ActiveQuery is not null));
        CommandBindings.Add(new CommandBinding(ShowResultsPaneCommand, (_, _) => ToggleResultsPane(), (_, e) => e.CanExecute = ActiveQuery is not null));
        CommandBindings.Add(new CommandBinding(FullScreenCommand, (_, _) => SetFullScreen(!IsFullScreen)));
        CommandBindings.Add(new CommandBinding(PresenterModeCommand, TogglePresenterModeExecuted));
        CommandBindings.Add(new CommandBinding(FeatureSearchCommand, (_, _) => ShowFeatureSearch()));
    }

    internal bool ToggleResultsPane()
    {
        if (ActiveQuery is not { } query) return false;
        query.SetResultsPaneVisible(!query.IsResultsPaneVisible);
        return query.IsResultsPaneVisible;
    }

    private void SplitTabClicked(object sender, RoutedEventArgs e) { if (ActiveQuery is { } q) q.SetSplit(!q.IsSplit); }

    /// <summary>Full Screen: no title bar, maximized, toolbars hidden; the menu stays so the same command returns.</summary>
    internal void SetFullScreen(bool on)
    {
        if (on == IsFullScreen) return;
        if (on)
        {
            beforeFullScreen = (WindowState, WindowStyle, ResizeMode);
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-maximizing without borders covers the taskbar
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
            MainToolBars.Visibility = Visibility.Collapsed;
        }
        else
        {
            var (state, style, resize) = beforeFullScreen!.Value;
            beforeFullScreen = null;
            WindowStyle = style; ResizeMode = resize; WindowState = WindowState.Normal; WindowState = state;
            MainToolBars.Visibility = Visibility.Visible;
        }
        FullScreenMenu.IsChecked = on;
        UpdateTitleBand();
    }

    private void FullScreenClicked(object sender, RoutedEventArgs e) => SetFullScreen(!IsFullScreen);

    /// <summary>The layout item of the active tool window or document.</summary>
    private LayoutContent? ActiveLayoutContent() => DockManager.Layout.ActiveContent ?? DockManager.Layout.Descendents().OfType<LayoutContent>().FirstOrDefault(c => c.Content == DockManager.ActiveContent);

    private void DockActiveClicked(object sender, RoutedEventArgs e) => DockActive();
    internal bool DockActive()
    {
        if (ActiveLayoutContent() is not { } content) return false;
        if (content is LayoutAnchorable { IsAutoHidden: true } hidden) { hidden.ToggleAutoHide(); return true; }
        if (content.IsFloating) { content.Dock(); return true; }
        return false;
    }

    private void AutoHideActiveClicked(object sender, RoutedEventArgs e) => AutoHideActive();
    internal bool AutoHideActive()
    {
        if (ActiveLayoutContent() is not LayoutAnchorable { CanAutoHide: true, IsAutoHidden: false } anchorable) return false;
        if (anchorable.IsFloating) anchorable.Dock();
        anchorable.ToggleAutoHide();
        return true;
    }

    private void FloatActiveClicked(object sender, RoutedEventArgs e) => FloatActive();
    /// <summary>Move To New Window: the active document or tool window floats in its own window.</summary>
    internal bool FloatActive()
    {
        if (ActiveLayoutContent() is not { IsFloating: false, CanFloat: true } content) return false;
        content.Float();
        return true;
    }

    private void AutoHideAllClicked(object sender, RoutedEventArgs e) => AutoHideAll();
    /// <summary>Auto Hide All: every docked tool window goes to the edge; documents stay.</summary>
    internal int AutoHideAll()
    {
        int count = 0;
        foreach (var anchorable in DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Where(a => a is { IsAutoHidden: false, IsFloating: false, IsHidden: false, CanAutoHide: true }).ToList())
        { anchorable.ToggleAutoHide(); count++; }
        return count;
    }

    private void WindowMenuPanesOpened()
    {
        var query = ActiveQuery;
        ShowResultsPaneMenu.IsChecked = query?.IsResultsPaneVisible == true;
        SplitTabMenu.Header = query?.IsSplit == true ? "Unsplit Tab" : "Split Tab";
        SplitTabMenu.IsEnabled = query is not null;
        var content = ActiveLayoutContent();
        DockMenu.IsEnabled = content is LayoutAnchorable { IsAutoHidden: true } || content?.IsFloating == true;
        AutoHideMenu.IsEnabled = content is LayoutAnchorable { CanAutoHide: true, IsAutoHidden: false };
        MoveToNewWindowMenu.IsEnabled = content is { IsFloating: false, CanFloat: true };
        ApplyLayoutMenu.Items.Clear();
        var saved = WindowLayouts.Load();
        for (int i = 0; i < saved.Count; i++)
        {
            var layout = saved[i];
            var item = new MenuItem { Header = $"_{(i + 1) % 10} {layout.Name}" };
            item.Click += (_, _) => ApplyWindowLayout(layout.Name);
            ApplyLayoutMenu.Items.Add(item);
        }
        if (saved.Count == 0) ApplyLayoutMenu.Items.Add(new MenuItem { Header = "No Saved Layouts", IsEnabled = false });
    }

    private void SaveWindowLayoutClicked(object sender, RoutedEventArgs e) => SaveWindowLayout();
    /// <summary>Save Window Layout: the current docking layout under a name (Layout N by default); the same name replaces it.</summary>
    internal bool SaveWindowLayout()
    {
        string suggestion = "Layout " + (WindowLayouts.Load().Count + 1);
        string? name = (LayoutNamePrompt ?? (s => { var d = new LayoutNameDialog("Save Window Layout", s) { Owner = this }; return d.ShowDialog() == true ? d.LayoutName : null; }))(suggestion)?.Trim();
        if (string.IsNullOrEmpty(name)) return false;
        try
        {
            using var writer = new StringWriter();
            new XmlLayoutSerializer(DockManager).Serialize(writer);
            WindowLayouts.Put(new WindowLayout(name, writer.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { ShowError(ex); return false; }
        Status.Text = $"Window layout {name} saved.";
        return true;
    }

    /// <summary>Apply Window Layout: tool windows go where the layout had them; every open document stays open, in the first document pane.</summary>
    internal bool ApplyWindowLayout(string name)
    {
        if (WindowLayouts.Load().FirstOrDefault(l => l.Name == name) is not { } layout) return false;
        var openDocuments = DockManager.Layout.Descendents().OfType<LayoutDocument>().ToList();
        var active = DockManager.ActiveContent;
        var tools = new Dictionary<string, object>();
        foreach (var anchorable in DockManager.Layout.Descendents().OfType<LayoutAnchorable>().ToList())
            if (anchorable.ContentId is { } id && anchorable.Content is { } content) { tools[id] = content; anchorable.Content = null; }
        try
        {
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                if (args.Model is LayoutAnchorable && args.Model.ContentId is { } id)
                    args.Content = tools.TryGetValue(id, out var content) ? content : id == "explorer" ? ExplorerContent : id == RegisteredServersId ? RegisteredServers : id == TemplateExplorerId ? TemplateExplorer : id == FindResultsId ? FindResults : id == PropertiesWindowId ? PropertiesContent : ToolContentFor(id);
                if (args.Content is null) args.Cancel = true; // Documents come back below, the ones open now.
            };
            using var reader = new StringReader(layout.Xml);
            serializer.Deserialize(reader);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            ShowError(new InvalidOperationException($"The window layout {name} could not be applied: {ex.Message}", ex));
            ResetLayout(this, new RoutedEventArgs());
            return false;
        }
        // Object Explorer always exists, even if the saved layout somehow lost it.
        if (!DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Any(a => a.ContentId == "explorer"))
            new LayoutAnchorable { Title = "Object Explorer", ContentId = "explorer", Content = ExplorerContent, CanClose = false }.AddToLayout(DockManager, AnchorableShowStrategy.Left);
        var pane = DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (pane is null)
        {
            pane = new LayoutDocumentPane();
            if (DockManager.Layout.RootPanel is { } root) root.Children.Add(pane); else DockManager.Layout.RootPanel = new LayoutPanel(pane);
        }
        foreach (var document in openDocuments) pane.Children.Add(document);
        if (openDocuments.FirstOrDefault(d => d.Content == active) is { } again) again.IsActive = true;
        Status.Text = $"Window layout {name} applied.";
        return true;
    }

    private void ManageWindowLayoutsClicked(object sender, RoutedEventArgs e) => new ManageLayoutsDialog(WindowLayouts) { Owner = this }.ShowDialog();
}
