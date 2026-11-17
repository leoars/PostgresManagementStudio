using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public enum MaintenanceKind { Vacuum, Analyze, Refresh }

/// <summary>VACUUM, ANALYZE and REFRESH MATERIALIZED VIEW text for Object Explorer ▸ Maintenance (PGMS-062, M1089). A null relation means the whole database.</summary>
public static class MaintenanceCommands
{
    public static string Target(string? schema, string? relation) => relation is null ? "" : " " + SqlIdentifier.Quote(schema!) + "." + SqlIdentifier.Quote(relation);

    public static string Vacuum(string? schema, string? relation, bool full = false, bool freeze = false, bool analyze = false, bool verbose = false)
    {
        var options = new List<string>();
        if (full) options.Add("FULL");
        if (freeze) options.Add("FREEZE");
        if (verbose) options.Add("VERBOSE");
        if (analyze) options.Add("ANALYZE");
        return "VACUUM" + (options.Count == 0 ? "" : " (" + string.Join(", ", options) + ")") + Target(schema, relation) + ";";
    }

    public static string Analyze(string? schema, string? relation, bool verbose = false) => "ANALYZE" + (verbose ? " (VERBOSE)" : "") + Target(schema, relation) + ";";

    /// <summary>CONCURRENTLY needs a unique index on the view and cannot be combined with WITH NO DATA.</summary>
    public static string Refresh(string schema, string relation, bool concurrently = false, bool withData = true)
    {
        if (concurrently && !withData) throw new ArgumentException("CONCURRENTLY cannot be used with WITH NO DATA.");
        return "REFRESH MATERIALIZED VIEW" + (concurrently ? " CONCURRENTLY" : "") + Target(schema, relation) + (withData ? "" : " WITH NO DATA") + ";";
    }
}

/// <summary>Options of one maintenance command with the statement it will open in a query editor, where it is reviewed and executed (never run from here).</summary>
public sealed class MaintenanceDialog : Window
{
    private readonly MaintenanceKind kind;
    private readonly string? schema, relation;
    private readonly TextBox preview = new() { IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, MinHeight = 48, Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBlock error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private readonly Button ok = new() { Content = "_Open in Query Editor", Padding = new Thickness(12, 3, 12, 3), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
    public CheckBox Full { get; } = Box("_FULL (rewrites the table; locks it exclusively)");
    public CheckBox Freeze { get; } = Box("F_REEZE");
    public CheckBox AnalyzeToo { get; } = Box("_ANALYZE (update planner statistics)");
    public CheckBox Verbose { get; } = Box("_VERBOSE (report progress in Messages)");
    public CheckBox Concurrently { get; } = Box("_CONCURRENTLY (needs a unique index; readers are not blocked)");
    public CheckBox NoData { get; } = Box("WITH _NO DATA (leaves the view unscannable)");
    public string Sql { get; private set; } = "";

    private static CheckBox Box(string text) => new() { Content = text, Margin = new Thickness(0, 3, 0, 3) };

    public MaintenanceDialog(MaintenanceKind kind, string database, string? schema, string? relation)
    {
        this.kind = kind; this.schema = schema; this.relation = relation;
        Title = kind switch { MaintenanceKind.Vacuum => "Vacuum", MaintenanceKind.Analyze => "Analyze", _ => "Refresh Materialized View" };
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = relation is null ? $"Database: {database}" : $"{database} › {schema}.{relation}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });
        CheckBox[] boxes = kind switch { MaintenanceKind.Vacuum => [Full, Freeze, AnalyzeToo, Verbose], MaintenanceKind.Analyze => [Verbose], _ => [Concurrently, NoData] };
        foreach (var box in boxes) { panel.Children.Add(box); box.Checked += (_, _) => Update(); box.Unchecked += (_, _) => Update(); }
        panel.Children.Add(new TextBlock { Text = "Statement:", Margin = new Thickness(0, 10, 0, 0) });
        AutomationProperties.SetName(preview, "Statement");
        panel.Children.Add(preview);
        panel.Children.Add(error);
        panel.Children.Add(new TextBlock { Text = "The statement opens in a query editor; nothing runs until you execute it there.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.SystemColors.GrayTextBrush, Margin = new Thickness(0, 0, 0, 10) });
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        Content = panel;
        Update();
    }

    private void Update()
    {
        try
        {
            Sql = kind switch
            {
                MaintenanceKind.Vacuum => MaintenanceCommands.Vacuum(schema, relation, Full.IsChecked == true, Freeze.IsChecked == true, AnalyzeToo.IsChecked == true, Verbose.IsChecked == true),
                MaintenanceKind.Analyze => MaintenanceCommands.Analyze(schema, relation, Verbose.IsChecked == true),
                _ => MaintenanceCommands.Refresh(schema!, relation!, Concurrently.IsChecked == true, NoData.IsChecked != true)
            };
            error.Text = ""; ok.IsEnabled = true;
        }
        catch (ArgumentException ex) { Sql = ""; error.Text = ex.Message; ok.IsEnabled = false; }
        preview.Text = Sql;
    }

    public bool CanAccept => ok.IsEnabled;
}
