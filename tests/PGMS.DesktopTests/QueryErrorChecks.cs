using Npgsql;
using PGMS.Desktop;

internal static class QueryErrorChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var error=new PostgresException("duplicate 😀","ERROR","ERROR","23505",detail:"secret detail",hint:"Choose another key",position:9,schemaName:"public",tableName:"fixture",columnName:"id",dataTypeName:"integer",constraintName:"fixture_pk");
        string text=QueryErrorFormatter.Format(error);
        check(text.Contains("SQLSTATE: 23505") && text.Contains("Severity: ERROR") && text.Contains("duplicate 😀"),"Error formatter preserves SQLSTATE severity and Unicode");
        check(text.Contains("Hint: Choose another key") && text.Contains("Constraint: fixture_pk") && text.Contains("Column: id") && text.Contains("Server character position: 9"),"Error formatter exposes server hints and object identity");
        check(!text.Contains("secret detail"),"Error formatter does not expose optional server detail");
        text=QueryErrorFormatter.Format(new PostgresException(new string('x',4095)+"😀tail","ERROR","ERROR","P0001"));
        check(text.Contains("[truncated]") && !text.Any(char.IsSurrogate) && text.Length<4300,"Oversize error text is bounded without splitting surrogate pair");
    }
}
