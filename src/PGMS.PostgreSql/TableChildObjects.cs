using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>Rename and Delete of keys, constraints and triggers from Object Explorer (PGMS-053, M1086).
/// The object is found by OID and must still have the name the tree showed, so a stale tree never touches another object. Never CASCADE.</summary>
public static class TableChildObjects
{
    public sealed record Target(bool IsTrigger, string Relation, string Name);

    private static async Task<Target> ResolveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool trigger, uint oid, string expectedName, CancellationToken token)
    {
        // An empty search_path makes regclass print the schema-qualified, quoted name.
        await using (var path = new NpgsqlCommand("SET LOCAL search_path = ''", connection, transaction)) await path.ExecuteNonQueryAsync(token);
        string sql = trigger
            ? "SELECT tgname, tgrelid::regclass::text FROM pg_catalog.pg_trigger WHERE oid=@oid AND NOT tgisinternal"
            : "SELECT conname, conrelid::regclass::text FROM pg_catalog.pg_constraint WHERE oid=@oid AND conrelid<>0";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, oid);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException($"The {(trigger ? "trigger" : "constraint")} {expectedName} no longer exists. Refresh Object Explorer.");
        string name = reader.GetString(0), relation = reader.GetString(1);
        if (name != expectedName) throw new InvalidOperationException($"The {(trigger ? "trigger" : "constraint")} is now named {name}, not {expectedName}. Refresh Object Explorer.");
        return new Target(trigger, relation, name);
    }

    public static string RenameSql(Target target, string newName) => target.IsTrigger
        ? $"ALTER TRIGGER {SqlIdentifier.Quote(target.Name)} ON {target.Relation} RENAME TO {SqlIdentifier.Quote(newName)};"
        : $"ALTER TABLE {target.Relation} RENAME CONSTRAINT {SqlIdentifier.Quote(target.Name)} TO {SqlIdentifier.Quote(newName)};";

    public static string DropSql(Target target) => target.IsTrigger
        ? $"DROP TRIGGER {SqlIdentifier.Quote(target.Name)} ON {target.Relation};"
        : $"ALTER TABLE {target.Relation} DROP CONSTRAINT {SqlIdentifier.Quote(target.Name)};";

    public static string EnableSql(Target target, bool enable) => $"ALTER TABLE {target.Relation} {(enable ? "ENABLE" : "DISABLE")} TRIGGER {SqlIdentifier.Quote(target.Name)};";

    /// <summary>Enable / Disable of a trigger (PGMS-061, M1088).</summary>
    public static Task<string> SetTriggerEnabledAsync(string connectionString, uint oid, string expectedName, bool enable, CancellationToken token = default) =>
        RunAsync(connectionString, true, oid, expectedName, t => EnableSql(t, enable), token);

    /// <summary>The statement Delete would run, for the confirmation; throws when the object changed.</summary>
    public static async Task<string> DescribeDropAsync(string connectionString, bool trigger, uint oid, string expectedName, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        return DropSql(await ResolveAsync(connection, transaction, trigger, oid, expectedName, token));
    }

    public static Task<string> RenameAsync(string connectionString, bool trigger, uint oid, string expectedName, string newName, CancellationToken token = default)
    {
        SqlIdentifier.Quote(newName); // validates before connecting
        return RunAsync(connectionString, trigger, oid, expectedName, t => RenameSql(t, newName), token);
    }

    public static Task<string> DropAsync(string connectionString, bool trigger, uint oid, string expectedName, CancellationToken token = default) =>
        RunAsync(connectionString, trigger, oid, expectedName, DropSql, token);

    private static async Task<string> RunAsync(string connectionString, bool trigger, uint oid, string expectedName, Func<Target, string> statement, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        string sql = statement(await ResolveAsync(connection, transaction, trigger, oid, expectedName, token));
        await using (var command = new NpgsqlCommand(sql, connection, transaction)) await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return sql;
    }
}
