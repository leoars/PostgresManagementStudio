using Npgsql;
using PGMS.PostgreSql;

// PGMS-013: roles reached through memberships, with the shortest path and the server's INHERIT and SET answers.
internal static class EffectiveMembershipChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        string s = Guid.NewGuid().ToString("N")[..10];
        string member = "em_member_" + s, mid = "em_mid_" + s, top = "em_top_" + s, side = "em_side_" + s, a = "em_a_" + s, b = "em_b_" + s, c = "em_c_" + s, lone = "em_lone_" + s;
        string[] roles = [member, mid, top, side, a, b, c, lone];
        string Q(string r) => SqlIdentifier.Quote(r);
        await using var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
        async Task Sql(string sql) { await using var cmd = new NpgsqlCommand(sql, admin); await cmd.ExecuteNonQueryAsync(); }
        try
        {
            await Sql(string.Join(";", roles.Select(r => $"CREATE ROLE {Q(r)}")));
            // member -> mid without INHERIT, mid -> top (defaults), member -> side without SET, and a diamond member -> a -> c, member -> b -> c.
            await Sql($"GRANT {Q(mid)} TO {Q(member)} WITH INHERIT FALSE; GRANT {Q(top)} TO {Q(mid)}; GRANT {Q(side)} TO {Q(member)} WITH SET FALSE;" +
                      $"GRANT {Q(a)} TO {Q(member)}; GRANT {Q(b)} TO {Q(member)}; GRANT {Q(c)} TO {Q(a)}; GRANT {Q(c)} TO {Q(b)};");
            var service = new RoleMembershipService(connection);
            var result = await service.InspectEffectiveAsync(member);
            EffectiveMembership Row(string role) => result.Rows.Single(r => r.Role == role);
            check(result.Rows.Select(r => r.Role).Order().SequenceEqual(new[] { mid, top, side, a, b, c }.Order()) && !result.Truncated, "Effective memberships list every role reached, direct and through other roles");
            check(Row(mid).Depth == 1 && Row(top).Depth == 2 && Row(top).Path == $"{member} → {mid} → {top}", "A role reached through another one shows its depth and path");
            check(result.Rows.Count(r => r.Role == c) == 1 && Row(c).Depth == 2 && (Row(c).Path == $"{member} → {a} → {c}" || Row(c).Path == $"{member} → {b} → {c}"), "A role reached by two paths appears once, with a shortest path");
            check(!Row(mid).InheritsPrivileges && !Row(top).InheritsPrivileges, "A grant without INHERIT stops automatic privileges for that role and the roles beyond it");
            check(Row(side).InheritsPrivileges && !Row(side).CanSetRole, "A grant without SET keeps inherited privileges but refuses SET ROLE");
            check(Row(mid).CanSetRole && Row(top).CanSetRole && Row(c).InheritsPrivileges && Row(c).CanSetRole, "SET ROLE follows the server through the chain");
            // The flags are the server's answer.
            await using (var verify = new NpgsqlCommand($"SELECT pg_has_role({SqlLiteral(member)}, {SqlLiteral(top)}, 'USAGE'), pg_has_role({SqlLiteral(member)}, {SqlLiteral(side)}, 'SET')", admin))
            await using (var row = await verify.ExecuteReaderAsync()) { await row.ReadAsync(); check(row.GetBoolean(0) == Row(top).InheritsPrivileges && row.GetBoolean(1) == Row(side).CanSetRole, "Effective flags match pg_has_role"); }
            string text = result.ToText();
            check(text.Contains($"{top} | via {member} → {mid} → {top} | privileges: not inherited | SET ROLE: yes", StringComparison.Ordinal), "The effective list text names the role, path, privileges and SET ROLE");
            check((await service.InspectEffectiveAsync(lone)).ToText() == "The role is not a member of any other role.", "A role without memberships says so");
            bool missing = false; try { await service.InspectEffectiveAsync("em_missing_" + s); } catch (InvalidOperationException) { missing = true; }
            check(missing, "An unknown role is refused with the existing message");
            await Sql($"REVOKE {Q(top)} FROM {Q(mid)}");
            check(!(await service.InspectEffectiveAsync(member)).Rows.Any(r => r.Role == top), "Removing a link removes the roles reached only through it");
        }
        finally
        {
            foreach (var r in roles) await Sql($"DROP ROLE IF EXISTS {Q(r)}");
        }
    }
    private static string SqlLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}
