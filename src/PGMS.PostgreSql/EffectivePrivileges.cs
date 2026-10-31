namespace PGMS.PostgreSql;

public sealed record EffectivePrivilege(string Scope, string? Column, short? ColumnNumber, string Privilege, bool Allowed, bool GrantOption);

// A read-only observation of the connected role, never an authorization to mutate.
public sealed record EffectivePrivilegeSnapshot(
    PrivilegeTarget Target, string Name, string? Schema, uint Oid, string Database,
    string Role, string SessionUser, DateTimeOffset ObservedAt, string Owner, bool? SchemaUsage, bool? RowSecurityEnabled,
    bool? RowSecurityForced, bool? RowSecurityActive,
    IReadOnlyList<EffectivePrivilege> Privileges, bool ColumnsTruncated)
{
    public IReadOnlyList<PrivilegeAclEntry> AclEntries { get; init; } = Array.Empty<PrivilegeAclEntry>();
    public bool AclTruncated { get; init; }
    public bool AclIncomplete => AclTruncated || ColumnsTruncated;
    public PrivilegeTargetIdentity? TargetIdentity { get; init; }
    public PrivilegeRecipientIdentity? RecipientIdentity { get; init; }
}
