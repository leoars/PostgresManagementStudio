using System.Collections.Concurrent;
using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>
/// server_version_num of a server, read once per host and port. Used to leave out what an older server does not
/// have (for example the MAINTAIN table privilege, which exists from PostgreSQL 17).
/// </summary>
public static class ServerVersionNumber
{
    public const int Maintain = 170000;
    private static readonly ConcurrentDictionary<string, int> Known = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        return settings.Host + ":" + settings.Port;
    }

    /// <summary>The version already read for this server, or null when it was never read.</summary>
    public static int? Cached(string connectionString) => Known.TryGetValue(Key(connectionString), out int version) ? version : null;

    public static async Task<int> GetAsync(string connectionString, CancellationToken token = default)
    {
        string key = Key(connectionString);
        if (Known.TryGetValue(key, out int known)) return known;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT pg_catalog.current_setting('server_version_num')::integer", connection) { CommandTimeout = 15 };
        int version = (int)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        Known[key] = version;
        return version;
    }

    /// <summary>Forgets a server, for example after it was replaced by another version on the same port.</summary>
    public static void Forget(string connectionString) => Known.TryRemove(Key(connectionString), out _);
}
