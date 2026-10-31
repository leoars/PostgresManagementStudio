using System.Text.Json;
using PGMS.PostgreSql;

internal static class QueryPlanFileChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        string directory=Path.GetFullPath(Path.Combine("artifacts","plan-files",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"plan.json");var plan=new QueryPlan("""[{"Plan":{"Node Type":"Seq Scan","Relation Name":"á_😀","Plan Rows":1}}]""",false);
        await QueryPlanFiles.SaveAsync(plan,path);byte[] bytes=await File.ReadAllBytesAsync(path);
        check(await File.ReadAllTextAsync(path)==plan.Json && bytes[0]=='[',"Plan export preserves original JSON and Unicode as UTF8 without BOM");
        using var parsed=JsonDocument.Parse(bytes);check(parsed.RootElement[0].GetProperty("Plan").GetProperty("Plan Rows").GetInt32()==1,"Export remains native numeric PostgreSQL JSON, not result-grid strings");
        bool refused=false;try{await QueryPlanFiles.SaveAsync(plan,path);}catch(IOException){refused=true;}
        check(refused && await File.ReadAllTextAsync(path)==plan.Json,"Plan export requires explicit overwrite and preserves destination");
        using var cancel=new CancellationTokenSource();cancel.Cancel();bool canceled=false;try{await QueryPlanFiles.SaveAsync(plan,path,true,cancel.Token);}catch(OperationCanceledException){canceled=true;}
        check(canceled && await File.ReadAllTextAsync(path)==plan.Json && !Directory.EnumerateFiles(directory,"*.partial").Any(),"Canceled plan export leaves prior file and no partial");
        string target=Path.Combine(directory,"directory-target");Directory.CreateDirectory(target);bool failed=false;try{await QueryPlanFiles.SaveAsync(plan,target,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){failed=true;}
        check(failed && Directory.Exists(target) && !Directory.EnumerateFiles(directory,"*.partial").Any(),"Failed plan rename cleans staging and preserves destination directory");
    }
}
