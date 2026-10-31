using Npgsql;

namespace PGMS.PostgreSql;
public enum MembershipOption { Admin, Inherit, Set }
public sealed record RoleMembershipEdit(string GrantedRole, string MemberRole, bool Revoke = false, MembershipOption? Option = null, bool OptionValue = true) : IAdministrativeEdit
{
    public string ToSql()
    {
        string granted = SqlIdentifier.Quote(GrantedRole), member = SqlIdentifier.Quote(MemberRole);
        if (GrantedRole == MemberRole) throw new ArgumentException("A role cannot be a member of itself.");
        if (Option is { } selected && !Enum.IsDefined(selected)) throw new ArgumentException("Select a supported membership option.");
        string option=Option?.ToString().ToUpperInvariant() ?? "";
        return Revoke
            ? $"REVOKE {(Option is null ? "" : option+" OPTION FOR ")}{granted} FROM {member} RESTRICT;"
            : $"GRANT {granted} TO {member}{(Option is null ? "" : " WITH "+option+" "+(OptionValue ? "TRUE" : "FALSE"))};";
    }
}

public sealed class RoleMembershipService(string connectionString)
{
    public async Task<string> InspectAsync(string member, CancellationToken token = default)
    {
        SqlIdentifier.Quote(member);
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        await using (var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=@member)", c) { CommandTimeout = 15 })
        {
            exists.Parameters.AddWithValue("member", member);
            if (await exists.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw new InvalidOperationException("Member role was not found. Check its exact name.");
        }
        await using var command = new NpgsqlCommand("""
            SELECT granted.rolname, grantor.rolname, m.admin_option, m.inherit_option, m.set_option
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles granted ON granted.oid=m.roleid
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            JOIN pg_catalog.pg_roles grantor ON grantor.oid=m.grantor
            WHERE member.rolname=@member ORDER BY granted.rolname,grantor.rolname LIMIT 1001
            """, c) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("member", member);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var lines = new List<string>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (lines.Count == 1000) { lines.Add("Display limit reached (1000 direct grants)."); break; }
            lines.Add($"{reader.GetString(0)} | grantor: {reader.GetString(1)} | ADMIN={reader.GetBoolean(2)} INHERIT={reader.GetBoolean(3)} SET={reader.GetBoolean(4)}");
        }
        return lines.Count == 0 ? "No direct role memberships found." : string.Join(Environment.NewLine, lines);
    }
}
