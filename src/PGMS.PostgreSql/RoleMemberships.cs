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

    public const int EffectiveLimit = 1000, MaximumDepth = 32;

    /// <summary>
    /// Every role the member reaches through role memberships, direct or through other roles (PGMS-013). The path shown
    /// is the shortest one; whether the member gets the role's privileges automatically (INHERIT) and may SET ROLE to it
    /// comes from pg_has_role, so the server's own rules decide, not this list. Read only.
    /// </summary>
    public async Task<EffectiveMemberships> InspectEffectiveAsync(string member, CancellationToken token = default)
    {
        SqlIdentifier.Quote(member);
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH RECURSIVE target AS (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=@member),
            chain AS (
                SELECT a.roleid, 1 AS depth, ARRAY[a.member, a.roleid] AS path
                FROM pg_catalog.pg_auth_members a JOIN target t ON a.member=t.oid
                UNION
                SELECT a.roleid, chain.depth+1, chain.path || a.roleid
                FROM chain JOIN pg_catalog.pg_auth_members a ON a.member=chain.roleid
                WHERE chain.depth < {MaximumDepth} AND NOT a.roleid = ANY(chain.path)
            ),
            shortest AS (SELECT DISTINCT ON (roleid) roleid, depth, path FROM chain ORDER BY roleid, depth, path)
            SELECT r.rolname, s.depth,
                   (SELECT string_agg(x.rolname, ' → ' ORDER BY u.ord) FROM unnest(s.path) WITH ORDINALITY AS u(oid, ord) JOIN pg_catalog.pg_roles x ON x.oid=u.oid),
                   pg_catalog.pg_has_role(t.oid, s.roleid, 'USAGE'), pg_catalog.pg_has_role(t.oid, s.roleid, 'SET'),
                   EXISTS(SELECT 1 FROM target)
            FROM shortest s JOIN pg_catalog.pg_roles r ON r.oid=s.roleid CROSS JOIN target t
            ORDER BY s.depth, r.rolname LIMIT {EffectiveLimit + 1}
            """, c) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("member", member);
        await using (var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname=@member)", c) { CommandTimeout = 15 })
        {
            exists.Parameters.AddWithValue("member", member);
            if (await exists.ExecuteScalarAsync(token).ConfigureAwait(false) is not true) throw new InvalidOperationException("Member role was not found. Check its exact name.");
        }
        var rows = new List<EffectiveMembership>(); bool truncated = false;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (rows.Count == EffectiveLimit) { truncated = true; break; }
            rows.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        }
        return new(rows, truncated);
    }
}

/// <summary>A role the member reaches. Depth 1 is a direct grant. InheritsPrivileges: pg_has_role USAGE. CanSetRole: pg_has_role SET.</summary>
public sealed record EffectiveMembership(string Role, int Depth, string Path, bool InheritsPrivileges, bool CanSetRole);
public sealed record EffectiveMemberships(IReadOnlyList<EffectiveMembership> Rows, bool Truncated)
{
    public string ToText()
    {
        if (Rows.Count == 0) return "The role is not a member of any other role.";
        var lines = Rows.Select(r => $"{r.Role} | via {r.Path} | privileges: {(r.InheritsPrivileges ? "inherited" : "not inherited")} | SET ROLE: {(r.CanSetRole ? "yes" : "no")}").ToList();
        if (Truncated) lines.Add($"Display limit reached ({RoleMembershipService.EffectiveLimit} roles).");
        return string.Join(Environment.NewLine, lines);
    }
}
