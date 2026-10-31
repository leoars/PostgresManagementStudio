using Npgsql;

namespace PGMS.PostgreSql;

public sealed record ConnectionTestResult(string Database, string User, string Version, bool Tls);

public sealed class ConnectionTestService(string connectionString)
{
    public async Task<ConnectionTestResult> TestAsync(CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT current_database(), current_user, version(), COALESCE((SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid=pg_backend_pid()), false)", connection) { CommandTimeout = 10 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("The server connection opened but did not return connection details.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
    }
}
