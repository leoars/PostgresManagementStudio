using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

// Object Explorer context menus per node kind, following the SSMS 22.11 menus recorded in docs/MARCO-M1035.md.
// Only actions PGMS implements are listed; SSMS items without a PostgreSQL equivalent are left out on purpose.
public partial class MainWindow
{
    private void ServerProperties(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before opening server properties."; return; }
        new ServerPropertiesDialog(connection) { Owner = this }.ShowDialog();
    }

    private async void ReloadConfiguration(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) return;
        if (MessageBox.Show(this, "Reload the server configuration (pg_reload_conf)?\nChanges from postgresql.conf and ALTER SYSTEM that do not need a restart take effect for all sessions. Nothing is restarted.", "Reload Configuration", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ReloadConfigurationAsync(connection);
    }
    internal async Task ReloadConfigurationAsync(string connection)
    {
        Status.Text = "Reloading server configuration…";
        try
        {
            var pending = await new ServerPropertiesService(connection).ReloadAsync();
            Status.Text = pending.Count == 0 ? "Server configuration reloaded." : $"Server configuration reloaded. Waiting for a restart: {string.Join(", ", pending)}.";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.IO.IOException or InvalidOperationException) { Status.Text = "Reload failed: " + (ex is Npgsql.PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message); }
    }

    // Replaceable in tests, so the suite never touches a real Windows service or raises a UAC prompt.
    internal Func<string?, int, (LocalPostgresService? Service, LocalServiceState State)> LocalServiceResolver { get; set; } = (host, port) =>
        LocalServerServices.Find(host, port) is { } service ? (service, LocalServerServices.QueryState(service.Name)) : (null, LocalServiceState.Unknown);
    internal Func<string, LocalServiceAction, Task<LocalServiceState>> LocalServiceController { get; set; } = (name, action) => LocalServerServices.ControlAsync(name, action);
    internal Func<string, bool>? ConfirmServiceAction { get; set; }
    private bool ConfirmService(string message) => ConfirmServiceAction?.Invoke(message)
        ?? MessageBox.Show(this, message, "PostgreSQL Service", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private (LocalPostgresService? Service, LocalServiceState State) ResolveLocalService(string connection)
    {
        var c = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        return LocalServiceResolver(c.Host, c.Port);
    }

    /// <summary>Start, Stop or Restart the local service behind the selected server. Stop disconnects Object Explorer; Restart reconnects it.
    /// Query windows keep their (now broken) sessions and report the error on their next statement.</summary>
    internal async Task ControlLocalServiceAsync(LocalServiceAction action)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before controlling the server service."; return; }
        var c = new Npgsql.NpgsqlConnectionStringBuilder(connection);
        await ControlLocalServiceAsync(c.Host, c.Port, action);
    }

    /// <summary>Also used by Registered Servers ▸ Service Control. Object Explorer is disconnected or reconnected only when it shows that server.</summary>
    internal async Task ControlLocalServiceAsync(string? host, int port, LocalServiceAction action)
    {
        var (service, _) = LocalServiceResolver(host, port);
        if (service is null) { Status.Text = "No PostgreSQL Windows service on this computer matches this connection."; return; }
        if (action != LocalServiceAction.Start)
        {
            string verb = action == LocalServiceAction.Stop ? "Stop" : "Restart";
            string effect = action == LocalServiceAction.Stop ? "stopped" : "stopped and started again";
            if (!ConfirmService($"{verb} the PostgreSQL service \"{service.Name}\" on this computer?\nData directory: {service.DataDirectory}\n\nThe server is {effect}: every session is disconnected, including open query windows. Windows asks for administrator permission.")) return;
        }
        Status.Text = $"{action} {service.Name}: waiting for administrator permission and the service…";
        try
        {
            var state = await LocalServiceController(service.Name, action);
            bool explorerOnServer = explorerConnection is { } shown && new Npgsql.NpgsqlConnectionStringBuilder(shown) is var e && e.Port == port && LocalServerServices.IsLocalHost(e.Host);
            if (explorerOnServer && action == LocalServiceAction.Stop) DisconnectExplorer();
            else if (explorerOnServer && explorerConnection is { } explorer && state == LocalServiceState.Running) await ConnectExplorerAsync(explorer);
            Status.Text = $"Service {service.Name} is {LocalServerServices.Describe(state)}.";
        }
        catch (OperationCanceledException) { Status.Text = $"{action} {service.Name} cancelled: administrator permission was not given."; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Status.Text = $"{action} {service.Name} failed: {ex.Message}"; }
    }

    // SSMS: Register… on the server node opens New Server Registration filled in from the connection.
    private void RegisterFromExplorer(object sender, RoutedEventArgs e)
    {
        if (explorerConnection is not { } connection) return;
        var dialog = new ConnectionDialog(profilePath: RegisteredServersProfilePath) { Owner = this };
        dialog.Prefill(connection);
        dialog.ShowDialog();
        if (dialog.SavedProfileId is { } id) { ShowRegisteredServers(); RegisteredServers.Reload(selectProfile: id); Status.Text = "Server registered."; }
    }

    private void OpenReportFromExplorer(ServerReportKind kind)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before opening a report."; return; }
        OpenReport(connection, kind);
    }

    private void StartPsql(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before starting psql."; return; }
        if (PsqlLauncher.Find() is not { } psql) { Status.Text = "psql.exe was not found on PATH or under Program Files\\PostgreSQL. Install the PostgreSQL command-line tools."; return; }
        try { System.Diagnostics.Process.Start(PsqlLauncher.CreateStartInfo(connection, psql))?.Dispose(); Status.Text = "psql started in a new console."; }
        catch (System.ComponentModel.Win32Exception ex) { Status.Text = "psql could not start: " + ex.Message; }
    }

    // Built before the popup shows (right click, Shift+F10, menu key) so it never flashes the previous node's items;
    // Opened only builds it when the menu was opened some other way (IsOpen set in code).
    private bool explorerMenuPrepared;
    private void ExplorerMenuOpening(object sender, ContextMenuEventArgs e) { FillExplorerMenu(ExplorerMenu, ExplorerTree.SelectedItem as CatalogNode); explorerMenuPrepared = true; }
    private void ExplorerMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!explorerMenuPrepared) FillExplorerMenu((ContextMenu)sender, ExplorerTree.SelectedItem as CatalogNode);
        explorerMenuPrepared = false;
    }

    internal void FillExplorerMenu(ContextMenu menu, CatalogNode? node)
    {
        menu.Items.Clear();
        void Add(string header, RoutedEventHandler click, string? name = null, bool isDefault = false, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (name is not null) item.Name = name;
            if (isDefault) item.FontWeight = FontWeights.Bold; // SSMS shows the double-click action in bold.
            item.Click += click; menu.Items.Add(item);
        }
        void Separator() { if (menu.Items.Count > 0 && menu.Items[^1] is not System.Windows.Controls.Separator) menu.Items.Add(new System.Windows.Controls.Separator()); }
        // SSMS 22.11 table menu: Design, Select Top 1000 Rows, Edit Top 200 Rows; the numbers come from Tools ▸ Options ▸ Object Explorer ▸ Commands (M1044).
        void RowCommands(bool edit = true)
        {
            var commands = EnvironmentSettings.Load();
            Add($"Select Top {commands.SelectTop} Rows", SelectTopFromExplorer, "ExplorerSelectTop");
            if (edit) Add($"Edit Top {commands.EditTop} Rows", EditTopFromExplorer, "ExplorerEditTop");
        }
        void Footer(bool properties = false, RoutedEventHandler? click = null, string? name = null, IReadOnlyList<ServerReportDefinition>? reports = null)
        {
            if (node is { CanFilter: true } folder)
            {
                Separator();
                var filter = new MenuItem { Header = "_Filter", Name = "ExplorerFilterMenu" };
                var settings = new MenuItem { Header = "_Filter Settings", Name = "ExplorerFilterSettings" };
                settings.Click += (_, _) => _ = FilterSelectedFolderAsync();
                var remove = new MenuItem { Header = "_Remove Filter", Name = "ExplorerRemoveFilter", IsEnabled = folder.Filter.IsActive };
                remove.Click += (_, _) => _ = ApplyExplorerFilterAsync(folder, CatalogFilter.None);
                filter.Items.Add(settings); filter.Items.Add(remove); menu.Items.Add(filter);
            }
            Separator(); Add("Start psql", StartPsql, "ExplorerStartPsql");
            if (reports is not null)
            {
                // SSMS: Reports ▸ Standard Reports ▸ …; Custom Reports (RDL files) has no PostgreSQL counterpart.
                Separator();
                var standard = new MenuItem { Header = "_Standard Reports", Name = "ExplorerStandardReports" };
                foreach (var report in reports)
                {
                    var item = new MenuItem { Header = report.Title, Name = "ExplorerReport" + report.Kind, ToolTip = report.Description };
                    item.Click += (_, _) => OpenReportFromExplorer(report.Kind);
                    standard.Items.Add(item);
                }
                menu.Items.Add(new MenuItem { Header = "Re_ports", Name = "ExplorerReports", Items = { standard } });
            }
            Separator(); Add("_Refresh", RefreshExplorer, "ExplorerRefresh");
            if (properties) Add("_Properties", click!, name, isDefault: true);
        }

        var entry = node?.Entry;
        switch (entry?.Kind)
        {
            case null:
                Add("_Connect…", Connect, "ExplorerConnect");
                break;
            case CatalogKind.Server:
                Add("_Connect…", Connect, "ExplorerConnect");
                Add("_Disconnect", DisconnectExplorer, "ExplorerDisconnect");
                Add("Re_gister…", RegisterFromExplorer, "ExplorerRegister");
                Separator(); Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Separator(); Add("Activity _Monitor", ShowActivity, "ExplorerActivityMonitor");
                // SSMS: Start · Stop · Pause · Resume · Restart; PostgreSQL has no pause, so Reload Configuration takes the place of Pause/Resume.
                Separator();
                var (service, state) = SelectedConnection is { } current ? ResolveLocalService(current) : (null, LocalServiceState.Unknown);
                string? unavailable = service is null ? "Available for a PostgreSQL Windows service on this computer whose port matches this connection." : null;
                void ServiceItem(string header, LocalServiceAction action, string name, bool allowed)
                {
                    Add(header, async (_, _) => await ControlLocalServiceAsync(action), name, enabled: service is not null && allowed);
                    var item = (MenuItem)menu.Items[^1];
                    item.ToolTip = unavailable ?? $"Windows service {service!.Name} ({LocalServerServices.Describe(state)}); asks for administrator permission.";
                    ToolTipService.SetShowOnDisabled(item, true);
                }
                ServiceItem("_Start", LocalServiceAction.Start, "ExplorerStartService", state == LocalServiceState.Stopped);
                ServiceItem("St_op", LocalServiceAction.Stop, "ExplorerStopService", state == LocalServiceState.Running);
                Add("Reload Configuration", ReloadConfiguration, "ExplorerReloadConfiguration");
                ServiceItem("Res_tart", LocalServiceAction.Restart, "ExplorerRestartService", state == LocalServiceState.Running);
                Separator(); Add("Configure Server TLS…", ConfigureServerTls, "ExplorerServerTls");
                Footer(true, ServerProperties, "ExplorerServerProperties", ServerReportService.ServerReports);
                break;
            case CatalogKind.Databases:
                Add("New _Database…", NewDatabase, "ExplorerNewDatabase");
                Separator(); Add("Restore Database…", RestoreDatabase, "ExplorerRestoreDatabase");
                Footer();
                break;
            case CatalogKind.Database:
                Add("New _Database…", NewDatabase, "ExplorerNewDatabase");
                Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Separator();
                var tasks = new MenuItem { Header = "Tas_ks", Name = "ExplorerDatabaseTasks" };
                foreach (var (header, click, name) in new (string, RoutedEventHandler, string)[] { ("Back Up…", BackupDatabase, "ExplorerBackupDatabase"), ("Restore…", RestoreDatabase, "ExplorerRestoreDatabase"), ("Database Operations…", DatabaseOperations, "ExplorerDatabaseOperations") })
                { var item = new MenuItem { Header = header, Name = name }; item.Click += click; tasks.Items.Add(item); }
                menu.Items.Add(tasks);
                Add("Find Missing _Foreign Keys…", FindMissingForeignKeys, "ExplorerMissingForeignKeys");
                Footer(true, ObjectProperties, "ExplorerObjectProperties", ServerReportService.DatabaseReports);
                break;
            case CatalogKind.Schemas:
                Add("New _Schema…", NewSchema, "ExplorerNewSchema");
                Footer();
                break;
            case CatalogKind.Schema:
                Add("New _Schema…", NewSchema, "ExplorerNewSchema");
                Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Separator(); Add("Schema Operations…", SchemaOperations, "ExplorerSchemaOperations");
                Footer(true, ObjectProperties, "ExplorerObjectProperties");
                break;
            case CatalogKind.Tables:
                Add("New _Table…", NewTableInDesigner, "ExplorerNewTable");
                Footer();
                break;
            case CatalogKind.Views:
                Add("New _View…", NewViewFromExplorer, "ExplorerNewView");
                Footer();
                break;
            case CatalogKind.DatabaseDiagrams:
                // SSMS 22.11 (M1043): New Database Diagram, Install Diagram Support, Filter ▸, Reports ▸, Refresh. Filter and Reports are not offered here.
                Add("New Database Diagram", NewDiagramFromExplorer, "ExplorerNewDiagram");
                Add("Install Diagram Support", InstallDiagramSupport, "ExplorerInstallDiagramSupport");
                Separator(); Add("_Refresh", RefreshExplorer, "ExplorerRefresh");
                break;
            case CatalogKind.DatabaseDiagram:
                // Not captured in SSMS; inferred from its other object menus: Modify (double click) opens the diagram.
                Add("New Database Diagram", NewDiagramFromExplorer, "ExplorerNewDiagram");
                Add("_Modify", ModifyDiagramFromExplorer, "ExplorerModifyDiagram", isDefault: true);
                Separator(); Add("Rena_me", RenameDiagramFromExplorer, "ExplorerRenameDiagram"); Add("_Delete", DeleteDiagramFromExplorer, "ExplorerDeleteDiagram");
                Separator(); Add("_Refresh", RefreshExplorer, "ExplorerRefresh");
                break;
            case CatalogKind.Enums or CatalogKind.Domains:
                Add("New Enum / Domain…", NewTypeFromExplorer, "ExplorerNewType");
                Footer();
                break;
            case CatalogKind.Roles:
                Add("New Login/Group _Role…", NewRole, "ExplorerNewRole");
                Footer();
                break;
            case CatalogKind.Role when entry.RoleName?.StartsWith("pg_", StringComparison.Ordinal) == true:
                // Predefined roles, like SSMS fixed server roles, offer no editing.
                Footer();
                break;
            case CatalogKind.Role:
                Add("New Login/Group _Role…", NewRole, "ExplorerNewRole");
                Add("Role Memberships…", RoleMemberships, "ExplorerRoleMemberships");
                Add("Set Role Password…", SetRolePassword, "ExplorerSetRolePassword");
                Separator(); Add("Start psql", StartPsql, "ExplorerStartPsql");
                Separator(); Add("Rena_me", RenameRole, "ExplorerRenameRole"); Add("_Delete", DropRole, "ExplorerDropRole");
                Separator(); Add("_Refresh", RefreshExplorer, "ExplorerRefresh");
                Add("_Properties", RoleProperties, "ExplorerRoleProperties", isDefault: true);
                break;
            case CatalogKind.Relation when entry.RelationType is "r" or "p":
                Add("New _Table…", NewTableInDesigner, "ExplorerNewTable");
                Add("De_sign", DesignFromExplorer, "ExplorerDesign");
                RowCommands();
                Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Separator();
                if (entry.CanEditTable)
                {
                    Add("Table Operations…", TableOperationsFromExplorer, "ExplorerTableOperations");
                    Add("Expression / Partial Index…", ExpressionIndexFromExplorer, "ExplorerExpressionIndex");
                    Add("Range Exclusion Constraint…", RangeExclusionFromExplorer, "ExplorerRangeExclusion");
                }
                if (entry.CanManagePartitions) Add("Partitions…", PartitionsFromExplorer, "ExplorerPartitions");
                Add("Pending Index Operation…", PendingIndexFromExplorer, "ExplorerPendingIndex");
                Footer(true, TableProperties, "ExplorerTableProperties");
                break;
            case CatalogKind.Relation when entry.RelationType == "S":
                Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Separator(); Add("Sequence Operations…", SequenceOperations, "ExplorerSequenceOperations");
                Footer();
                break;
            case CatalogKind.Relation when entry.CanInspectObjectProperties: // Views and materialized views.
                if (entry.RelationType == "v") { Add("New _View…", NewViewFromExplorer, "ExplorerNewView"); Add("De_sign", DesignViewFromExplorer, "ExplorerDesignView"); }
                RowCommands(edit: entry.RelationType == "v");
                Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Footer(true, ObjectProperties, "ExplorerObjectProperties");
                break;
            case CatalogKind.Routine:
                Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Separator(); Add("Routine Permissions…", RoutinePermissionsFromExplorer, "ExplorerRoutinePermissions");
                Footer(true, ObjectProperties, "ExplorerObjectProperties");
                break;
            case CatalogKind.DataType:
                if (entry.CanManageEnum) Add("Enum Values…", EnumFromExplorer, "ExplorerEnumValues");
                if (entry.CanManageDomain) Add("Domain Constraints…", DomainFromExplorer, "ExplorerDomainConstraints");
                Footer();
                break;
            default: // Remaining folders, columns, indexes and foreign tables.
                Add("New _Query", QueryFromExplorer, "ExplorerNewQuery");
                Footer();
                break;
        }
    }
}
