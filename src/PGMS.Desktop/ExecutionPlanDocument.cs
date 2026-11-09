using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>An execution plan in a document tab of its own: Show Execution Plan In New Tab, or a saved plan file. Keeps no connection to any session.</summary>
public sealed class ExecutionPlanDocument : UserControl
{
    private readonly DataGrid properties;
    public ExecutionPlanView Diagram { get; } = new();
    /// <summary>Asks where Save writes; null cancels. Replaced by tests.</summary>
    internal Func<string?>? PickSaveFile { get; set; }

    public ExecutionPlanDocument(IReadOnlyList<ExecutionPlanStatement> statements)
    {
        properties = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, RowHeight = 23, HeadersVisibility = DataGridHeadersVisibility.Column };
        System.Windows.Automation.AutomationProperties.SetName(properties, "Selected plan node properties");
        DataGridRowNames.SetEnabled(properties, true);
        properties.Columns.Add(new DataGridTextColumn { Header = "Property", Binding = new Binding("Key"), Width = 130 });
        properties.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var layout = new Grid { Background = System.Windows.Media.Brushes.White };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); Grid.SetColumn(properties, 2);
        layout.Children.Add(Diagram); layout.Children.Add(splitter); layout.Children.Add(properties);
        Content = layout;
        Diagram.SelectionChanged += (_, _) => properties.ItemsSource = Diagram.SelectedOperator?.Properties;
        Diagram.SaveRequested += async (_, _) => await SaveSelectedAsync();
        Diagram.Show(statements);
    }

    /// <summary>Set by the host to report an outcome in the status bar.</summary>
    internal Action<string>? Report { get; set; }

    internal async Task SaveSelectedAsync()
    {
        if (Diagram.SelectedStatement?.Source is not { } plan || AskFile() is not { } path) return;
        try { await QueryPlanFiles.SaveAsync(plan, path, overwrite: true); Report?.Invoke("Plan snapshot exported as native PostgreSQL JSON."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or InvalidDataException) { Report?.Invoke("Plan export failed: " + ex.Message); }
    }

    private string? AskFile()
    {
        if (PickSaveFile is { } pick) return pick();
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "PostgreSQL plan JSON (*.json)|*.json", DefaultExt = ".json", FileName = "plan.json" };
        return picker.ShowDialog(Window.GetWindow(this)) == true ? picker.FileName : null;
    }
}
