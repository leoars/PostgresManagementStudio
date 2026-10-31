using Npgsql;

namespace PGMS.PostgreSql;

public sealed record RoleRenameEdit(string Name, string NewName) : IAdministrativeEdit
{
    private static bool IsReserved(string name) => name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase);
    public string ToSql()
    {
        if (IsReserved(Name) || IsReserved(NewName)) throw new ArgumentException("PostgreSQL predefined role names beginning with pg_ are protected.");
        if (string.Equals(Name, NewName, StringComparison.Ordinal)) throw new ArgumentException("Choose a different role name.");
        return $"ALTER ROLE {SqlIdentifier.Quote(Name)} RENAME TO {SqlIdentifier.Quote(NewName)};";
    }
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_user", connection, transaction) { CommandTimeout = 15 };
        string current = (string)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        if (string.Equals(current, Name, StringComparison.Ordinal)) throw new InvalidOperationException("The role used by this administrative connection cannot be renamed.");
    }
}

public sealed record RoleDropEdit(string Name) : IAdministrativeEdit
{
    public string ToSql()
    {
        if (Name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("PostgreSQL predefined roles beginning with pg_ are protected.");
        return $"DROP ROLE {SqlIdentifier.Quote(Name)};";
    }
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_user", connection, transaction) { CommandTimeout = 15 };
        string current = (string)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        if (string.Equals(current, Name, StringComparison.Ordinal)) throw new InvalidOperationException("The role used by this administrative connection cannot be dropped.");
    }
}
