using Npgsql;

namespace PGMS.PostgreSql;

public enum DatabaseOperation { Rename, Drop }

public sealed record DatabaseEdit(string Name, DatabaseOperation Operation, string NewName = "")
{
    private static readonly HashSet<string> Protected = new(["postgres", "template0", "template1"], StringComparer.OrdinalIgnoreCase);
    public string ToSql()
    {
        string name = SqlIdentifier.Quote(Name);
        if (Protected.Contains(Name)) throw new ArgumentException("System and maintenance databases cannot be changed here.");
        if (Operation == DatabaseOperation.Drop) return $"DROP DATABASE {name};";
        string newName = SqlIdentifier.Quote(NewName);
        if (string.Equals(Name, NewName, StringComparison.Ordinal)) throw new ArgumentException("Choose a different database name.");
        if (Protected.Contains(NewName)) throw new ArgumentException("System database names are reserved.");
        return $"ALTER DATABASE {name} RENAME TO {newName};";
    }
}

public sealed class DatabaseEditService(string connectionString)
{
    private NpgsqlConnectionStringBuilder MaintenanceConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        return builder;
    }
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnection().ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT datname FROM pg_catalog.pg_database ORDER BY datname", connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var names = new List<string>(); while (await reader.ReadAsync(token).ConfigureAwait(false)) names.Add(reader.GetString(0)); return names;
    }
    public async Task ApplyAsync(DatabaseEdit edit, CancellationToken token = default)
    {
        string sql = edit.ToSql();
        await using var connection = new NpgsqlConnection(MaintenanceConnection().ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        const string inspectSql = "SELECT datistemplate, datallowconn FROM pg_catalog.pg_database WHERE datname=@name";
        await using (var inspect = new NpgsqlCommand(inspectSql, connection) { CommandTimeout = 15 })
        {
            inspect.Parameters.AddWithValue("name", edit.Name);
            await using var reader = await inspect.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Database was not found. Inspect the current server catalog.");
            if (reader.GetBoolean(0) || !reader.GetBoolean(1)) throw new InvalidOperationException("Template or connection-disabled databases cannot be changed here.");
        }
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
