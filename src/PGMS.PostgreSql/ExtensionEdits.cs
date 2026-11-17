using Npgsql;

namespace PGMS.PostgreSql;

/// <summary>Extensions folder of a database (PGMS-060, M1090): CREATE, ALTER … UPDATE and DROP EXTENSION.
/// An installed extension is found by OID and must still have the name the tree showed; Delete never uses CASCADE.</summary>
public static class ExtensionEdits
{
    public sealed record Available(string Name, string? DefaultVersion, string? InstalledVersion, string? Comment);
    public sealed record Installed(uint Oid, string Name, string Version, string Schema, bool Relocatable, string? Comment, IReadOnlyList<string> UpdateTargets);

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>CREATE EXTENSION; a null or empty schema/version lets the server use the control file defaults.</summary>
    public static string CreateSql(string name, string? schema = null, string? version = null, bool cascade = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Choose an extension.");
        return "CREATE EXTENSION " + SqlIdentifier.Quote(name)
            + (string.IsNullOrWhiteSpace(schema) ? "" : " SCHEMA " + SqlIdentifier.Quote(schema))
            + (string.IsNullOrWhiteSpace(version) ? "" : " VERSION " + Literal(version))
            + (cascade ? " CASCADE" : "") + ";";
    }

    /// <summary>ALTER EXTENSION … UPDATE; without a version the server moves to the default version of the control file.</summary>
    public static string UpdateSql(string name, string? version = null) =>
        "ALTER EXTENSION " + SqlIdentifier.Quote(name) + " UPDATE" + (string.IsNullOrWhiteSpace(version) ? "" : " TO " + Literal(version)) + ";";

    public static string DropSql(string name) => "DROP EXTENSION " + SqlIdentifier.Quote(name) + ";";

    /// <summary>Every extension the server can install, with the version already installed in this database, if any.</summary>
    public static async Task<IReadOnlyList<Available>> ListAvailableAsync(string connectionString, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT name, default_version, installed_version, comment FROM pg_catalog.pg_available_extensions ORDER BY name", connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(token);
        var list = new List<Available>();
        while (await reader.ReadAsync(token))
            list.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return list;
    }

    /// <summary>Versions of one extension that the server has scripts for, default first.</summary>
    public static async Task<IReadOnlyList<string>> VersionsAsync(string connectionString, string name, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT v.version FROM pg_catalog.pg_available_extension_versions v LEFT JOIN pg_catalog.pg_available_extensions a ON a.name=v.name AND a.default_version=v.version WHERE v.name=@name ORDER BY a.name IS NULL, v.version DESC", connection) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(token);
        var list = new List<string>();
        while (await reader.ReadAsync(token)) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>The installed extension with the versions it can be updated to; throws when it was dropped or renamed since the tree loaded.</summary>
    public static async Task<Installed> InspectAsync(string connectionString, uint oid, string expectedName, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        Installed? found = null;
        await using (var command = new NpgsqlCommand("SELECT e.extname, e.extversion, n.nspname, e.extrelocatable, pg_catalog.obj_description(e.oid, 'pg_extension') FROM pg_catalog.pg_extension e JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace WHERE e.oid=@oid", connection) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, oid);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                found = new(oid, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4), []);
        }
        if (found is null) throw new InvalidOperationException($"The extension {expectedName} is no longer installed. Refresh Object Explorer.");
        if (found.Name != expectedName) throw new InvalidOperationException($"The extension is now named {found.Name}, not {expectedName}. Refresh Object Explorer.");
        var targets = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT target FROM pg_catalog.pg_extension_update_paths(@name) WHERE source=@version AND path IS NOT NULL ORDER BY target DESC", connection) { CommandTimeout = 15 })
        {
            command.Parameters.AddWithValue("name", found.Name);
            command.Parameters.AddWithValue("version", found.Version);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) targets.Add(reader.GetString(0));
        }
        return found with { UpdateTargets = targets };
    }

    /// <summary>Runs one statement built by this class.</summary>
    public static async Task ExecuteAsync(string connectionString, string sql, CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>DROP EXTENSION after checking that the OID still has the expected name.</summary>
    public static async Task<string> DropAsync(string connectionString, uint oid, string expectedName, CancellationToken token = default)
    {
        var installed = await InspectAsync(connectionString, oid, expectedName, token);
        string sql = DropSql(installed.Name);
        await ExecuteAsync(connectionString, sql, token);
        return sql;
    }
}
