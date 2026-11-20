using System.Windows;
using System.Windows.Controls;

namespace PGMS.Desktop;

/// <summary>One page of Tools ▸ Options for Search (M1114): its path in the tree ("Environment > Fonts and Colors") and the captions of its settings.</summary>
public sealed record OptionsPageIndex(string Path, IReadOnlyList<string> Settings);

public partial class OptionsDialog
{
    /// <summary>Every leaf page with the captions shown on it (check boxes, labels, group titles, short texts), read by showing each page in turn
    /// without displaying the dialog. Search lists them as SSMS 22.11 lists settings next to commands (observed in M1114).</summary>
    internal IReadOnlyList<OptionsPageIndex> SearchIndex()
    {
        var result = new List<OptionsPageIndex>();
        foreach (var (path, page) in LeafPages())
        {
            page.IsSelected = true;
            var captions = new List<string>();
            Collect(Content as DependencyObject, captions);
            result.Add(new(path, captions.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return result;
    }

    /// <summary>Selects a page by its path from <see cref="SearchIndex"/>; false when there is no such page.</summary>
    internal bool SelectPagePath(string path)
    {
        foreach (var (pagePath, page) in LeafPages())
            if (string.Equals(pagePath, path, StringComparison.OrdinalIgnoreCase) || string.Equals(PathIn(page, translated: true), path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(PathIn(page, translated: false), path, StringComparison.OrdinalIgnoreCase)) { page.IsSelected = true; return true; }
        return false;
    }

    /// <summary>The path of a page in English or in the interface language, whether or not the tree was translated yet (M1129): the Search index
    /// is built from a translated window, and the window it opens may not be translated when the page is selected.</summary>
    private static string PathIn(TreeViewItem page, bool translated)
    {
        string Name(TreeViewItem item) { var english = Localization.EnglishHeader(item, item.Header) as string ?? ""; return FeatureSearch.Text(translated ? Localization.T(english) : english); }
        return page.Parent is TreeViewItem category ? Name(category) + " > " + Name(page) : Name(page);
    }

    private IEnumerable<(string Path, TreeViewItem Page)> LeafPages()
    {
        foreach (var category in Pages.Items.OfType<TreeViewItem>())
        {
            string name = FeatureSearch.Text(category.Header);
            if (category.Items.Count == 0) { yield return (name, category); continue; }
            foreach (var page in category.Items.OfType<TreeViewItem>()) yield return (name + " > " + FeatureSearch.Text(page.Header), page);
        }
    }

    /// <summary>Captions of the visible part of the page: the tree, buttons and long explanations are left out.</summary>
    private void Collect(DependencyObject? node, List<string> captions)
    {
        if (node is null || node == Pages || node is Button) return;
        if (node is UIElement { Visibility: not Visibility.Visible }) return;
        string? text = node switch
        {
            CheckBox { Content: string s } => s,
            RadioButton { Content: string s } => s,
            Label { Content: string s } => s,
            GroupBox { Header: string s } => s,
            TextBlock t when t.Parent is not (CheckBox or RadioButton or Label or Button) => t.Text,
            _ => null,
        };
        if (text is not null)
        {
            string clean = FeatureSearch.Text(text).Trim().TrimEnd(':').Trim();
            if (clean.Length is > 1 and <= 80) captions.Add(clean);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Collect(child, captions);
    }
}
