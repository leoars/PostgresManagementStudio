using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Object Explorer ▸ Maintenance (PGMS-062, M1089): VACUUM, ANALYZE and REFRESH MATERIALIZED VIEW reviewed in a dialog and opened in a query editor.</summary>
public partial class MainWindow
{
    /// <summary>Shows the dialog; replaced by tests. True means Open in Query Editor.</summary>
    internal Func<MaintenanceDialog, bool>? MaintenancePrompt { get; set; }

    private MenuItem MaintenanceMenu(CatalogEntry entry)
    {
        var menu = new MenuItem { Header = "_Maintenance", Name = "ExplorerMaintenance" };
        void Item(string header, string name, MaintenanceKind kind)
        {
            var item = new MenuItem { Header = header, Name = name };
            item.Click += async (_, _) => await OpenMaintenanceAsync(entry, kind);
            menu.Items.Add(item);
        }
        if (entry.Kind == CatalogKind.Database || entry.RelationType is "r" or "p" or "m") { Item("_Vacuum…", "ExplorerVacuum", MaintenanceKind.Vacuum); Item("_Analyze…", "ExplorerAnalyze", MaintenanceKind.Analyze); }
        if (entry.RelationType == "m") Item("_Refresh Materialized View…", "ExplorerRefreshMatview", MaintenanceKind.Refresh);
        return menu;
    }

    internal async Task<QueryView?> OpenMaintenanceAsync(CatalogEntry entry, MaintenanceKind kind)
    {
        if (catalogService is not { } catalog) { Status.Text = "Connect before maintenance."; return null; }
        bool database = entry.Kind == CatalogKind.Database;
        string databaseName = database ? entry.Database ?? entry.Name : entry.Database ?? "";
        var dialog = new MaintenanceDialog(kind, databaseName, database ? null : entry.Schema, database ? null : entry.Name) { Owner = this };
        bool accepted = MaintenancePrompt?.Invoke(dialog) ?? dialog.ShowDialog() == true;
        if (!accepted || dialog.Sql.Length == 0) return null;
        string connection = catalog.ConnectionFor(databaseName);
        var before = OpenQueries().ToHashSet();
        await OpenAdministrativeScriptAsync(connection, dialog.Sql);
        Status.Text = "Review the statement and execute it (F5). Nothing has run yet.";
        return OpenQueries().FirstOrDefault(q => !before.Contains(q));
    }
}
