using System.Text.Json;
using PGMS.PostgreSql;

internal static class QueryPlanTreeChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var plan=new QueryPlan("""[{"Plan":{"Node Type":"Aggregate","Total Cost":14.25,"Plan Rows":1,"Plans":[{"Node Type":"Seq Scan","Relation Name":"t_😀","Plan Rows":20,"Filter":"(id > 10)","Unknown Extension Field":[1,2]}]}}]""",false);
        var root=QueryPlanTree.Parse(plan);
        check(root.Label.Contains("cost 14.25") && root.Children.Single().Label.Contains("t_😀"),"Plan tree preserves hierarchy, Unicode and invariant planner costs");
        check(root.Children[0].Properties["Unknown Extension Field"]=="[1,2]" && root.Children[0].Properties["Filter"]=="(id > 10)","Plan properties preserve filters and unknown server fields");
        foreach(string invalid in new[]{"[]","[{}]","[1]","{}","[{\"Plan\":{\"Node Type\":\"Result\",\"Plans\":1}}]"})
        {
            bool rejected=false;try{QueryPlanTree.Parse(new(invalid,false));}catch(Exception ex)when(ex is InvalidDataException or JsonException){rejected=true;}
            check(rejected,"Unsupported JSON plan is refused: "+invalid);
        }
        string many="[{\"Plan\":{\"Node Type\":\"Append\",\"Plans\":["+string.Join(',',Enumerable.Repeat("{\"Node Type\":\"Result\"}",4096))+"]}}]";
        bool limited=false;try{QueryPlanTree.Parse(new(many,false));}catch(InvalidDataException){limited=true;}
        check(limited,"Oversize plan tree is refused whole after node budget");
        string deep="[{\"Plan\":"+string.Concat(Enumerable.Repeat("{\"Node Type\":\"Limit\",\"Plans\":[",65))+"{\"Node Type\":\"Result\"}"+string.Concat(Enumerable.Repeat("]}",65))+"}]";
        limited=false;try{QueryPlanTree.Parse(new(deep,false));}catch(InvalidDataException){limited=true;}
        check(limited,"Excessive plan hierarchy is refused before unbounded recursion");
        string longType=new string('x',499)+"😀"+new string('y',3000);
        string longJson=JsonSerializer.Serialize(new[]{new{Plan=new Dictionary<string,object>{{"Node Type",longType},{"Relation Name",new string('r',3000)},{"Plan Rows",1}}}});
        var bounded=QueryPlanTree.Parse(new(longJson,false));
        check(bounded.Label.Length<=512 && bounded.Label.EndsWith(" [truncated]"),"Aggregate plan label is bounded even when several server fields are long");
        check(bounded.Properties["Node Type"]==longType && bounded.Properties["Relation Name"].Length==3000,"Bounded label preserves original values in node properties");
        check(new System.Text.UTF8Encoding(false,true).GetByteCount(bounded.Label)>0,"Plan label clipping preserves Unicode surrogate pair boundary");
        using var cancel=new CancellationTokenSource();cancel.Cancel();bool canceled=false;try{QueryPlanTree.Parse(plan,cancel.Token);}catch(OperationCanceledException){canceled=true;}
        check(canceled,"Plan tree preparation respects cancellation");
    }
}
