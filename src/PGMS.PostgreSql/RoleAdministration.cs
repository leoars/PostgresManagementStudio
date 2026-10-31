using Npgsql;

namespace PGMS.PostgreSql;

public sealed record CreateRoleRequest(
    string Name,
    bool CanLogin = true,
    bool Inherit = true,
    bool CreateDatabase = false,
    bool CreateRole = false,
    bool Replication = false,
    bool BypassRowLevelSecurity = false,
    int ConnectionLimit = -1,
    IReadOnlyList<string>? MemberOf = null)
{
    public IReadOnlyList<string> Memberships => MemberOf ?? [];

    public string CreateSql()
    {
        string name = SqlIdentifier.Quote(Name);
        if (ConnectionLimit < -1) throw new ArgumentException("Connection limit must be -1 or greater.");
        string[] memberships = Memberships.Distinct(StringComparer.Ordinal).ToArray();
        if (memberships.Any(role => string.Equals(role, Name, StringComparison.Ordinal)))
            throw new ArgumentException("A role cannot be a member of itself.");
        foreach (string membership in memberships) SqlIdentifier.Quote(membership);
        return $"CREATE ROLE {name} WITH " +
            $"{(CanLogin ? "LOGIN" : "NOLOGIN")} {(Inherit ? "INHERIT" : "NOINHERIT")} " +
            $"{(CreateDatabase ? "CREATEDB" : "NOCREATEDB")} {(CreateRole ? "CREATEROLE" : "NOCREATEROLE")} " +
            $"{(Replication ? "REPLICATION" : "NOREPLICATION")} {(BypassRowLevelSecurity ? "BYPASSRLS" : "NOBYPASSRLS")} " +
            $"CONNECTION LIMIT {ConnectionLimit};";
    }

    public string ToSqlScript()
    {
        string create = CreateSql();
        string[] memberships = Memberships.Distinct(StringComparer.Ordinal).ToArray();
        return memberships.Length == 0
            ? create
            : create + Environment.NewLine + $"GRANT {string.Join(", ", memberships.Select(SqlIdentifier.Quote))} TO {SqlIdentifier.Quote(Name)};";
    }
}

public sealed class RoleAdministrationService(string connectionString)
{
    public async Task<IReadOnlyList<string>> ListRolesAsync(CancellationToken token = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT rolname FROM pg_catalog.pg_roles ORDER BY rolname", connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var roles = new List<string>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) roles.Add(reader.GetString(0));
        return roles;
    }

    public async Task CreateAsync(CreateRoleRequest request, CancellationToken token = default)
    {
        string createSql = request.CreateSql();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var create = new NpgsqlCommand(createSql, connection, transaction) { CommandTimeout = 60 })
            await create.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        string[] memberships = request.Memberships.Distinct(StringComparer.Ordinal).ToArray();
        if (memberships.Length > 0)
        {
            string grantSql = $"GRANT {string.Join(", ", memberships.Select(SqlIdentifier.Quote))} TO {SqlIdentifier.Quote(request.Name)};";
            await using var grant = new NpgsqlCommand(grantSql, connection, transaction) { CommandTimeout = 60 };
            await grant.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
