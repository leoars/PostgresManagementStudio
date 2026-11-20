using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>
/// B1 review (M1139): titles of tabs and tool windows composed in code ("{host} - Activity Monitor", "Showplan Comparison", "Recovered - draft.sql")
/// are shown in the interface language. The layout keeps the English title, which the code reads back (recovery, Close All, Window ▸ Windows…);
/// only what is drawn changes: every TextBlock that the docking templates bind to a layout element's Title gets a converter.
/// A query tab shows its file or query name as it is; only the "Recovered - " mark of a restored draft is translated.
/// </summary>
public static class DockTitleTranslation
{
    /// <summary>Follows a docking manager: after each change of its layout or active tab (and when it loads), the titles drawn since then get
    /// the translation. WPF raises Loaded only for elements with their own handlers, so the docking chrome is swept instead, without going into
    /// the documents' content.</summary>
    public static void Watch(AvalonDock.DockingManager manager)
    {
        if (Localization.Current == "en") return;
        bool scheduled = false;
        void Schedule()
        {
            if (scheduled) return;
            scheduled = true;
            manager.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { scheduled = false; Sweep(manager); });
        }
        LayoutRoot? root = null;
        void Follow()
        {
            if (root is not null) root.Updated -= OnUpdated;
            root = manager.Layout;
            if (root is not null) root.Updated += OnUpdated;
        }
        void OnUpdated(object? sender, EventArgs e) => Schedule();
        Follow();
        manager.LayoutChanged += (_, _) => { Follow(); Schedule(); };
        manager.ActiveContentChanged += (_, _) => Schedule();
        manager.Loaded += (_, _) => Schedule();
        Schedule();
    }

    /// <summary>Attaches the translation to the titles drawn under <paramref name="root"/>.</summary>
    internal static void Refresh(DependencyObject root) { if (Localization.Current != "en") Sweep(root); }

    private static void Sweep(DependencyObject node)
    {
        if (node is TextBlock text) { Attach(text); return; }
        // The content of documents and tool windows (queries, grids, designers) is not docking chrome.
        if (node is UserControl && node.GetType().Namespace?.StartsWith("AvalonDock", StringComparison.Ordinal) != true) return;
        if (node is TreeView or DataGrid or ListBox or System.Windows.Controls.Primitives.TextBoxBase or ICSharpCode.AvalonEdit.TextEditor) return;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) Sweep(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
    }

    private static void Attach(TextBlock text)
    {
        if (BindingOperations.GetBinding(text, TextBlock.TextProperty) is not { Path.Path: "Title" or "Model.Title" } binding || binding.Converter is not null) return;
        // Only the docking controls (tabs, captions, document lists, navigator); the data context may arrive after Loaded.
        if (!InDocking(text)) return;
        var translated = new Binding(binding.Path.Path) { Mode = BindingMode.OneWay, Converter = Converter, ConverterParameter = text };
        if (binding.Source is not null) translated.Source = binding.Source;
        if (binding.RelativeSource is not null) translated.RelativeSource = binding.RelativeSource;
        if (binding.ElementName is not null) translated.ElementName = binding.ElementName;
        text.SetBinding(TextBlock.TextProperty, translated);
    }

    private static bool InDocking(DependencyObject element)
    {
        for (var node = System.Windows.Media.VisualTreeHelper.GetParent(element); node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node.GetType().Namespace?.StartsWith("AvalonDock", StringComparison.Ordinal) == true) return true;
            if (node is QueryView or UserControl) return false; // inside a document's own content
        }
        return false;
    }

    private static readonly TitleConverter Converter = new();

    /// <summary>The title as drawn: the change mark kept, query tabs untouched except the recovery mark.</summary>
    public static string Display(string title, bool queryTab)
    {
        if (Localization.Current == "en" || title.Length == 0) return title;
        bool changed = title.EndsWith('*');
        string core = changed ? title[..^1] : title;
        const string recovered = "Recovered - ";
        if (core.StartsWith(recovered, StringComparison.Ordinal))
            core = Localization.T("Recovered - {0}").Replace("{0}", core[recovered.Length..], StringComparison.Ordinal);
        else if (!queryTab) core = Localization.Message(core);
        return changed ? core + "*" : core;
    }

    private sealed class TitleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string title) return value;
            var context = (parameter as FrameworkElement)?.DataContext;
            var content = context switch { LayoutContent c => c, AvalonDock.Controls.LayoutItem i => i.LayoutElement as LayoutContent, _ => null };
            return Display(title, content?.Content is QueryView);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
