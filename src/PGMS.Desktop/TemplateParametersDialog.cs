using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace PGMS.Desktop;

/// <summary>Specify Values for Template Parameters (Query menu, Ctrl+Shift+M), as in SSMS 22.11: a Parameter/Type/Value grid where only Value is edited.</summary>
public sealed class TemplateParametersDialog : Window
{
    public sealed class Row(SqlTemplateParameter parameter)
    {
        public string Parameter { get; } = parameter.Name;
        public string Type { get; } = parameter.Type;
        public string Value { get; set; } = parameter.Value;
    }

    private readonly DataGrid grid = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false, SelectionUnit = DataGridSelectionUnit.Cell,
        HeadersVisibility = DataGridHeadersVisibility.Column, Margin = new Thickness(0, 0, 0, 12), MinHeight = 160
    };
    public IReadOnlyList<Row> Rows { get; }
    public DataGrid Grid => grid;

    public TemplateParametersDialog(IReadOnlyList<SqlTemplateParameter> parameters)
    {
        Title = "Specify Values for Template Parameters";
        Width = 440; Height = 312; MinWidth = 360; MinHeight = 240; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResizeWithGrip;
        Rows = parameters.Select(p => new Row(p)).ToArray();
        grid.Columns.Add(new DataGridTextColumn { Header = "Parameter", Binding = new Binding(nameof(Row.Parameter)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(Row.Type)), IsReadOnly = true, Width = new DataGridLength(0.7, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(Row.Value)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        grid.ItemsSource = Rows;
        AutomationProperties.SetName(grid, "Template parameters");
        var ok = new Button { Content = "OK", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 76, IsCancel = true };
        ok.Click += (_, _) => { grid.CommitEdit(DataGridEditingUnit.Row, true); DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(grid);
        Content = root;
        Loaded += (_, _) =>
        {
            if (Rows.Count == 0) return;
            grid.Focus();
            grid.CurrentCell = new DataGridCellInfo(Rows[0], grid.Columns[2]);
        };
    }

    public IReadOnlyDictionary<string, string> Values => Rows.ToDictionary(r => r.Parameter, r => r.Value);
}
