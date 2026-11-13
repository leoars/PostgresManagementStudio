using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

// Script … as (M1054, PGMS-026). Menus observed in SSMS 22.11 (docs/MARCO-M1054.md): every verb is always listed and the unavailable ones are gray.
// A script only ever opens, saves or copies; running it is up to the user.
public partial class MainWindow
{
    private static readonly (ScriptVerb? Verb, string Header, string Name)?[] ScriptVerbItems =
    [
        (ScriptVerb.Create, "CREATE To", "Create"), (null, "ALTER To", "Alter"), (ScriptVerb.CreateOrReplace, "CREATE OR REPLACE To", "CreateOrReplace"), (ScriptVerb.Drop, "DROP To", "Drop"), (ScriptVerb.DropAndCreate, "DROP And CREATE To", "DropAndCreate"),
        null,
        (ScriptVerb.Select, "SELECT To", "Select"), (ScriptVerb.Insert, "INSERT To", "Insert"), (ScriptVerb.Update, "UPDATE To", "Update"), (ScriptVerb.Delete, "DELETE To", "Delete"),
        null,
        (ScriptVerb.Execute, "EXECUTE To", "Execute"),
    ];

    internal MenuItem? BuildScriptMenu(CatalogEntry entry)
    {
        if (ObjectScripts.Noun(entry) is not { } noun) return null;
        var available = ObjectScripts.Verbs(entry);
        var root = new MenuItem { Header = $"Script {noun} as", Name = "ExplorerScriptAs" };
        foreach (var definition in ScriptVerbItems)
        {
            if (definition is not { } item) { root.Items.Add(new Separator()); continue; }
            var verbItem = new MenuItem { Header = item.Header, Name = "ExplorerScript" + item.Name, IsEnabled = item.Verb is { } v && available.Contains(v) };
            if (item.Verb is null) { verbItem.ToolTip = "PostgreSQL changes a view or routine definition with CREATE OR REPLACE."; ToolTipService.SetShowOnDisabled(verbItem, true); }
            if (verbItem.IsEnabled && item.Verb is { } verb)
                foreach (var (destination, header, suffix) in new[] { (ScriptDestination.NewQuery, "New Query Editor Window", "NewQuery"), (ScriptDestination.File, "File ...", "File"), (ScriptDestination.Clipboard, "Clipboard", "Clipboard") })
                {
                    var target = new MenuItem { Header = header, Name = verbItem.Name + suffix };
                    target.Click += async (_, _) => await ScriptFromExplorerAsync(entry, verb, destination);
                    verbItem.Items.Add(target);
                }
            root.Items.Add(verbItem);
        }
        return root;
    }

    private async void RebuildIndexFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Entry is not { Kind: CatalogKind.Index } entry) return;
        try
        {
            var snapshot = await new IndexMaintenanceCatalog(connection).InspectAsync(entry.Oid);
            var dialog = new ConcurrentIndexDialog(connection, maintenance: snapshot) { Owner = this };
            dialog.ShowDialog();
            if (dialog.WasApplied) Status.Text = "Index operation finished.";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or IOException or OperationCanceledException or TimeoutException) { Status.Text = "Rebuild failed: " + ex.Message; }
    }

    /// <summary>SSMS Rebuild All. Opens the statement instead of running it: on a large table it blocks writes, and the concurrent form is a choice for the user.</summary>
    internal static string RebuildAllSql(string schema, string table)
    {
        string name = SqlIdentifier.Quote(schema) + "." + SqlIdentifier.Quote(table);
        return $"-- REINDEX blocks writes to the table while it runs.\r\n-- REINDEX TABLE CONCURRENTLY {name}; rebuilds without blocking writes, but runs outside a transaction.\r\nREINDEX TABLE {name};\r\n";
    }

    private async void RebuildAllFromExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedConnection is not { } connection || (ExplorerTree.SelectedItem as CatalogNode)?.Parent?.Entry is not { Kind: CatalogKind.Relation, Schema: { } schema } table) return;
        await OpenAdministrativeScriptAsync(connection, RebuildAllSql(schema, table.Name));
    }

    // Replaceable in tests, which must not raise a message box.
    internal Func<string, bool>? ConfirmDatabaseOffline { get; set; }

    internal async Task SetDatabaseOnlineAsync(CatalogEntry entry, bool online)
    {
        if (catalogService is not { } catalog || entry.Database is not { } database) return;
        if (!online)
        {
            string message = $"Take the database \"{database}\" offline?\n\nNew connections are refused, for every user. Sessions already connected keep running: end them in Activity Monitor if they must stop. Nothing is deleted, and Bring Online reverses this.";
            if (!(ConfirmDatabaseOffline?.Invoke(message) ?? MessageBox.Show(this, message, "Take Offline", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)) return;
        }
        try
        {
            await new DatabaseEditService(catalog.ConnectionFor(null)).SetOnlineAsync(database, online);
            var folder = (ExplorerTree.SelectedItem as CatalogNode)?.Parent;
            if (folder is not null) await folder.LoadAsync(catalogCancellation.Token, refresh: true);
            Status.Text = online ? $"Database {database} is online." : $"Database {database} is offline.";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or ArgumentException or IOException or OperationCanceledException or TimeoutException) { Status.Text = (online ? "Bring Online failed: " : "Take Offline failed: ") + (ex is Npgsql.PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message); }
    }

    // Replaceable in tests, which must not open a file dialog or touch the real clipboard.
    internal Func<string, string?> ScriptFilePicker { get; set; } = suggested =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", DefaultExt = ".sql", FileName = suggested };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };
    internal Action<string> ScriptClipboard { get; set; } = text => Clipboard.SetText(text, TextDataFormat.UnicodeText);

    /// <summary>Returns the query tab for <see cref="ScriptDestination.NewQuery"/>. A database script opens on the maintenance database, because a database cannot be dropped from a session inside it.</summary>
    internal async Task<QueryView?> ScriptFromExplorerAsync(CatalogEntry entry, ScriptVerb verb, ScriptDestination destination)
    {
        if (catalogService is not { } catalog) { Status.Text = "Connect before scripting an object."; return null; }
        string connection = catalog.ConnectionFor(entry.Database);
        try
        {
            string sql = await new ObjectScriptService(connection).ScriptAsync(entry, verb);
            switch (destination)
            {
                case ScriptDestination.File:
                    if (ScriptFilePicker(ObjectScripts.FileName(entry)) is not { } path) return null;
                    await File.WriteAllTextAsync(path, sql, new UTF8Encoding(false));
                    Status.Text = "Script saved to " + path;
                    return null;
                case ScriptDestination.Clipboard:
                    ScriptClipboard(sql);
                    Status.Text = "Script copied to the clipboard.";
                    return null;
                default:
                    var query = NewQuery(sql);
                    string target = entry.Kind == CatalogKind.Database ? new Npgsql.NpgsqlConnectionStringBuilder(connection) { Database = "postgres" }.ConnectionString : connection;
                    await query.ConnectAsync(target);
                    return query;
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or ObjectScriptException or IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Runtime.InteropServices.ExternalException)
        { Status.Text = "Script failed: " + (ex is Npgsql.PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : ex.Message); return null; }
    }
}
