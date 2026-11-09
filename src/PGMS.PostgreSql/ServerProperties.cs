using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record ServerProperty(string Page, string Name, string Value, string Description);
public sealed record ServerSetting(string Name, string Value, string Unit, string Category, string Source, bool PendingRestart, string Description);
public sealed record ServerPropertiesSnapshot(IReadOnlyList<ServerProperty> Properties, IReadOnlyList<ServerSetting> Settings, DateTimeOffset ReadAt);

/// <summary>Read-only server information for the Server Properties dialog (SSMS: General, Connections, Security, Advanced),
/// and the reload that replaces SSMS Pause/Resume. Values the connected role may not read are reported as unavailable.</summary>
public sealed class ServerPropertiesService(string connectionString)
{
    public const string Unavailable = "(not visible to this role)";

    public async Task<ServerPropertiesSnapshot> ReadAsync(CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        var properties = new List<ServerProperty>();
        await using (var command = new NpgsqlCommand("""
            SELECT pg_catalog.version(), current_setting('server_version'), pg_catalog.pg_postmaster_start_time(),
                   date_trunc('second', now() - pg_catalog.pg_postmaster_start_time())::text, pg_catalog.pg_is_in_recovery(),
                   current_setting('data_directory', true), current_setting('config_file', true), current_setting('hba_file', true),
                   current_setting('server_encoding'), current_setting('TimeZone'),
                   (SELECT datcollate FROM pg_catalog.pg_database WHERE datname = current_database()),
                   current_setting('max_connections'), current_setting('superuser_reserved_connections'),
                   (SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE backend_type = 'client backend'),
                   (SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE backend_type = 'client backend' AND state = 'active'),
                   current_setting('listen_addresses', true), current_setting('port'),
                   current_setting('ssl'), current_setting('password_encryption'),
                   (SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()),
                   (SELECT version FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()),
                   current_user, (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user),
                   (SELECT count(*) FROM pg_catalog.pg_database WHERE NOT datistemplate),
                   (SELECT pg_catalog.pg_size_pretty(sum(pg_catalog.pg_database_size(oid))) FROM pg_catalog.pg_database
                     WHERE NOT datistemplate AND pg_catalog.has_database_privilege(oid, 'CONNECT')),
                   current_setting('shared_buffers'), current_setting('work_mem'), current_setting('effective_cache_size'), current_setting('max_worker_processes')
            """, connection) { CommandTimeout = 30 })
        await using (var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            await r.ReadAsync(token).ConfigureAwait(false);
            string S(int i) => r.IsDBNull(i) ? Unavailable : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            void P(string page, string name, string value, string description) => properties.Add(new(page, name, value, description));
            string full = S(0);
            P("General", "Name", $"{b.Host}:{b.Port}", "Server address and port used by this connection.");
            P("General", "Product", "PostgreSQL", "Database engine.");
            P("General", "Version", S(1), "server_version.");
            P("General", "Version details", full, "version(): build, compiler and platform.");
            P("General", "Platform", full.Contains("Windows", StringComparison.OrdinalIgnoreCase) || full.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) ? "Windows" : full.Contains("linux", StringComparison.OrdinalIgnoreCase) ? "Linux" : full.Contains("darwin", StringComparison.OrdinalIgnoreCase) ? "macOS" : "Other", "Operating system, inferred from version().");
            P("General", "Started", r.IsDBNull(2) ? Unavailable : r.GetFieldValue<DateTime>(2).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "pg_postmaster_start_time(), local time.");
            P("General", "Uptime", S(3), "Time since the server started.");
            P("General", "In recovery (standby)", S(4), "pg_is_in_recovery(): true on a standby replica.");
            P("General", "Data directory", S(5), "data_directory (requires pg_read_all_settings or superuser).");
            P("General", "Configuration file", S(6), "config_file.");
            P("General", "HBA file", S(7), "hba_file: client authentication rules.");
            P("General", "Server encoding", S(8), "server_encoding.");
            P("General", "Collation of this database", S(10), "pg_database.datcollate of the connected database.");
            P("General", "Time zone", S(9), "TimeZone of this session.");
            P("General", "Databases", S(23), "Databases that are not templates.");
            P("General", "Size of accessible databases", S(24), "Sum of pg_database_size for databases this role can connect to.");
            P("Memory", "shared_buffers", S(25), "Memory for shared data pages (restart required to change).");
            P("Memory", "work_mem", S(26), "Memory per sort or hash operation.");
            P("Memory", "effective_cache_size", S(27), "Planner estimate of the cache available to one query.");
            P("Memory", "max_worker_processes", S(28), "Background worker processes (restart required to change).");
            P("Connections", "listen_addresses", S(15), "Addresses the server listens on (restart required to change).");
            P("Connections", "port", S(16), "TCP port (restart required to change).");
            P("Connections", "max_connections", S(11), "Maximum concurrent connections (restart required to change).");
            P("Connections", "superuser_reserved_connections", S(12), "Connections kept for superusers.");
            P("Connections", "Client sessions", S(13), "Client backends in pg_stat_activity.");
            P("Connections", "Active sessions", S(14), "Client backends running a statement now.");
            P("Security", "ssl", S(17), "Whether the server accepts TLS connections.");
            P("Security", "password_encryption", S(18), "Algorithm used for new passwords.");
            P("Security", "This connection uses TLS", S(19), "pg_stat_ssl for this session.");
            P("Security", "TLS version", S(20), "Protocol of this session, when TLS is used.");
            P("Security", "Connected role", S(21), "current_user.");
            P("Security", "Superuser", S(22), "Whether the connected role is a superuser.");
        }
        var settings = new List<ServerSetting>();
        await using (var command = new NpgsqlCommand("SELECT name, setting, coalesce(unit, ''), category, source, pending_restart, short_desc FROM pg_catalog.pg_settings ORDER BY category, name", connection) { CommandTimeout = 30 })
        await using (var r = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await r.ReadAsync(token).ConfigureAwait(false))
                settings.Add(new(r.GetString(0), r.IsDBNull(1) ? Unavailable : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.IsDBNull(6) ? "" : r.GetString(6)));
        return new(properties, settings, DateTimeOffset.Now);
    }

    /// <summary>pg_reload_conf(): applies configuration file and ALTER SYSTEM changes that do not need a restart.
    /// Returns the settings still waiting for a restart. The reload is asynchronous on the server, so it is re-read briefly.</summary>
    public async Task<IReadOnlyList<string>> ReloadAsync(CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using (var reload = new NpgsqlCommand("SELECT pg_catalog.pg_reload_conf()", connection) { CommandTimeout = 30 })
            if (await reload.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw new InvalidOperationException("The server did not accept the reload signal.");
        await Task.Delay(500, token).ConfigureAwait(false);
        await using var pending = new NpgsqlCommand("SELECT name FROM pg_catalog.pg_settings WHERE pending_restart ORDER BY name", connection) { CommandTimeout = 30 };
        var names = new List<string>();
        await using var r = await pending.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await r.ReadAsync(token).ConfigureAwait(false)) names.Add(r.GetString(0));
        return names;
    }
}
