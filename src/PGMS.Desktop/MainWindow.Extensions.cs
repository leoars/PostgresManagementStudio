using System.IO;
using System.Windows;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Extensions folder of a database (PGMS-060, M1090): New Extension…, Update Extension… and Delete.</summary>
public partial class MainWindow
{
    /// <summary>Replaces ShowDialog of the extension window in tests; true means the dialog closed with OK or Script.</summary>
    internal Func<ExtensionDialog, Task<bool>>? ExtensionPrompt { get; set; }

    private async void NewExtensionFromExplorer(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is CatalogNode node) await ExtensionDialogAsync(node, update: false);
    }

    private async void UpdateExtensionFromExplorer(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is CatalogNode { Entry.IsExtension: true } node) await ExtensionDialogAsync(node, update: true);
    }

    private async void DeleteExtensionFromExplorer(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is CatalogNode { Entry.IsExtension: true } node) await DeleteExtensionAsync(node);
    }

    /// <summary>The Extensions folder holding the node (the node itself when it is the folder).</summary>
    private static CatalogNode? ExtensionsFolder(CatalogNode node) => node.Entry.Kind == CatalogKind.Extensions ? node : node.Parent is { Entry.Kind: CatalogKind.Extensions } parent ? parent : null;

    /// <summary>New (from the folder or an extension) or Update (an installed extension); OK runs and refreshes the folder, Script opens the statement connected to the database.</summary>
    internal async Task<ExtensionDialog?> ExtensionDialogAsync(CatalogNode node, bool update)
    {
        if (catalogService?.ConnectionFor(node.Entry.Database) is not { } connection) { Status.Text = "Connect before managing extensions."; return null; }
        var dialog = new ExtensionDialog(connection, update ? node.Entry : null) { Owner = this };
        bool accepted = ExtensionPrompt is { } prompt ? await prompt(dialog) : dialog.ShowDialog() == true;
        if (!accepted) return dialog;
        if (dialog.ScriptSql is { } sql) { await OpenAdministrativeScriptAsync(connection, sql); Status.Text = "Review the statement and execute it (F5). Nothing has run yet."; }
        if (dialog.WasApplied)
        {
            if (ExtensionsFolder(node) is { } folder) { await folder.LoadAsync(refresh: true); folder.IsExpanded = true; }
            Status.Text = update ? "Extension updated." : "Extension created.";
        }
        return dialog;
    }

    /// <summary>Delete: shows the exact DROP EXTENSION (never CASCADE) and runs it after Yes; the folder is refreshed.</summary>
    internal async Task<bool> DeleteExtensionAsync(CatalogNode node)
    {
        if (catalogService?.ConnectionFor(node.Entry.Database) is not { } connection) return false;
        try
        {
            var installed = await ExtensionEdits.InspectAsync(connection, node.Entry.Oid, node.Entry.Name);
            string question = $"Delete extension {installed.Name}? This runs:\n\n{ExtensionEdits.DropSql(installed.Name)}\n\nObjects of other extensions or of your own that depend on it make the server refuse. It cannot be undone.";
            bool yes = DeleteObjectConfirm?.Invoke(question) ?? LocalizedMessageBox.Show(this, question, "Delete Object", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!yes) { Status.Text = "Nothing was deleted."; return false; }
            await ExtensionEdits.DropAsync(connection, node.Entry.Oid, node.Entry.Name);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or TimeoutException)
        {
            ShowError(new InvalidOperationException("Not deleted: " + (ex is Npgsql.PostgresException pg ? pg.MessageText : ex.Message), ex)); return false;
        }
        var folder = node.Parent;
        if (folder is not null) { await folder.LoadAsync(refresh: true); folder.IsSelected = true; }
        Status.Text = $"Extension {node.Entry.Name} deleted.";
        return true;
    }
}
