using ICSharpCode.AvalonEdit;
using PGMS.Desktop;
using PGMS.PostgreSql;

/// <summary>PGMS-058: Quick Info and Parameter Info against the SSMS 22.11 texts piloted in M1079.</summary>
internal static class QuickInfoChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var catalog=new SqlCompletionCatalog
        {
            Database="shop",SearchPath=["public","pg_catalog"],Schemas=["public","sales"],
            Relations=[new("public","orders",false),new("sales","report",true)],
            Columns=new Dictionary<(string,string),IReadOnlyList<SqlCatalogColumn>>{[("public","orders")]=[new("id","integer",false),new("note","text",true)],[("sales","report")]=[new("total","numeric",true)]},
            Functions=[new("pg_catalog","date_part","text, timestamp without time zone","double precision"),new("pg_catalog","date_part","text, interval","double precision"),new("public","add_tax","amount numeric, rate numeric DEFAULT 0.1","numeric")],
        };
        void Eq(string? actual,string? expected,string name)=>check(actual==expected,actual==expected?name:name+" => ["+actual+"]");
        string sql="select o.note, id from orders o join sales.report r on true where add_tax(o.id, 2) > 0;";
        int At(string word,int skip=0){int i=-1;for(int k=0;k<=skip;k++)i=sql.IndexOf(word,i+1,StringComparison.Ordinal);return i+1;}
        Eq(SqlCompletionEngine.QuickInfo(sql,At("orders"),catalog)?.Text,"table shop.public.orders","Quick Info on a table: kind and database.schema.name, as in SSMS");
        Eq(SqlCompletionEngine.QuickInfo(sql,At("report"),catalog)?.Text,"view shop.sales.report","Quick Info on a qualified view");
        Eq(SqlCompletionEngine.QuickInfo(sql,At("note"),catalog)?.Text,"column note(text, null)","Quick Info on alias.column: column name(type, null)");
        Eq(SqlCompletionEngine.QuickInfo(sql,At(" id")+1,catalog)?.Text,"column id(integer, not null)","Quick Info on a bare column of a table in FROM");
        Eq(SqlCompletionEngine.QuickInfo(sql,At("add_tax"),catalog)?.Text,"function shop.public.add_tax(amount numeric, rate numeric DEFAULT 0.1) RETURNS numeric","Quick Info on a function call");
        Eq(SqlCompletionEngine.QuickInfo(sql,At("sales"),catalog)?.Text,"schema shop.sales","Quick Info on a schema");
        check(SqlCompletionEngine.QuickInfo(sql,At("select"),catalog) is null && SqlCompletionEngine.QuickInfo(sql,At(" o ")+1,catalog) is null && SqlCompletionEngine.QuickInfo(sql,At("join")-2,catalog) is null,"No Quick Info on keywords, aliases or blanks (as in SSMS)");
        check(SqlCompletionEngine.QuickInfo("select 'orders'",9,catalog) is null && SqlCompletionEngine.QuickInfo("select missing",8,catalog) is null,"No Quick Info inside strings or for unknown names");
        // Parameter Info
        var p=SqlCompletionEngine.ParameterInfo(sql,At("add_tax(")+8,catalog);
        check(p is {ArgumentIndex:0,Signatures.Count:1} && p.Signatures[0].Prefix=="ADD_TAX(" && p.Signatures[0].Arguments.SequenceEqual(["amount numeric","rate numeric DEFAULT 0.1"]) && p.Signatures[0].Suffix==") RETURNS numeric","Parameter Info gives NAME(args) RETURNS type with the first argument current");
        p=SqlCompletionEngine.ParameterInfo(sql,sql.IndexOf(" 2)",StringComparison.Ordinal)+1,catalog);check(p is {ArgumentIndex:1},"The argument after a comma is the current one");
        p=SqlCompletionEngine.ParameterInfo("select date_part('x', now())",17,catalog);check(p is {Signatures.Count:2},"Overloads from the first schema of the search path that has the name");
        p=SqlCompletionEngine.ParameterInfo("select add_tax(abs(1), ",23,catalog);check(p is {ArgumentIndex:1} && p.Signatures[0].Prefix=="ADD_TAX(","Nested calls: closed parentheses do not count, the open one wins");
        check(SqlCompletionEngine.ParameterInfo("select add_tax(1);",17,catalog) is null && SqlCompletionEngine.ParameterInfo("select (1, 2)",10,catalog) is null,"No Parameter Info after the call or in plain parentheses");
        check(SqlCompletionEngine.ParameterInfo("select add_tax('a,b', ",22,catalog) is {ArgumentIndex:1},"Commas inside strings are not arguments");

        // In the editor
        var view=new QueryView{TestCatalog=catalog};var editor=(TextEditor)view.FindName("Editor");
        var host=new System.Windows.Window{Content=view,Width=800,Height=600,ShowInTaskbar=false,ShowActivated=false,Left=-10000};
        host.Show();
        try
        {
            editor.Text="select add_tax(1, 2) from orders";editor.CaretOffset=15;editor.Focus();
            await view.ShowParameterInfoAsync();
            check(view.ParameterProvider is { } provider && provider.CurrentText=="ADD_TAX(amount numeric,rate numeric DEFAULT 0.1) RETURNS numeric" && provider.BoldArgument=="amount numeric","Parameter Info opens with the SSMS text and the first argument in bold");
            editor.CaretOffset=18;check(view.ParameterProvider?.BoldArgument=="rate numeric DEFAULT 0.1","Moving the caret to the next argument makes it bold");
            editor.CaretOffset=editor.Text.Length;check(view.OpenParameterWindow is null,"Leaving the parentheses closes Parameter Info");
            editor.CaretOffset=editor.Text.Length-2;await view.ShowQuickInfoAsync();
            check(view.OpenQuickInfoWindow is {Content:System.Windows.Controls.TextBlock{Text:"table shop.public.orders"}},"Quick Info shows the tip of the name at the caret");
            view.OpenQuickInfoWindow?.Close();
            editor.CaretOffset=2;await view.ShowQuickInfoAsync();check(view.OpenQuickInfoWindow is null,"Quick Info on a keyword shows nothing");
            editor.Text="select add_tax";editor.CaretOffset=editor.Text.Length;editor.TextArea.PerformTextInput("(");
            for(int i=0;i<20 && view.OpenParameterWindow is null;i++)await Task.Delay(20);
            check(view.OpenParameterWindow is not null,"Typing ( after a known function opens Parameter Info");
            view.OpenParameterWindow?.Close();
            view.TestCatalog=null;editor.Text="select add_tax(";editor.CaretOffset=15;await view.ShowParameterInfoAsync();
            check(view.OpenParameterWindow is null,"Without a connection nothing opens (the cache needs one)");
        }
        finally{host.Close();await view.DisposeAsync();}
    }
}
