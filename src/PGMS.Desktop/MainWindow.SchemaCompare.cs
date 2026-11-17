using System.IO;
using System.Windows;
using AvalonDock.Layout;

namespace PGMS.Desktop;

/// <summary>Tools ▸ Schema Compare (PGMS-075, M1102): each use opens a new Schema Compare document, as in SSMS 22.11.</summary>
public partial class MainWindow
{
    private void ShowSchemaCompare(object sender, RoutedEventArgs e) => ShowSchemaCompare();

    internal SchemaCompareView ShowSchemaCompare()
    {
        var view = new SchemaCompareView { PickConnection = PickConnection, OpenScript = OpenSchemaCompareScriptAsync };
        var document = new LayoutDocument { Title = view.Title, ContentId = "schemacompare-" + Guid.NewGuid().ToString("N"), Content = view };
        document.Closed += (_, _) => RefreshCommands();
        AddDocument(document);
        document.IsActive = true;
        RefreshCommands();
        return view;
    }

    /// <summary>Generate Script: a new query tab with the script, connected to the target like the SSMS deployment script. Nothing is executed.</summary>
    private async Task OpenSchemaCompareScriptAsync(string script, string target)
    {
        var query = NewQuery(script);
        try { await query.ConnectAsync(target); Status.Text = "Schema Compare script opened on the target. Nothing was executed."; }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
        RefreshCommands();
    }
}
