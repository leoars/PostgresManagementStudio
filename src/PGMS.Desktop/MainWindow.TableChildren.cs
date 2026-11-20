using System.IO;
using System.Windows;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Keys, Constraints and Triggers folders (PGMS-053, M1086): New Foreign Key, New Constraint, Modify, Rename (in the tree) and Delete, as in SSMS.</summary>
public partial class MainWindow
{
    /// <summary>Confirms Delete with the statement it runs; replaced by tests.</summary>
    internal Func<string, bool>? DeleteObjectConfirm { get; set; }

    private static bool IsTableChild(CatalogEntry entry) => entry.Kind is CatalogKind.Key or CatalogKind.Constraint or CatalogKind.Trigger && entry.Oid != 0;

    /// <summary>Modify: keys and constraints open the Table Designer of their table (as SSMS opens its designer); a trigger opens its DROP And CREATE script, not executed.</summary>
    private async void ModifyTableChildFromExplorer(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not CatalogNode { Entry: var entry } || !IsTableChild(entry)) return;
        await ModifyTableChildAsync(entry);
    }

    internal async Task ModifyTableChildAsync(CatalogEntry entry)
    {
        if (entry.Kind == CatalogKind.Trigger) { await ScriptFromExplorerAsync(entry, ScriptVerb.DropAndCreate, ScriptDestination.NewQuery); return; }
        if (SelectedConnection is not { } connection || SelectedAncestor(e => e.Kind == CatalogKind.Relation) is not { Schema: { } schema } table) { Status.Text = "Connect before modifying a constraint."; return; }
        if (!table.CanEditTable) { Status.Text = "Table Designer edits ordinary tables; use Script as or the Tools ▸ constraint windows for this one."; return; }
        OpenTableDesigner(connection, schema, table.Name);
    }

    private void RenameTableChildFromExplorer(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is CatalogNode node) BeginExplorerRename(node);
    }

    /// <summary>Rename typed in the tree for a key, constraint or trigger: checked by OID and old name, then renamed and the folder refreshed.</summary>
    internal async Task<bool> RenameTableChildAsync(CatalogNode node, string newName)
    {
        if (catalogService?.ConnectionFor(node.Entry.Database) is not { } connection) return false;
        // Object Explorer ▸ Commands ▸ Prompt to confirm the renaming of schema objects (M1143), as in SSMS.
        if (explorerSettings.ConfirmRename && !(RenameConfirmPrompt?.Invoke(node.Entry.Name, newName) ?? LocalizedMessageBox.Show(this, $"Rename {node.Entry.Name} to {newName}?",
                "Rename", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes))
        { Status.Text = "Not renamed."; return false; }
        try
        {
            await TableChildObjects.RenameAsync(connection, node.Entry.Kind == CatalogKind.Trigger, node.Entry.Oid, node.Entry.Name, newName);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or ArgumentException or TimeoutException) { Status.Text = "Not renamed: " + ex.Message; return false; }
        if (node.Parent is { } folder) await folder.LoadAsync(refresh: true);
        Status.Text = $"Renamed to {newName}.";
        return true;
    }

    private async void DeleteTableChildFromExplorer(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is CatalogNode node && IsTableChild(node.Entry)) await DeleteTableChildAsync(node);
    }

    /// <summary>Delete: shows the exact statement (never CASCADE) and runs it after Yes; the folder is refreshed.</summary>
    internal async Task<bool> DeleteTableChildAsync(CatalogNode node)
    {
        if (catalogService?.ConnectionFor(node.Entry.Database) is not { } connection) return false;
        bool trigger = node.Entry.Kind == CatalogKind.Trigger;
        try
        {
            string sql = await TableChildObjects.DescribeDropAsync(connection, trigger, node.Entry.Oid, node.Entry.Name);
            string question = $"Delete {(trigger ? "trigger" : "constraint")} {node.Entry.Name}? This runs:\n\n{sql}\n\nIt cannot be undone.";
            bool yes = DeleteObjectConfirm?.Invoke(question) ?? LocalizedMessageBox.Show(this, question, "Delete Object", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!yes) { Status.Text = "Nothing was deleted."; return false; }
            await TableChildObjects.DropAsync(connection, trigger, node.Entry.Oid, node.Entry.Name);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or TimeoutException)
        {
            ShowError(new InvalidOperationException("Not deleted: " + ex.Message, ex)); return false;
        }
        var folder = node.Parent;
        if (folder is not null) { await folder.LoadAsync(refresh: true); folder.IsSelected = true; }
        Status.Text = $"{(trigger ? "Trigger" : "Constraint")} {node.Entry.Name} deleted.";
        return true;
    }
}
