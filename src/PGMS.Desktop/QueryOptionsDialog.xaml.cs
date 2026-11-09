using System.Windows;
using System.Windows.Controls;

namespace PGMS.Desktop;

/// <summary>Query ▸ Query Options: what is chosen here applies only to one query tab, from its next execution on.</summary>
public partial class QueryOptionsDialog : Window
{
    /// <summary>The accepted options, after OK.</summary>
    internal QueryOptions? Result { get; private set; }

    public QueryOptionsDialog(QueryOptions options)
    {
        InitializeComponent();
        OptionPages.Load(options);
    }

    private void PageChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // A category node shows its first page, as the SSMS tree does.
        var node = e.NewValue as TreeViewItem;
        if (node is { Items.Count: > 0 } && node.Items[0] is TreeViewItem first) node = first;
        // General is selected in XAML before the pages exist; it is already the visible page.
        if (OptionPages is not null && node?.Tag is string tag) OptionPages.ShowPage(int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture));
    }

    internal void Accept(object sender, RoutedEventArgs e)
    {
        if (OptionPages.TryRead() is not { } read)
        {
            ((TreeViewItem[])[ExecutionGeneralNode, ExecutionAdvancedNode, ResultsGridNode, ResultsTextNode])[OptionPages.RejectedPage].IsSelected = true;
            return;
        }
        Result = read;
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); } // shown without ShowDialog
    }
}
