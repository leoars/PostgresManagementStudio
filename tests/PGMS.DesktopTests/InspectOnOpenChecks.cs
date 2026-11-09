using System.Windows.Controls;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

// Windows opened from Object Explorer read the selected object right away, as SSMS shows properties already loaded;
// windows built without InspectOnOpen keep waiting for Inspect.
internal static class InspectOnOpenChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string schema = "open_" + Guid.NewGuid().ToString("N")[..8];
        await using var session = new QuerySession(connection); await session.OpenAsync();
        await session.ExecuteAsync($"CREATE SCHEMA {schema}; CREATE SEQUENCE {schema}.counter START 7;");
        try
        {
            async Task<bool> Eventually(Func<bool> condition) { for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(50); return condition(); }

            var sequence = new SequenceDialog(connection, schema, "counter") { ShowInTaskbar = false, Opacity = 0, InspectOnOpen = true };
            sequence.Show();
            try { check(await Eventually(() => sequence.Snapshot is not null), "Sequence opened from Object Explorer reads the sequence without Inspect"); }
            finally { sequence.Close(); }
            var manual = new SequenceDialog(connection, schema, "counter") { ShowInTaskbar = false, Opacity = 0 };
            manual.Show();
            try { await Task.Delay(500); check(manual.Snapshot is null, "Without InspectOnOpen the window still waits for Inspect"); }
            finally { manual.Close(); }

            var entry = (await new CatalogService(connection).ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"))).Single(e => e.Name == schema);
            var properties = new ObjectPropertiesDialog(connection, entry) { ShowInTaskbar = false, Opacity = 0, InspectOnOpen = true };
            properties.Show();
            try { check(await Eventually(() => properties.Snapshot?.Target.Oid == entry.Oid), "Object Properties opened from Object Explorer reads the object without Inspect"); }
            finally { properties.Close(); }

            var memberships = new RoleMembershipDialog(connection, "pg_monitor") { ShowInTaskbar = false, Opacity = 0 };
            memberships.Show();
            try
            {
                var inspection = (TextBox)memberships.FindName("Inspection");
                check(((TextBox)memberships.FindName("MemberRole")).Text == "pg_monitor" && await Eventually(() => inspection.Text.Length > 0), "Role Memberships opened on a role fills it in and reads its memberships");
            }
            finally { memberships.Close(); }
        }
        finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE;"); }
    }
}
