using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public interface IAdministrativeEdit
{
    string ToSql();
    int CommandTimeoutSeconds => 30;
    Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default) => Task.CompletedTask;
    Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default) => Task.CompletedTask;
}
public enum RoleAttribute { Login, Inherit, CreateDatabase, CreateRole, Replication, BypassRls, ConnectionLimit }
public sealed record RoleAttributeEdit(string Name, RoleAttribute Attribute, string Value) : IAdministrativeEdit
{
    public string ToSql()
    {
        string name = SqlIdentifier.Quote(Name);
        string option;
        if (Attribute == RoleAttribute.ConnectionLimit)
        {
            if (!int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) || limit < -1)
                throw new ArgumentException("Connection limit must be an integer >= -1.");
            option = "CONNECTION LIMIT " + limit.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (!bool.TryParse(Value, out bool enabled)) throw new ArgumentException("Enter true or false for this attribute.");
            string keyword = Attribute switch
            {
                RoleAttribute.Login => "LOGIN", RoleAttribute.Inherit => "INHERIT", RoleAttribute.CreateDatabase => "CREATEDB",
                RoleAttribute.CreateRole => "CREATEROLE", RoleAttribute.Replication => "REPLICATION", RoleAttribute.BypassRls => "BYPASSRLS",
                _ => throw new ArgumentException("Unsupported role attribute.")
            };
            option = (enabled ? "" : "NO") + keyword;
        }
        return $"ALTER ROLE {name} WITH {option};";
    }
}

public sealed class AdministrativeEditService(string connectionString)
{
    public async Task ApplyAsync(IAdministrativeEdit edit, CancellationToken token = default)
    {
        string sql = edit.ToSql();
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await c.BeginTransactionAsync(token).ConfigureAwait(false);
        string? notice = null;
        c.Notice += (_, e) => notice = e.Notice.MessageText;
        await edit.ValidateAsync(c, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, c, transaction) { CommandTimeout = edit.CommandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await edit.VerifyAppliedAsync(c, transaction, token).ConfigureAwait(false);
        if (notice is not null) throw new InvalidOperationException("Operation was rolled back after PostgreSQL notice: " + notice);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task<string> InspectRoleAsync(string name, CancellationToken token = default)
    {
        SqlIdentifier.Quote(name);
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT rolcanlogin, rolinherit, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolconnlimit FROM pg_catalog.pg_roles WHERE rolname=@name", c) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Role was not found. Check the exact name.");
        string[] labels = ["LOGIN", "INHERIT", "CREATEDB", "CREATEROLE", "REPLICATION", "BYPASSRLS", "CONNECTION LIMIT"];
        return string.Join(Environment.NewLine, labels.Select((label, index) => label + " = " + Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)));
    }
}
