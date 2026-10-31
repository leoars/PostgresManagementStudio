using Npgsql;

namespace PGMS.PostgreSql;

public sealed record DefaultPrivilegeCreator(uint DatabaseOid, uint Oid, string Name, bool CanLogin);

public sealed class DefaultPrivilegeCreators(string connectionString)
{
    public async Task<IReadOnlyList<DefaultPrivilegeCreator>> ListAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT d.oid,r.oid,r.rolname,r.rolcanlogin
            FROM pg_catalog.pg_roles r CROSS JOIN pg_catalog.pg_database d
            WHERE d.datname=pg_catalog.current_database() ORDER BY r.rolname,r.oid
            """, connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<DefaultPrivilegeCreator>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            rows.Add(new(reader.GetFieldValue<uint>(0), reader.GetFieldValue<uint>(1), reader.GetString(2), reader.GetBoolean(3)));
        return rows.AsReadOnly();
    }
}
