using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Showplan Comparison (PGMS-084, M1119): the two plans side by side, each with its caption, and below them the differences
/// (statement totals, then operators in display order). Rows with a difference are tinted; selecting an operator row selects it in its plan.</summary>
public sealed class ExecutionPlanComparisonView : UserControl
{
    public ExecutionPlanView First { get; } = new();
    public ExecutionPlanView Second { get; } = new();
    public IReadOnlyList<PlanDifference> Differences { get; }
    internal DataGrid DifferenceGrid { get; }
    internal CheckBox OnlyDifferencesBox { get; }

    public ExecutionPlanComparisonView(IReadOnlyList<ExecutionPlanStatement> first, string firstCaption, IReadOnlyList<ExecutionPlanStatement> second, string secondCaption, IReadOnlyList<PlanDifference> differences)
    {
        Differences = differences;
        var layout = new Grid();
        layout.SetResourceReference(Panel.BackgroundProperty, "Pgms.ContentBackground");
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 80 });
        var plans = new Grid();
        plans.ColumnDefinitions.Add(new ColumnDefinition());
        plans.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        plans.ColumnDefinitions.Add(new ColumnDefinition());
        plans.Children.Add(Pane(First, "Plan 1: " + firstCaption, 0));
        var middle = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, IsTabStop = false }; Grid.SetColumn(middle, 1); plans.Children.Add(middle);
        plans.Children.Add(Pane(Second, "Plan 2: " + secondCaption, 2));
        layout.Children.Add(plans);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, IsTabStop = false }; Grid.SetRow(splitter, 1); layout.Children.Add(splitter);

        DifferenceGrid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
        System.Windows.Automation.AutomationProperties.SetName(DifferenceGrid, "Plan differences");
        DataGridRowNames.SetEnabled(DifferenceGrid, true);
        DifferenceGrid.Columns.Add(new DataGridTextColumn { Header = "Area", Binding = new Binding(nameof(PlanDifference.Area)), Width = 80 });
        DifferenceGrid.Columns.Add(new DataGridTextColumn { Header = "Item", Binding = new Binding(nameof(PlanDifference.Item)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), FontFamily = new FontFamily("Consolas") });
        DifferenceGrid.Columns.Add(new DataGridTextColumn { Header = "Plan 1", Binding = new Binding(nameof(PlanDifference.First)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        DifferenceGrid.Columns.Add(new DataGridTextColumn { Header = "Plan 2", Binding = new Binding(nameof(PlanDifference.Second)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        DifferenceGrid.Columns.Add(new DataGridTextColumn { Header = "Difference", Binding = new Binding(nameof(PlanDifference.Difference)), Width = 120 });
        DifferenceGrid.LoadingRow += (_, e) => e.Row.Background = e.Row.Item is PlanDifference { Difference.Length: > 0 } ? Tint : Brushes.Transparent;
        DifferenceGrid.SelectionChanged += (_, _) => SelectOperator(DifferenceGrid.SelectedItem as PlanDifference);
        OnlyDifferencesBox = new CheckBox { Content = "Show only _differences", Margin = new Thickness(0, 0, 0, 4) };
        OnlyDifferencesBox.Click += (_, _) => Filter();
        var bottom = new DockPanel { Margin = new Thickness(6, 4, 6, 6) };
        var heading = new TextBlock { Text = "Differences", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 4) };
        var top = new StackPanel { Orientation = Orientation.Horizontal }; top.Children.Add(heading); top.Children.Add(OnlyDifferencesBox);
        var legend = new TextBlock { Text = "Shaded operators: amber, different values; rose, only in that plan.", Margin = new Thickness(16, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        legend.SetResourceReference(TextBlock.ForegroundProperty, "Pgms.SecondaryText");
        top.Children.Add(legend);
        DockPanel.SetDock(top, Dock.Top); bottom.Children.Add(top); bottom.Children.Add(DifferenceGrid);
        Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
        Content = layout;
        First.Show(first); Second.Show(second);
        MarkDifferences();
        Filter();
    }

    /// <summary>Shading of the boxes, as SSMS shades regions (B1 review, M1140): an operator found in both plans with other values is amber in both;
    /// an operator found only in one plan is rose in that plan. Same operators stay unshaded.</summary>
    internal static readonly Brush ChangedShade = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xF2, 0xB7, 0x05))), OnlyShade = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xE8, 0x4A, 0x5F)));

    private void MarkDifferences()
    {
        var a = ExecutionPlanSearch.Operators(First.Statements).Where(o => !o.IsStatement).ToList();
        var b = ExecutionPlanSearch.Operators(Second.Statements).Where(o => !o.IsStatement).ToList();
        int x = 0, y = 0;
        foreach (var row in Differences.Where(d => d.Area == "Operator"))
        {
            var left = row.First.Length > 0 ? a.ElementAtOrDefault(x++) : null;
            var right = row.Second.Length > 0 ? b.ElementAtOrDefault(y++) : null;
            if (row.Difference == QueryPlanComparison.Changed)
            {
                if (left is not null) First.Mark(left, ChangedShade, Localization.T("Different values in plan 2"));
                if (right is not null) Second.Mark(right, ChangedShade, Localization.T("Different values in plan 1"));
            }
            else if (row.Difference == QueryPlanComparison.OnlyFirst && left is not null) First.Mark(left, OnlyShade, Localization.T("Only in plan 1"));
            else if (row.Difference == QueryPlanComparison.OnlySecond && right is not null) Second.Mark(right, OnlyShade, Localization.T("Only in plan 2"));
        }
    }

    private static readonly Brush Tint = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xF2, 0xB7, 0x05)));
    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }

    private static DockPanel Pane(ExecutionPlanView view, string caption, int column)
    {
        var pane = new DockPanel();
        var title = new TextBlock { Text = caption, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 6, 4), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = caption };
        DockPanel.SetDock(title, Dock.Top);
        pane.Children.Add(title); pane.Children.Add(view);
        Grid.SetColumn(pane, column);
        return pane;
    }

    internal void Filter() => DifferenceGrid.ItemsSource = OnlyDifferencesBox.IsChecked == true ? Differences.Where(d => d.Difference.Length > 0).ToList() : Differences;

    /// <summary>Operator rows select the operator with that name in each plan that has it (the first not yet passed, in display order).</summary>
    private void SelectOperator(PlanDifference? row)
    {
        if (row is not { Area: "Operator" }) return;
        int index = Differences.Where(d => d.Area == "Operator").ToList().IndexOf(row);
        var operatorRows = Differences.Where(d => d.Area == "Operator").ToList();
        int firstIndex = operatorRows.Take(index).Count(d => d.First.Length > 0), secondIndex = operatorRows.Take(index).Count(d => d.Second.Length > 0);
        if (row.First.Length > 0 && ExecutionPlanSearch.Operators(First.Statements).Where(o => !o.IsStatement).ElementAtOrDefault(firstIndex) is { } a) First.Select(a);
        if (row.Second.Length > 0 && ExecutionPlanSearch.Operators(Second.Statements).Where(o => !o.IsStatement).ElementAtOrDefault(secondIndex) is { } b) Second.Select(b);
    }
}
