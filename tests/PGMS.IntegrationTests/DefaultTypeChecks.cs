using PGMS.PostgreSql;

internal static class DefaultTypeChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Admin($"GRANT USAGE,CREATE ON SCHEMA {f.QS} TO {f.QR}");await f.Owner($"CREATE TYPE {f.QS}.old_enum AS ENUM('one','two')");
        check(!await f.Apply(DefaultPrivilegeTarget.Types,"USAGE",revoke:true,isPublic:true),"Schema type revoke cannot subtract built-in global PUBLIC USAGE");
        check(await f.Apply(DefaultPrivilegeTarget.Types,"USAGE",revoke:true,isPublic:true,global:true),"Global type default removes PUBLIC for subsequent definitions");
        await f.Owner($"CREATE TYPE {f.QS}.denied_enum AS ENUM('one');CREATE DOMAIN {f.QS}.denied_domain AS integer");
        check(await f.Denied($"CREATE TABLE {f.QS}.denied_enum_table(value {f.QS}.denied_enum)") && await f.Denied($"CREATE TABLE {f.QS}.denied_domain_table(value {f.QS}.denied_domain)"),"Restricted future enum/domain cannot be used in recipient object definitions");
        await f.Reader($"CREATE TABLE {f.QS}.old_enum_table(value {f.QS}.old_enum)");check(true,"Type default revoke does not remove PUBLIC from an existing enum");
        check(await f.Apply(DefaultPrivilegeTarget.Types,"USAGE"),"Schema type policy adds USAGE for future enum/domain/composite types");
        await f.Owner($"CREATE TYPE {f.QS}.allowed_enum AS ENUM('one');CREATE DOMAIN {f.QS}.allowed_domain AS integer CHECK(VALUE>=0);CREATE TYPE {f.QS}.allowed_composite AS(value integer)");
        foreach(string type in new[]{"allowed_enum","allowed_domain","allowed_composite"})check(await f.Has("has_type_privilege",f.QS+"."+type,"USAGE"),"Creation-time type ACL includes recipient for "+type);
        await f.Reader($"CREATE TABLE {f.QS}.allowed_table(e {f.QS}.allowed_enum,d {f.QS}.allowed_domain,c {f.QS}.allowed_composite);INSERT INTO {f.QS}.allowed_table VALUES('one',1,ROW(2))");
        check(await f.ReaderCount($"SELECT count(*) FROM {f.QS}.allowed_table")==1,"Future type USAGE permits real table definitions and typed rows");
        await f.Reader($"CREATE FUNCTION {f.QS}.reader_fn({f.QS}.allowed_enum) RETURNS {f.QS}.allowed_enum LANGUAGE sql AS 'SELECT $1'");check(true,"Future type USAGE permits function signatures using that type");
        check(await f.Denied($"CREATE TABLE {f.QS}.still_denied(value {f.QS}.denied_enum)"),"Adding type template does not repair an earlier restricted enum");
        await f.Apply(DefaultPrivilegeTarget.Types,"USAGE",revoke:true);await f.Owner($"CREATE TYPE {f.QS}.after_revoke AS ENUM('one')");
        check(await f.Denied($"CREATE TABLE {f.QS}.later_denied(value {f.QS}.after_revoke)") && await f.Has("has_type_privilege",f.QS+".allowed_enum","USAGE"),"Revoking type template affects later definitions but preserves old ACL");
        await f.Admin($"REVOKE USAGE ON TYPE {f.QS}.allowed_enum,{f.QS}.allowed_domain,{f.QS}.allowed_composite FROM {f.QR}");
        await f.Reader($"SELECT e,d,c FROM {f.QS}.allowed_table;SELECT {f.QS}.reader_fn('one')");
        check(await f.Denied($"CREATE TABLE {f.QS}.new_definition_denied(value {f.QS}.allowed_enum)"),"Current type USAGE revoke blocks new dependencies without blocking existing typed data/function use");
        await f.Apply(DefaultPrivilegeTarget.Types,"USAGE",global:true,isPublic:true);await f.Owner($"CREATE TYPE {f.QS}.public_again AS ENUM('one')");
        check(await f.Has("has_type_privilege",f.QS+".public_again","USAGE") && !await f.Has("has_type_privilege",f.QS+".denied_enum","USAGE"),"Restored global PUBLIC applies only to newly created types");
    }
}
