using Npgsql;
using PGMS.PostgreSql;

internal static class SequenceIdentifierChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string suffix = Guid.NewGuid().ToString("N"), name = "seq_é'\"_" + suffix, tableName = "tab_é'\"_" + suffix;
        string target = ObjectSql.Relation("public", name), table = ObjectSql.Relation("public", tableName), column = SqlIdentifier.Quote("id'\"é");
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        async Task Sql(string sql) { await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
        var service = new SequenceService(connection); var edits = new AdministrativeEditService(connection);
        try
        {
            await Sql($"CREATE TABLE {table}({column} bigint); CREATE SEQUENCE {target} MINVALUE -100 MAXVALUE 100 START 1 OWNED BY {table}.{column};");
            var before = await service.InspectAsync("public", name);
            check(before.Name == name && before.Dependency?.ColumnNumber == 1, "Special identifiers preserve inspected sequence and column identity");
            await edits.ApplyAsync(new SequenceRestartEdit("public", name, -20) { Expected = before });
            await using (var c = new NpgsqlCommand("SELECT nextval(@target::regclass)", db))
            {
                c.Parameters.AddWithValue("target", target);
                check(Equals(await c.ExecuteScalarAsync(), -20L), "Restart handles Unicode quotes and apostrophes in identifiers");
            }
            await edits.ApplyAsync(new SequenceDetachEdit("public", name) { Expected = before });
            check((await service.InspectAsync("public", name)).Dependency is null, "Detach handles quoted column and relation identifiers");
            await Sql($"DROP TABLE {table};");
            check((await service.InspectAsync("public", name)).Oid == before.Oid, "Special-name sequence survives former owner removal");
        }
        finally { await Sql($"DROP TABLE IF EXISTS {table}; DROP SEQUENCE IF EXISTS {target};"); }
    }
}
