using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

// Filter Settings follows the folder map measured in SSMS 22.11 (docs/MARCO-M1040.md).
internal static class FilterSettingsChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var service = new CatalogService(connection);
        bool Can(CatalogKind kind) => new CatalogNode(new(kind, "x", "postgres", "public"), service).CanFilter;
        check(new[] { CatalogKind.Tables, CatalogKind.Views, CatalogKind.MaterializedViews, CatalogKind.Functions, CatalogKind.Procedures, CatalogKind.Indexes, CatalogKind.Roles }.All(Can), "Folders SSMS filters enable the Filter button");
        check(!new[] { CatalogKind.Server, CatalogKind.Databases, CatalogKind.Database, CatalogKind.Schemas, CatalogKind.Schema, CatalogKind.Sequences, CatalogKind.Columns, CatalogKind.Enums, CatalogKind.Domains, CatalogKind.Relation, CatalogKind.Index, CatalogKind.Role }.Any(Can), "Other nodes keep the Filter button disabled");

        var excluding = new CatalogFilter("ord", CatalogFilterOperator.DoesNotContain);
        check(!excluding.Matches("orders", null) && excluding.Matches("items", null), "Does not contain excludes matching names");
        check(new CatalogFilter("ORDERS", CatalogFilterOperator.Equals).Matches("orders", null) && !new CatalogFilter("order", CatalogFilterOperator.Equals).Matches("orders", null), "Equals compares the whole name and ignores case");
        var owned = new CatalogFilter(Owner: "ana");
        check(owned.Matches("t", "ana") && !owned.Matches("t", "bob") && !owned.Matches("t", "anabel"), "Owner criterion defaults to Equals");

        string schema = "flt_" + Guid.NewGuid().ToString("N")[..8], role = schema + "_owner";
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE ROLE {role}; CREATE SCHEMA {schema}; CREATE TABLE {schema}.mine(id int); CREATE TABLE {schema}.other(id int); ALTER TABLE {schema}.mine OWNER TO {role}; CREATE FUNCTION {schema}.calc(a int) RETURNS int LANGUAGE sql AS 'SELECT 1'; CREATE FUNCTION {schema}.calculate() RETURNS int LANGUAGE sql AS 'SELECT 1';");
        try
        {
            var tables = new CatalogNode(new(CatalogKind.Tables, "Tables", "postgres", schema), service);
            await tables.LoadAsync();
            tables.SetFilter(new CatalogFilter(Owner: role));
            check(tables.Children.Single().Name == "mine" && tables.Name == "Tables (filtered)", "Owner criterion reads the real owner from the catalog");
            var functions = new CatalogNode(new(CatalogKind.Functions, "Functions", "postgres", schema), service);
            await functions.LoadAsync();
            functions.SetFilter(new CatalogFilter("calc", CatalogFilterOperator.Equals));
            check(functions.Children.Single().Entry.Name.StartsWith("calc(a"), "Equals on a routine ignores its argument list");
            var roles = new CatalogNode(new(CatalogKind.Roles, "Login/Group Roles"), service);
            await roles.LoadAsync();
            roles.SetFilter(new CatalogFilter("pgms_test", CatalogFilterOperator.Equals));
            check(roles.Children.Single().Entry.RoleName == "pgms_test", "Equals on a role ignores the (login) suffix");
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE; DROP ROLE IF EXISTS {role};"); }

        var dialog = new FilterSettingsDialog("localhost:5432", "sales", new CatalogFilter("old", CatalogFilterOperator.Equals, "ana"), canFilterByOwner: true) { ShowInTaskbar = false, Opacity = 0 };
        dialog.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(dialog.Title == "Filter Settings" && ((TextBlock)dialog.FindName("ServerText")).Text == "localhost:5432" && ((TextBlock)dialog.FindName("DatabaseText")).Text == "sales", "Window names the server and database");
            check(dialog.Rows.Select(r => r.Property).SequenceEqual(["Name", "Owner"]) && dialog.Rows[0].Value == "old" && dialog.Rows[0].Operator == CatalogFilterOperator.Equals, "Window reopens with the current criteria");
            check(((TextBlock)dialog.FindName("Description")).Text.StartsWith("Include or exclude objects based on the name"), "Description explains the selected criterion");
            ExplorerExperienceChecks.Capture(dialog, "m1040-filter-settings");
            ((Button)dialog.FindName("ClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(dialog.Rows.All(r => r.Value == "") && dialog.IsVisible, "Clear Filter empties the values and keeps the window open");
        }
        finally { dialog.Close(); }
        var indexes = new FilterSettingsDialog("localhost:5432", null, CatalogFilter.None, canFilterByOwner: false) { ShowInTaskbar = false, Opacity = 0 };
        indexes.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(indexes.Rows.Single().Property == "Name" && ((TextBlock)indexes.FindName("DatabaseLabel")).Visibility != Visibility.Visible, "Folders without owner or database show only what applies");
        }
        finally { indexes.Close(); }
    }
}
