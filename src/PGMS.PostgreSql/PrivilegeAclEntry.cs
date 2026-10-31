namespace PGMS.PostgreSql;

public enum PrivilegeAclSource { Stored, BuiltInDefault }

// Catalog entries explain grants; they are not a complete proof of effective access.
public sealed record PrivilegeAclEntry(
    string Scope, string? Column, short? ColumnNumber,
    uint GrantorOid, string Grantor, uint GranteeOid, string Grantee,
    string Privilege, bool GrantOption, PrivilegeAclSource Source, bool RoleAvailable)
{
    public string SourceText => Source == PrivilegeAclSource.Stored ? "Stored ACL" : "Built-in default";
}
