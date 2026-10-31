using PGMS.PostgreSql;

internal static class DefaultSequenceChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        await f.Admin($"GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");await f.Owner($"CREATE SEQUENCE {f.QS}.old");
        check(await f.Apply(DefaultPrivilegeTarget.Sequences,"USAGE"),"USAGE default policy commits for future sequences");
        await f.Owner($"CREATE SEQUENCE {f.QS}.with_usage");
        check(await f.ReaderCount($"SELECT nextval('{f.QS}.with_usage')")==1 && await f.ReaderCount($"SELECT currval('{f.QS}.with_usage')")==1,"Future USAGE default enables nextval and session currval");
        check(await f.Denied($"SELECT setval('{f.QS}.with_usage',9)") && await f.Denied($"SELECT last_value FROM {f.QS}.with_usage"),"USAGE default does not grant UPDATE or SELECT access");
        check(await f.Denied($"SELECT nextval('{f.QS}.old')"),"Sequence default does not grant access to a previously existing sequence");
        await f.Apply(DefaultPrivilegeTarget.Sequences,"USAGE",revoke:true);await f.Owner($"CREATE SEQUENCE {f.QS}.without_usage");
        check(await f.Denied($"SELECT nextval('{f.QS}.without_usage')") && await f.ReaderCount($"SELECT nextval('{f.QS}.with_usage')")==2,"Revoked USAGE template affects next sequence while prior grant remains");
        await f.Apply(DefaultPrivilegeTarget.Sequences,"SELECT");await f.Owner($"CREATE SEQUENCE {f.QS}.with_select");
        check(await f.ReaderCount($"SELECT last_value FROM {f.QS}.with_select")==1,"Future SELECT default exposes stored sequence value");
        check(await f.Denied($"SELECT nextval('{f.QS}.with_select')") && await f.Denied($"SELECT setval('{f.QS}.with_select',9)"),"SELECT default does not authorize sequence mutations");
        await f.Admin($"GRANT USAGE ON SEQUENCE {f.QS}.with_select TO {f.QR}");await f.Reader($"SELECT nextval('{f.QS}.with_select')");await f.Admin($"REVOKE USAGE ON SEQUENCE {f.QS}.with_select FROM {f.QR}");
        check(await f.ReaderCount($"SELECT currval('{f.QS}.with_select')")==1,"SELECT default supports currval after session initialization without remaining USAGE");
        await f.Apply(DefaultPrivilegeTarget.Sequences,"SELECT",revoke:true);await f.Apply(DefaultPrivilegeTarget.Sequences,"UPDATE");await f.Owner($"CREATE SEQUENCE {f.QS}.with_update");
        check(await f.ReaderCount($"SELECT setval('{f.QS}.with_update',40,false)")==40 && await f.ReaderCount($"SELECT nextval('{f.QS}.with_update')")==40,"Future UPDATE default enables setval and nextval");
        check(await f.Denied($"SELECT currval('{f.QS}.with_update')") && await f.Denied($"SELECT last_value FROM {f.QS}.with_update"),"UPDATE default does not imply read privileges");
        await f.Apply(DefaultPrivilegeTarget.Sequences,"UPDATE",revoke:true);await f.Apply(DefaultPrivilegeTarget.Tables,"INSERT");
        await f.Owner($"CREATE TABLE {f.QS}.serial_denied(id serial PRIMARY KEY)");
        check(await f.Has("has_table_privilege",f.QS+".serial_denied","INSERT") && await f.Denied($"INSERT INTO {f.QS}.serial_denied DEFAULT VALUES"),"Future table INSERT default alone cannot consume its serial sequence");
        await f.Apply(DefaultPrivilegeTarget.Sequences,"USAGE");await f.Owner($"CREATE TABLE {f.QS}.serial_allowed(id serial PRIMARY KEY)");
        await f.Reader($"INSERT INTO {f.QS}.serial_allowed DEFAULT VALUES");
        check(await f.Has("has_sequence_privilege",f.QS+".serial_allowed_id_seq","USAGE"),"Future sequence USAGE default makes a new serial INSERT usable");
        check(await f.Denied($"INSERT INTO {f.QS}.serial_denied DEFAULT VALUES"),"Later sequence default never repairs an earlier serial sequence retroactively");
    }
}
