using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>PGMS-055: Format SQL against the SSMS 22.11 layout piloted in M1077, adapted to PostgreSQL.</summary>
internal static class SqlFormatChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        void Eq(string actual,string expected,string name)=>check(actual==expected,actual==expected?name:name+" => ["+actual.Replace("\n","\\n")+"]");
        string F(string sql)=>SqlFormatter.Format(sql,"\n");
        Eq(F("select a.id,b.name,count(*) as total from t1 a inner join t2 b on a.id=b.id left join t3 c on c.x = a.x where a.x=1 and b.y>2 or c.z is null group by a.id,b.name having count(*)>1 order by total desc;"),
            "SELECT   a.id,\n         b.name,\n         count(*) AS total\nFROM     t1 a\n         INNER JOIN\n         t2 b\n         ON a.id = b.id\n         LEFT JOIN\n         t3 c\n         ON c.x = a.x\nWHERE    a.x = 1\n         AND b.y > 2\n         OR c.z IS NULL\nGROUP BY a.id,\n         b.name\nHAVING   count(*) > 1\nORDER BY total DESC;",
            "SELECT is laid out like SSMS: keywords upper, functions kept, bodies aligned after the longest clause, joins and AND/OR on their own lines");
        Eq(F("update t1 set a=1,b=2 where c=3;"),"UPDATE t1\nSET    a = 1,\n       b = 2\nWHERE  c = 3;","UPDATE aligns SET and WHERE as in SSMS");
        Eq(F("delete from t1 where a in (select x from t2 where y = 'abc');"),"DELETE FROM t1\nWHERE  a IN (SELECT x\n             FROM   t2\n             WHERE  y = 'abc');","DELETE keeps FROM (PostgreSQL needs it) and aligns the subquery inside its parenthesis");
        Eq(F("insert into t1(a,b) values(1,'x'),(2,'y');"),"INSERT INTO t1 (a, b)\nVALUES (1, 'x'),\n       (2, 'y');","INSERT puts each VALUES row on its own line");
        Eq(F("select case when a=1 then 'x' else 'y' end as k, (select max(z) from t4) as m from t1 union all select 1,2;"),
            "SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END AS k,\n       (SELECT max(z)\n        FROM   t4) AS m\nFROM   t1\nUNION ALL\nSELECT 1,\n       2;","CASE stays inline, scalar subquery aligned, UNION ALL on its own line");
        Eq(F("with c as (select 1 as x) select * from c;"),"WITH   c AS (SELECT 1 AS x)\nSELECT *\nFROM   c;","WITH keeps each CTE with its query");
        Eq(F("create table t9 (id int not null primary key, nome varchar(20) null, check (id > 0));"),"CREATE TABLE t9 (\n    id INT NOT NULL PRIMARY KEY,\n    nome VARCHAR (20) NULL,\n    CHECK (id > 0)\n);","CREATE TABLE puts each column on its own indented line (space between type and parameters, the SSMS default since M1143)");
        Eq(F("select 1;\n-- comentario\nselect 2; -- fim\n"),"SELECT 1;\n\n-- comentario\nSELECT 2; -- fim\n","Statements are separated by a blank line, comments kept in place, final line break kept");
        Eq(F("select x::int, -1, a-1, 'it''s', E'a\\'b', $$ body $$, $tag$ x $tag$, \"Mixed Name\".col, arr[1], t.* from t where b between 1 and 2 and c is not distinct from d"),
            "SELECT x::INT,\n       -1,\n       a - 1,\n       'it''s',\n       E'a\\'b',\n       $$ body $$,\n       $tag$ x $tag$,\n       \"Mixed Name\".col,\n       arr[1],\n       t.*\nFROM   t\nWHERE  b BETWEEN 1 AND 2\n       AND c IS NOT DISTINCT FROM d","PostgreSQL literals, casts, quoted names and BETWEEN … AND stay intact; type names in uppercase");
        Eq(F("select a from t where x = 1 -- note\n and y = 2"),"SELECT a\nFROM   t\nWHERE  x = 1 -- note\n       AND y = 2","A line comment inside a clause ends its line");
        Eq(F("create function f() returns int language sql as $$ select   1 $$;"),"create function f() returns int language sql as $$ select   1 $$;","Statements the formatter does not lay out keep their text");
        Eq(F("create view v as select a,b from t"),"CREATE VIEW v AS\nSELECT a,\n       b\nFROM   t","CREATE VIEW … AS lays out its query");
        Eq(F("select coalesce(a,0), left(b, 2) from t order by 1 limit 10 offset 5"),"SELECT   coalesce(a, 0),\n         left(b, 2)\nFROM     t\nORDER BY 1\nLIMIT    10\nOFFSET   5","Keyword functions keep their case; LIMIT and OFFSET are clauses");
        Eq(F("select count(*) filter (where a > 0) over (partition by b order by c) from t"),"SELECT count(*) FILTER (WHERE a > 0) OVER (PARTITION BY b ORDER BY c)\nFROM   t","Clauses inside parentheses stay inline");
        Eq(F("insert into t (a) values (1) on conflict (a) do update set b = excluded.b returning *"),"INSERT      INTO t (a)\nVALUES      (1)\nON CONFLICT (a) DO UPDATE\nSET         b = excluded.b\nRETURNING   *","ON CONFLICT … DO UPDATE SET and RETURNING are clauses");
        string sample="select 1;";check(F(F(sample))==F(sample),"Formatting twice gives the same text");
        foreach(var (bad,reason) in new[]{("select 'abc","string"),("select /* x","comment"),("select (1","parenthesis"),("select 1)","parenthesis"),("select $$ x","dollar"),("select \"a","quoted")})
        {
            bool refused=false;try{F(bad);}catch(FormatException ex){refused=ex.Message.Contains(reason=="dollar"?"dollar":reason);}
            check(refused,$"Unreadable SQL is refused ({reason})");
        }
        check(SqlFormatter.Tokenize("a=-1").Select(t=>t.Text).SequenceEqual(["a","=","-","1"]),"Operators ending in - are split as PostgreSQL does");
        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");
        try
        {
            editor.Text="select 1 from t;\r\nselect 2 from u;\r\n";editor.Document.UndoStack.ClearAll();editor.Select(0,0);
            string status=SqlTextCommands.FormatSql(editor);
            check(editor.Text=="SELECT 1\r\nFROM   t;\r\n\r\nSELECT 2\r\nFROM   u;\r\n" && status=="Formatted the document.","Without selection the whole document is formatted with its CRLF line breaks");
            editor.Undo();check(editor.Text=="select 1 from t;\r\nselect 2 from u;\r\n","Format SQL is one Undo step");
            editor.Select(18,16);SqlTextCommands.FormatSql(editor);check(editor.Text=="select 1 from t;\r\nSELECT 2\r\nFROM   u;\r\n" && editor.SelectedText=="SELECT 2\r\nFROM   u;","With a selection only the selection is formatted and stays selected");
            editor.Text="select 'x";editor.Document.UndoStack.ClearAll();editor.Select(0,0);status=SqlTextCommands.FormatSql(editor);
            check(editor.Text=="select 'x" && !editor.CanUndo && status.StartsWith("SQL formatting could not be completed") && status.Contains("string is not closed"),"Unreadable SQL changes nothing and says why");
            editor.Text="SELECT 1";check(SqlTextCommands.FormatSql(editor)=="The SQL is already formatted.","Formatted SQL is left alone");
            editor.IsReadOnly=true;bool refused=false;try{SqlTextCommands.FormatSql(editor);}catch(InvalidOperationException){refused=true;}editor.IsReadOnly=false;check(refused,"Read-only editor is refused");
            editor.Text="select 1 from t";editor.Focus();
            view.ProcessCommentKey(System.Windows.Input.Key.K,System.Windows.Input.ModifierKeys.Control,1000);
            bool handled=view.ProcessCommentKey(System.Windows.Input.Key.Q,System.Windows.Input.ModifierKeys.Control,1500);
            check(!editor.IsKeyboardFocusWithin || handled && editor.Text.StartsWith("SELECT 1"),"Ctrl+K, Ctrl+Q formats in the editor");
        }
        finally{await view.DisposeAsync();}
    }
}
