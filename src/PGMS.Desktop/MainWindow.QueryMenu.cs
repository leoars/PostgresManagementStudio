using System.IO;
using System.Windows;
using System.Windows.Input;

namespace PGMS.Desktop;

/// <summary>Query menu items of PGMS-034: Connection, Open Server in Object Explorer, Parse, Client Statistics and the SSMS shortcuts.</summary>
public partial class MainWindow
{
    internal enum QueryMenuShortcut { None, Execute, Cancel, Parse, OpenServer, ClientStatistics }

    /// <summary>With Alt held WPF reports the key in SystemKey.</summary>
    internal static QueryMenuShortcut ShortcutFor(Key key, Key systemKey, ModifierKeys modifiers) => (key == Key.System ? systemKey : key, modifiers) switch
    {
        (Key.X, ModifierKeys.Alt) => QueryMenuShortcut.Execute,
        (Key.Pause or Key.Cancel, ModifierKeys.Alt) => QueryMenuShortcut.Cancel,
        (Key.F5, ModifierKeys.Control) => QueryMenuShortcut.Parse,
        (Key.F8, ModifierKeys.Alt) => QueryMenuShortcut.OpenServer,
        (Key.S, ModifierKeys.Alt | ModifierKeys.Shift) => QueryMenuShortcut.ClientStatistics,
        _ => QueryMenuShortcut.None
    };

    private bool HandleQueryMenuShortcut(KeyEventArgs e)
    {
        if (ActiveQuery is not { } query) return false;
        var shortcut = ShortcutFor(e.Key, e.SystemKey, Keyboard.Modifiers);
        // In Object Explorer, F5-like keys keep their meaning there.
        if (shortcut is QueryMenuShortcut.None || shortcut is QueryMenuShortcut.Execute or QueryMenuShortcut.Parse && ExplorerContent.IsKeyboardFocusWithin) return false;
        e.Handled = true;
        switch (shortcut)
        {
            case QueryMenuShortcut.Execute: _ = query.ExecuteAsync(); break;
            case QueryMenuShortcut.Cancel: query.Cancel(); break;
            case QueryMenuShortcut.Parse: _ = query.ParseAsync(); break;
            case QueryMenuShortcut.OpenServer: _ = OpenServerInExplorerAsync(); break;
            case QueryMenuShortcut.ClientStatistics: ToggleClientStatistics(); break;
        }
        return true;
    }

    private void ConnectionMenuOpened(object sender, RoutedEventArgs e)
    {
        var query = ActiveQuery;
        QueryConnectMenu.IsEnabled = query is null || query is { HasSession: false, IsBusy: false };
        QueryDisconnectMenu.IsEnabled = QueryChangeConnectionMenu.IsEnabled = query is { HasSession: true, IsBusy: false };
    }

    /// <summary>Replaces the Connect dialog in tests; returns the connection string or null.</summary>
    internal Func<string?>? ConnectionPicker { get; set; }

    private string? PickConnection()
    {
        if (ConnectionPicker is { } pick) return pick();
        var dialog = new ConnectionDialog { Owner = this };
        bool accepted = dialog.ShowDialog() == true;
        registeredServers?.Reload(); // The dialog may have saved or deleted profiles.
        return accepted ? dialog.ConnectionString : null;
    }

    private async void QueryConnect(object sender, RoutedEventArgs e) => await ConnectActiveQueryAsync();
    private async void QueryDisconnect(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) await DisconnectQueryAsync(query); }
    private async void QueryChangeConnection(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) await ChangeQueryConnectionAsync(query); }

    /// <summary>Connection ▸ Connect: only this tab; Object Explorer keeps its own connection.</summary>
    internal async Task ConnectActiveQueryAsync()
    {
        if (ActiveQuery is { HasSession: true } || ActiveQuery is { IsBusy: true } || PickConnection() is not { } connection) return;
        var query = ActiveQuery ?? NewQuery();
        try { await query.ConnectAsync(connection); Status.Text = "Connected"; }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        RefreshCommands();
    }

    /// <summary>Connection ▸ Disconnect: asks about an open transaction first, the same question as closing the tab.</summary>
    internal async Task DisconnectQueryAsync(QueryView query)
    {
        if (query.IsBusy || !query.HasSession || !await ConfirmOpenTransactionAsync(query)) return;
        await query.DisconnectAsync();
        Status.Text = "Disconnected";
        RefreshCommands();
    }

    /// <summary>Connection ▸ Change Connection: the new connection opens first, so a failure leaves the session as it was.</summary>
    internal async Task ChangeQueryConnectionAsync(QueryView query)
    {
        if (query.IsBusy || !query.HasSession) return;
        try
        {
            if (LossWarning(await query.InspectSessionLossRiskAsync(), "Changing the connection closes this session.", "Change connection?") is { } warning && !ConfirmDatabaseSwitch(warning)) { Status.Text = "Connection not changed."; return; }
            if (PickConnection() is not { } connection) { Status.Text = "Connection not changed."; return; }
            await query.ChangeDatabaseAsync(connection);
            Status.Text = "Connected";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        RefreshCommands();
    }

    private async void OpenServerInExplorer(object sender, RoutedEventArgs e) => await OpenServerInExplorerAsync();

    /// <summary>Query ▸ Open Server in Object Explorer. PGMS's Object Explorer holds one server: already on this one it is only shown, otherwise it moves to the tab's server.</summary>
    internal async Task OpenServerInExplorerAsync()
    {
        if (ActiveQuery?.ConnectionString is not { } connection) { Status.Text = "Connect this query before opening its server in Object Explorer."; return; }
        ShowExplorer(this, new RoutedEventArgs());
        if (SameServer(explorerConnection, connection)) return;
        try { await ConnectExplorerAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
    }

    private static bool SameServer(string? first, string second)
    {
        if (first is null) return false;
        var a = new Npgsql.NpgsqlConnectionStringBuilder(first);
        var b = new Npgsql.NpgsqlConnectionStringBuilder(second);
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port && a.Username == b.Username;
    }

    private async void FindMissingForeignKeys(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before scanning for foreign keys."; return; }
        var window = new ForeignKeyScanWindow(connection)
        {
            Owner = this,
            OpenInQuery = async script =>
            {
                try { await OpenQueryTextAsync(connection, script); }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException) { ShowError(ex); }
            }
        };
        window.ShowDialog();
        if (window.Created) await RefreshSelectedExplorerAsync();
    }

    private async void FindUnindexedForeignKeys(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before scanning for foreign keys."; return; }
        if (await ShowUnindexedForeignKeysAsync(connection)) await RefreshSelectedExplorerAsync();
    }

    internal Action<ForeignKeyIndexScanWindow>? UnindexedForeignKeysShown { get; set; }

    /// <summary>Find Unindexed Foreign Keys… on a database, from Object Explorer or from the warning of a diagram (PGMS-044). True when indexes were created;
    /// the diagrams open on that database then read their indexes again.</summary>
    internal async Task<bool> ShowUnindexedForeignKeysAsync(string connection, (string Schema, string Table)? focus = null)
    {
        var window = new ForeignKeyIndexScanWindow(connection, focus)
        {
            Owner = this,
            OpenInQuery = async script =>
            {
                try { await OpenQueryTextAsync(connection, script); }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException) { ShowError(ex); }
            }
        };
        UnindexedForeignKeysShown?.Invoke(window);
        window.ShowDialog();
        if (!window.Created) return false;
        foreach (var diagram in diagrams.Keys.Where(d => d.OnDatabase(connection)).ToList()) await diagram.RefreshTablesAsync();
        return true;
    }

    private readonly List<ExecutionPlanDocument> planDocuments = [];
    internal IReadOnlyList<ExecutionPlanDocument> PlanDocuments => planDocuments;
    private int planDocumentSequence;

    /// <summary>A plan in its own document tab, independent of any query session.</summary>
    internal ExecutionPlanDocument OpenPlanDocument(IReadOnlyList<PGMS.PostgreSql.ExecutionPlanStatement> statements, string title)
    {
        var view = new ExecutionPlanDocument(statements) { Report = text => Status.Text = text };
        var document = new AvalonDock.Layout.LayoutDocument { Title = planDocumentSequence++ == 0 ? title : $"{title} {planDocumentSequence}", ContentId = Guid.NewGuid().ToString(), Content = view };
        planDocuments.Add(view);
        document.Closed += (_, _) => { planDocuments.Remove(view); RefreshCommands(); };
        AddDocument(document);
        document.IsActive = true;
        return view;
    }

    private async void OpenPlanFile(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Open Execution Plan", Filter = "PostgreSQL plan JSON (*.json)|*.json|All files (*.*)|*.*", DefaultExt = ".json" };
        if (picker.ShowDialog(this) == true) await OpenPlanFileAsync(picker.FileName);
    }

    /// <summary>File ▸ Open Execution Plan: a plan saved by PGMS or written by EXPLAIN (FORMAT JSON).</summary>
    internal async Task<ExecutionPlanDocument?> OpenPlanFileAsync(string path)
    {
        try
        {
            var plan = await PGMS.PostgreSql.QueryPlanFiles.LoadAsync(path);
            var statements = await Task.Run(() => PGMS.PostgreSql.ExecutionPlanBuilder.Build([plan]));
            var view = OpenPlanDocument(statements, Path.GetFileName(path));
            Status.Text = "Saved plan opened. It was not run against any server.";
            return view;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { ShowError(ex); return null; }
    }

    private void ToggleIntelliSense(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) query.IntelliSenseEnabled = !query.IntelliSenseEnabled; RefreshResultsButtons(); }
    private async void ListMembers(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) { query.Editor.Focus(); await query.ShowCompletionAsync(); } }
    private void RefreshLocalCache(object sender, RoutedEventArgs e) => ActiveQuery?.RefreshLocalCache();

    private async void ParseQuery(object sender, RoutedEventArgs e) { if (ActiveQuery is { } query) await query.ParseAsync(); }
    private void ToggleClientStatistics(object sender, RoutedEventArgs e) => ToggleClientStatistics();
    internal void ToggleClientStatistics()
    {
        if (ActiveQuery is { } query) query.IncludeClientStatistics = !query.IncludeClientStatistics;
        RefreshResultsButtons();
    }
    private void ResetClientStatistics(object sender, RoutedEventArgs e) => ActiveQuery?.ResetClientStatistics();
}
