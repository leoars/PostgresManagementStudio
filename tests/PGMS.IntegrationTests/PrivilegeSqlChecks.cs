using PGMS.PostgreSql;

internal static class PrivilegeSqlChecks
{
    public static void Run(Action<bool,string> check)
    {
        foreach (var item in new[]
        {
            (PrivilegeTarget.Database,"db","CONNECT","DATABASE \"db\""),
            (PrivilegeTarget.Schema,"schema","USAGE","SCHEMA \"schema\""),
            (PrivilegeTarget.Table,"t","SELECT","TABLE \"s\".\"t\"")
        })
        {
            var request = new PrivilegeRequest(item.Item1,item.Item2,"worker",item.Item3,Schema:"s");
            string target=item.Item4, privilege=item.Item3;
            check(request.ToSql()==$"GRANT {privilege} ON {target} TO \"worker\";", "Legacy grant SQL preserved: "+item.Item1);
            check((request with { Revoke=true }).ToSql()==$"REVOKE {privilege} ON {target} FROM \"worker\" RESTRICT;", "Legacy revoke stays restrictive: "+item.Item1);
            check((request with { RecipientIsPublic=true,Role="" }).ToSql()==$"GRANT {privilege} ON {target} TO PUBLIC;", "Explicit PUBLIC grant needs no role name: "+item.Item1);
            check((request with { RecipientIsPublic=true,Role="",Revoke=true }).ToSql()==$"REVOKE {privilege} ON {target} FROM PUBLIC RESTRICT;", "Explicit PUBLIC revoke is restrictive: "+item.Item1);
            check((request with { GrantOption=true }).ToSql()==$"GRANT {privilege} ON {target} TO \"worker\" WITH GRANT OPTION;", "Delegable grant SQL: "+item.Item1);
            check((request with { GrantOption=true,Revoke=true }).ToSql()==$"REVOKE GRANT OPTION FOR {privilege} ON {target} FROM \"worker\" RESTRICT;", "Grant-option-only revoke SQL: "+item.Item1);
        }
        var table = new PrivilegeRequest(PrivilegeTarget.Table,"item'\"é","role'\"é","SELECT",Schema:"s'\"é",GrantOption:true);
        check(table.ToSql()=="GRANT SELECT ON TABLE \"s'\"\"é\".\"item'\"\"é\" TO \"role'\"\"é\" WITH GRANT OPTION;", "Delegation quotes exact Unicode identifiers rather than SQL fragments");
        check((table with {Role="PUBLIC",GrantOption=false}).ToSql().Contains("TO \"PUBLIC\";"), "Typing PUBLIC as a role never selects the PUBLIC keyword");
        foreach (var invalid in new[]
        {
            table with {RecipientIsPublic=true}, table with {RecipientIsPublic=true,Revoke=true},
            table with {Privilege="CONNECT"}, table with {Privilege="SELECT; DROP ROLE worker"},
            table with {Role=""}, table with {Name=""}, table with {Schema=""},
            table with {Target=(PrivilegeTarget)99}
        })
        {
            bool refused=false;
            try { invalid.ToSql(); } catch(ArgumentException) { refused=true; }
            check(refused,"Invalid privilege request refused before connection: "+invalid);
        }
    }
}
