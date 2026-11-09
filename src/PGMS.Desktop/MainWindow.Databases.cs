using System.IO;
using System.Windows;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>Available Databases box of the toolbar: shows the active query's database and moves the tab to another one.</summary>
public partial class MainWindow
{
    /// <summary>Replaces the warning box in tests; returns true to change the database.</summary>
    internal Func<string, bool>? DatabaseSwitchConfirm { get; set; }

    private void ShowActiveDatabase(QueryView? query)
    {
        DatabaseName.IsEnabled = query is { IsConnected: true, IsBusy: false };
        DatabaseName.ToolTip = query is null
            ? "Activate a query tab to see its database and session status. Object Explorer uses a separate connection."
            : "Database of the active query session. Choosing another one opens a new connection for this tab: SET values and temporary tables do not carry over.";
        if (DatabaseName.IsDropDownOpen) return;
        string shown = query?.Database ?? "No active query";
        if (DatabaseName.Items.Count != 1 || !Equals(DatabaseName.SelectedItem, shown)) FillDatabaseBox([shown], shown);
    }

    private void FillDatabaseBox(IEnumerable<string> names, string selected)
    {
        DatabaseName.Items.Clear();
        foreach (string name in names) DatabaseName.Items.Add(name);
        DatabaseName.SelectedItem = selected;
    }

    // The list comes from a connection of its own: the tab's session may be in an aborted transaction.
    private async void DatabaseBoxOpened(object? sender, EventArgs e)
    {
        if (ActiveQuery is not { ConnectionString: { } connection } query) return;
        try
        {
            var databases = await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Databases, "Databases"));
            if (ActiveQuery != query || !DatabaseName.IsDropDownOpen) return;
            FillDatabaseBox(databases.Select(d => d.Name).Append(query.Database).Distinct().Order(StringComparer.CurrentCultureIgnoreCase), query.Database);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException)
        {
            Status.Text = "Could not list databases: " + ex.Message;
        }
    }

    private async void DatabaseBoxClosed(object? sender, EventArgs e)
    {
        var chosen = DatabaseName.SelectedItem as string;
        var query = ActiveQuery;
        if (query is not null && chosen is not null && chosen != query.Database)
        {
            await SwitchQueryDatabaseAsync(query, chosen);
            if (ActiveQuery == query) query.Editor.Focus();
        }
        ShowActiveDatabase(ActiveQuery);
    }

    internal async Task SwitchQueryDatabaseAsync(QueryView query, string database)
    {
        if (query.ConnectionString is not { } connection || query.IsBusy) return;
        Status.Text = $"Changing to {database}…";
        try
        {
            if (LossWarning(await query.InspectSessionLossRiskAsync(), $"PostgreSQL changes database by opening a new connection, so moving to \"{database}\" closes this session.", "Change database?") is { } warning && !ConfirmDatabaseSwitch(warning))
            {
                Status.Text = "Database not changed.";
                return;
            }
            await query.ChangeDatabaseAsync(new Npgsql.NpgsqlConnectionStringBuilder(connection) { Database = database }.ConnectionString);
            Status.Text = query.Database == database ? $"Connected to {database}." : "Database not changed.";
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or InvalidOperationException or OperationCanceledException) { ShowError(ex); }
    }

    private static string? LossWarning(QuerySessionLossRisk risk, string reason, string question)
    {
        string? held = (risk.OpenTransaction, risk.TemporaryTables) switch
        {
            (true, true) => "an open transaction and temporary tables",
            (true, false) => "an open transaction",
            (false, true) => "temporary tables",
            _ => null,
        };
        if (held is null) return null;
        string loss = risk.OpenTransaction ? "Uncommitted work is rolled back, and temporary tables and SET values are lost." : "Temporary tables and SET values are lost.";
        return $"This query's session has {held}.\n\n{reason} {loss}\n\n{question}";
    }

    private bool ConfirmDatabaseSwitch(string warning) => DatabaseSwitchConfirm is { } confirm
        ? confirm(warning)
        : MessageBox.Show(this, warning, "PGMS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
