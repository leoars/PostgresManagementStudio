using System.Data;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PGMS.Desktop;

/// <summary>Short UI Automation names for DataGrid rows: records, result DataViews, editable grids and the new-row placeholder. No database.</summary>
internal static class DataGridRowNameChecks
{
    private sealed record Sample(long Id, string Database, bool Enabled, string Hidden);

    private sealed class Editable { public string Label { get; set; } = ""; public string Value { get; set; } = ""; }

    // Name exposed to UI Automation clients for the row that displays the item.
    public static string RowName(DataGrid grid, object item)
    {
        grid.ScrollIntoView(item); grid.UpdateLayout();
        var peer = (DataGridAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(grid);
        peer.ResetChildrenCache();
        return peer.GetChildren().OfType<ItemAutomationPeer>().First(p => Equals(p.Item, item)).GetName();
    }

    private static async Task Settle(Window window) =>
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    public static async Task RunAsync(Action<bool, string> check)
    {
        var records = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 200 };
        records.Columns.Add(new DataGridTextColumn { Header = "Query ID", Binding = new Binding(nameof(Sample.Id)) });
        records.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = "Database" }, Binding = new Binding(nameof(Sample.Database)) });
        records.Columns.Add(new DataGridCheckBoxColumn { Header = "Enabled", Binding = new Binding(nameof(Sample.Enabled)) });
        records.Columns.Add(new DataGridTextColumn { Header = "Hidden", Binding = new Binding(nameof(Sample.Hidden)) });
        string longName = new string('x', 62) + "😀tail";
        var items = new[] { new Sample(42, "app", true, "secret"), new Sample(7, "", false, "secret"), new Sample(9, longName, true, "secret"), new Sample(10, "line1\r\nline2", false, "secret") };
        records.ItemsSource = items;
        var window = new Window { Content = records, Width = 600, Height = 300, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Show();
        try
        {
            check(RowName(records, items[0]) == items[0].ToString(), "Without the attached property WPF announces the record ToString");
            DataGridRowNames.SetEnabled(records, true); await Settle(window);
            string first = RowName(records, items[0]);
            check(first == "Query ID 42, Database app, Enabled Yes", "Record row name uses the first three visible columns: " + first);
            check(!first.Contains("secret") && !first.Contains(nameof(Sample)), "Row name omits fields beyond the column limit and the record type");
            check(RowName(records, items[1]) == "Query ID 7, Database (empty), Enabled No", "Empty text and false checkbox are spoken explicitly");
            string truncated = RowName(records, items[2]);
            check(truncated.Contains(new string('x', 62) + "…") && !truncated.Contains("tail") && !truncated.Contains('\uD83D'), "Long values are cut without splitting a surrogate pair");
            check(RowName(records, items[3]).Contains("Database line1 line2"), "Line breaks in values become spaces");
            DataGridRowNames.SetColumnCount(records, 1); await Settle(window);
            check(RowName(records, items[0]) == "Query ID 42", "Column count can shorten the name");
            DataGridRowNames.SetColumnCount(records, 3);
            records.Columns[1].DisplayIndex = 0; records.Columns[2].Visibility = Visibility.Collapsed;
            DataGridRowNames.RefreshRealizedRows(records); await Settle(window);
            check(RowName(records, items[0]) == "Database app, Query ID 42, Hidden secret", "Name follows display order and skips hidden columns");
            DataGridRowNames.SetEnabled(records, false); await Settle(window);

            var table = new DataTable();
            table.Columns.Add(new DataColumn("c0", typeof(object)) { Caption = "value" });
            table.Columns.Add(new DataColumn("c1", typeof(object)) { Caption = "value" });
            table.Rows.Add(1, DBNull.Value); table.Rows.Add("", "texto");
            var results = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
            foreach (DataColumn column in table.Columns)
                results.Columns.Add(new DataGridTextColumn { Header = column.Caption, Binding = new Binding($"[{column.ColumnName}]") { Converter = new DatabaseValueConverter() } });
            results.ItemsSource = table.DefaultView; DataGridRowNames.SetEnabled(results, true);
            window.Content = results; await Settle(window);
            var view = table.DefaultView;
            string nullRow = RowName(results, view[0]);
            check(nullRow == "value 1, value NULL", "Result rows use the displayed NULL marker, not DataRowView: " + nullRow);
            check(RowName(results, view[1]) == "value (empty), value texto", "Empty string stays distinct from NULL in the row name");

            var editable = new System.Collections.ObjectModel.ObservableCollection<Editable> { new() { Label = "low", Value = "1" } };
            var edits = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = true, ItemsSource = editable };
            edits.Columns.Add(new DataGridTextColumn { Header = "Key", Binding = new Binding(nameof(Editable.Label)) });
            edits.Columns.Add(new DataGridTextColumn { Header = "Literal", Binding = new Binding(nameof(Editable.Value)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
            DataGridRowNames.SetEnabled(edits, true);
            window.Content = edits; await Settle(window);
            check(RowName(edits, editable[0]) == "Key low, Literal 1", "Editable grid row starts with its values");
            var placeholderRow = (DataGridRow)edits.ItemContainerGenerator.ContainerFromItem(CollectionView.NewItemPlaceholder);
            check(placeholderRow is not null && string.IsNullOrEmpty(AutomationProperties.GetName(placeholderRow)), "New-row placeholder receives no invented name");
            edits.CurrentCell = new DataGridCellInfo(editable[0], edits.Columns[1]); edits.BeginEdit(); await Settle(window);
            var cell = (TextBox)((DataGridCell)edits.Columns[1].GetCellContent(editable[0])!.Parent).Content;
            cell.Text = "99"; edits.CommitEdit(DataGridEditingUnit.Row, true); await Settle(window);
            check(RowName(edits, editable[0]) == "Key low, Literal 99", "Committed edit refreshes the row name");
        }
        finally { window.Close(); }
    }
}
