using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

public enum CreateObjectKind { Database, Schema }

public sealed record CreateObjectRequest(CreateObjectKind Kind, string Name, string? Owner = null)
{
    public string ToSql()
    {
        string name = SqlIdentifier.Quote(Name);
        if (Kind == CreateObjectKind.Schema && Name.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("Schema names beginning with pg_ are reserved by PostgreSQL.");
        string owner = string.IsNullOrWhiteSpace(Owner) ? "" : SqlIdentifier.Quote(Owner);
        return Kind switch
        {
            CreateObjectKind.Database => $"CREATE DATABASE {name}" + (owner.Length > 0 ? $" OWNER {owner}" : "") + ";",
            CreateObjectKind.Schema => $"CREATE SCHEMA {name}" + (owner.Length > 0 ? $" AUTHORIZATION {owner}" : "") + ";",
            _ => throw new ArgumentOutOfRangeException(nameof(Kind))
        };
    }
}

public static class SqlIdentifier
{
    public static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Enter an object name.");
        if (value.Contains('\0')) throw new ArgumentException("Names cannot contain a null character.");
        if (Encoding.UTF8.GetByteCount(value) > 63) throw new ArgumentException("Names must fit in 63 UTF-8 bytes; PostgreSQL would otherwise truncate them.");
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

public sealed class AdministrationService(string connectionString)
{
    public async Task CreateAsync(CreateObjectRequest request, CancellationToken token = default)
    {
        string sql = request.ToSql();
        // CREATE DATABASE must run outside a transaction. Administrative work never borrows
        // an editor session, so an open user transaction is not committed or rolled back.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
