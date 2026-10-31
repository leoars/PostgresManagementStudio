using System.Data;
using System.Text.Json;
using PGMS.PostgreSql;

internal static class JsonResultChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        using var table=new DataTable();table.Columns.Add("a").Caption="same";table.Columns.Add("b").Caption="same";
        table.Rows.Add(DBNull.Value,"");table.Rows.Add("\\N","á😀\t\r\n\"");table.Rows.Add("123456789012345678901234567890.123456789","true");
        string root=Path.Combine("artifacts","json-export",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string path=Path.Combine(root,"result.json");await JsonResults.SaveAsync(table,path);
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path));var json=document.RootElement;
        check(json.GetProperty("columns").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"same","same"}),"JSON ordered column array retains duplicate captions");
        var rows=json.GetProperty("rows");check(rows[0][0].ValueKind==JsonValueKind.Null && rows[0][1].GetString()=="" && rows[1][0].GetString()=="\\N","JSON distinguishes null empty and literal backslash N");
        check(rows[1][1].GetString()=="á😀\t\r\n\"","JSON escapes Unicode control characters and quotes losslessly");
        check(rows[2][0].GetString()=="123456789012345678901234567890.123456789" && rows[2][1].ValueKind==JsonValueKind.String,"JSON keeps PostgreSQL text without guessing numeric or boolean types");
        string before=await File.ReadAllTextAsync(path);bool refused=false;try{await JsonResults.SaveAsync(table,path);}catch(IOException){refused=true;}
        check(refused && await File.ReadAllTextAsync(path)==before,"JSON default refuses overwrite");
        using var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;try{await JsonResults.SaveAsync(table,path,true,canceled.Token);}catch(OperationCanceledException){stopped=true;}
        check(stopped && await File.ReadAllTextAsync(path)==before && !Directory.EnumerateFiles(root,"*.partial").Any(),"Canceled JSON export preserves destination without leftovers");
        using var empty=table.Clone();await JsonResults.SaveAsync(empty,path,true);using var emptyJson=JsonDocument.Parse(await File.ReadAllTextAsync(path));check(emptyJson.RootElement.GetProperty("rows").GetArrayLength()==0 && emptyJson.RootElement.GetProperty("columns").GetArrayLength()==2,"JSON empty result retains schema labels");
    }
}
