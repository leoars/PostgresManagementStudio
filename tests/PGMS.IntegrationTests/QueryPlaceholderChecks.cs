using Npgsql;
using PGMS.PostgreSql;

internal static class QueryPlaceholderChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        foreach(var (sql,offset,number,highest,count) in new (string,int,int,int,int)[]
        {
            ("SELECT $1",7,1,1,1),
            ("SELECT 'é', $2::int + $10",12,2,10,2),
            ("SELECT 1; SELECT $3",17,3,3,1),
            ("WITH a AS (SELECT $1::int) SELECT * FROM a",18,1,1,1),
            ("EXPLAIN SELECT $1::int",15,1,1,1),
            ("PREPARE p AS SELECT 1; SELECT $1",30,1,1,1),
        })
        {
            var found=QueryPlaceholders.Find(sql);
            check(found is not null && found.Offset==offset && found.Number==number && found.Highest==highest && found.Count==count,$"Placeholder found at its UTF-16 offset with number, highest and count: {sql}");
        }
        foreach(var sql in new[]
        {
            "SELECT '$1', 'it''s $2'","SELECT E'\\'$1'","SELECT \"a$1\", \"x\"\"$2\"","SELECT a$1 FROM t","SELECT $$ $1 $$, $q$ $2 $q$, $_é$ $3 $_é$",
            "SELECT 1 -- $1","SELECT 1 /* outer /* $1 */ still $2 */","DO $$BEGIN PERFORM $1; END$$",
            "PREPARE p(int) AS SELECT $1 + 1","prepare p as select $1","CREATE FUNCTION f(int) RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT $1 + 1",
            "CREATE OR REPLACE PROCEDURE p(int) LANGUAGE sql BEGIN ATOMIC SELECT $1","SELECT 1","SELECT $ FROM t","SELECT 'unterminated $1",
        })
            check(QueryPlaceholders.Find(sql) is null,$"No unbound placeholder reported: {sql}");
        check(QueryPlaceholders.Find("SELECT $99999999999")?.Number==int.MaxValue,"Oversized placeholder numbers saturate instead of overflowing");

        await using var session=new QuerySession(connection);await session.OpenAsync();
        await session.ExecuteAsync("CREATE TEMP TABLE placeholder_fixture(id int)");
        async Task<bool> Refused(Func<Task> action){try{await action();return false;}catch(UnboundParametersException){return true;}}
        check(await Refused(()=>session.ExecuteAsync("SELECT $1")) && session.IsConnected,"Execution with a bare $1 is refused before sending and the session stays connected");
        check(await Refused(()=>session.ExecuteAsync("INSERT INTO placeholder_fixture VALUES(1); SELECT $1")),"A script with a later $n is refused as a whole");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM placeholder_fixture")).Tables[0].Rows[0][0]=="0","Earlier statements of the refused script were not sent");
        check(await Refused(()=>session.ExplainAsync("SELECT $1::int")) && await Refused(()=>session.AnalyzeAsync("INSERT INTO placeholder_fixture VALUES($1)")) && session.IsConnected,"Estimated and analyzed plans with unbound $n are refused and the session stays open");
        check((string)(await session.ExecuteAsync("SELECT count(*) FROM placeholder_fixture")).Tables[0].Rows[0][0]=="0","Refused analyzed plan inserted no row");
        var generic=await session.ExplainGenericAsync("SELECT * FROM placeholder_fixture WHERE id = $1");
        check(generic.Generic && generic.Json.Contains("$1",StringComparison.Ordinal),"Generic plan still accepts $n after refusals on the same session");
        await session.ExecuteAsync("PREPARE placeholder_add(int) AS SELECT $1 + 1");
        check((string)(await session.ExecuteAsync("EXECUTE placeholder_add(41)")).Tables[0].Rows[0][0]=="42","PREPARE keeps its own $1 and still runs");
        check((string)(await session.ExecuteAsync("SELECT '$1' || \"x$1\" FROM (SELECT 'v' AS \"x$1\") s -- $1")).Tables[0].Rows[0][0]=="$1v","Dollar signs inside literals, identifiers and comments still execute");
        try{await session.ExecuteAsync("SELECT $1");}catch(UnboundParametersException ex){check(ex.Message.Contains("Nothing was sent",StringComparison.Ordinal) && ex.Parameters.Offset==7,"Refusal explains that nothing was sent and locates the placeholder");}
    }
}
