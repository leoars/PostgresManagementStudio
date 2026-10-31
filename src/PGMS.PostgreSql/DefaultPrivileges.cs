namespace PGMS.PostgreSql;

public enum DefaultPrivilegeTarget { Tables, Sequences, Routines, Types, Schemas }

// A future-object policy: never interpret it as a grant on existing objects.
// Null Schema means database-wide; an empty schema is invalid, not global.
public sealed record DefaultPrivilegeRequest(DefaultPrivilegeTarget Target, string Creator,
    string Role, string Privilege, string? Schema = null, bool Revoke = false,
    bool RecipientIsPublic = false, bool GrantOption = false)
{
    public static string[] Allowed(DefaultPrivilegeTarget target) => target switch
    {
        DefaultPrivilegeTarget.Tables => PrivilegeRequest.Allowed(PrivilegeTarget.Table),
        DefaultPrivilegeTarget.Sequences => PrivilegeRequest.Allowed(PrivilegeTarget.Sequence),
        DefaultPrivilegeTarget.Routines => ["EXECUTE"],
        DefaultPrivilegeTarget.Types => ["USAGE"],
        DefaultPrivilegeTarget.Schemas => ["USAGE", "CREATE"],
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    internal static string CatalogKind(DefaultPrivilegeTarget target) => target switch
    {
        DefaultPrivilegeTarget.Tables => "r", DefaultPrivilegeTarget.Sequences => "S",
        DefaultPrivilegeTarget.Routines => "f", DefaultPrivilegeTarget.Types => "T",
        DefaultPrivilegeTarget.Schemas => "n", _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public string ToSql()
    {
        if (!Allowed(Target).Contains(Privilege, StringComparer.Ordinal))
            throw new ArgumentException("Select a default privilege supported by this object class.");
        if (Target == DefaultPrivilegeTarget.Schemas && Schema is not null)
            throw new ArgumentException("Schema defaults require database-wide scope.");
        if (RecipientIsPublic && GrantOption) throw new ArgumentException("PUBLIC cannot receive grant options.");
        string creator = SqlIdentifier.Quote(Creator);
        string scope = Schema is null ? "" : " IN SCHEMA " + SqlIdentifier.Quote(Schema);
        string recipient = RecipientIsPublic ? "PUBLIC" : SqlIdentifier.Quote(Role);
        string action = Revoke ? GrantOption ? "REVOKE GRANT OPTION FOR" : "REVOKE" : "GRANT";
        return $"ALTER DEFAULT PRIVILEGES FOR ROLE {creator}{scope} {action} {Privilege} ON {Target.ToString().ToUpperInvariant()} {(Revoke ? "FROM" : "TO")} {recipient}{(Revoke ? " RESTRICT" : GrantOption ? " WITH GRANT OPTION" : "")};";
    }
}
