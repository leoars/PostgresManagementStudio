using System.IO;
using System.Windows;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>New Function, New Stored Procedure and New Trigger open a template in a query, as SSMS does; Enable and Disable of triggers (PGMS-061, M1088).</summary>
public partial class MainWindow
{
    private async void NewFunctionFromExplorer(object sender, RoutedEventArgs e) => await NewObjectFromTemplateAsync("Function", "Create PL/pgSQL Function");
    private async void NewProcedureFromExplorer(object sender, RoutedEventArgs e) => await NewObjectFromTemplateAsync("Procedure", "Create Procedure");
    private async void NewTriggerFromExplorer(object sender, RoutedEventArgs e) => await NewObjectFromTemplateAsync("Trigger", "Create Trigger");

    /// <summary>Opens the built-in template in a query connected to the selected database, with the schema (and table) of the node as parameter values;
    /// nothing runs. Specify Values for Template Parameters (Ctrl+Shift+M) fills the rest.</summary>
    internal async Task<QueryView?> NewObjectFromTemplateAsync(string folder, string name)
    {
        if (SelectedConnection is not { } connection) { Status.Text = "Connect before creating objects."; return null; }
        var template = SqlTemplateCatalog.BuiltInTemplates.Single(t => t.Folder == folder && t.Name == name);
        var defaults = new Dictionary<string, string>();
        var selected = (ExplorerTree.SelectedItem as CatalogNode)?.Entry;
        if ((SelectedAncestor(e => e.Kind == CatalogKind.Schema)?.Name ?? selected?.Schema) is { Length: > 0 } schema) defaults["schema_name"] = schema;
        if (SelectedAncestor(e => e.Kind == CatalogKind.Relation) is { } table)
        {
            defaults["table_name"] = table.Name;
            defaults["function_name"] = table.Name + "_trigger";
            defaults["trigger_name"] = table.Name + "_trigger";
        }
        var query = NewQuery(SqlTemplateParameters.WithDefaults(template.Text, defaults));
        Status.Text = $"{name} template opened; Ctrl+Shift+M fills its parameters. Nothing was executed.";
        try { await query.ConnectAsync(connection); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        return query;
    }

    private async void EnableTriggerFromExplorer(object sender, RoutedEventArgs e) { if (ExplorerTree.SelectedItem is CatalogNode node) await SetTriggerEnabledAsync(node, true); }
    private async void DisableTriggerFromExplorer(object sender, RoutedEventArgs e) { if (ExplorerTree.SelectedItem is CatalogNode node) await SetTriggerEnabledAsync(node, false); }

    /// <summary>Trigger ▸ Enable / Disable: ALTER TABLE … ENABLE/DISABLE TRIGGER, with the trigger found by OID and name.</summary>
    internal async Task<bool> SetTriggerEnabledAsync(CatalogNode node, bool enable)
    {
        if (node.Entry is not { Kind: CatalogKind.Trigger, Oid: not 0 } entry || catalogService?.ConnectionFor(entry.Database) is not { } connection) return false;
        try { await TableChildObjects.SetTriggerEnabledAsync(connection, entry.Oid, entry.Name, enable); }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or TimeoutException)
        { ShowError(new InvalidOperationException($"Trigger not {(enable ? "enabled" : "disabled")}: " + ex.Message, ex)); return false; }
        Status.Text = $"Trigger {entry.Name} {(enable ? "enabled" : "disabled")}.";
        return true;
    }
}
