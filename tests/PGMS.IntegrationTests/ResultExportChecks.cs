using System.Data;
using System.Text;
using PGMS.PostgreSql;

internal static class ResultExportChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        using var table=new DataTable();table.Columns.Add("a");table.Columns.Add("b");table.Rows.Add(DBNull.Value,"á😀;\t\"\r\n");table.Rows.Add("","\\N");
        foreach(char delimiter in new[]{',',';','\t'})
        {
            using var writer=new StringWriter();await CsvResults.WriteAsync(table,writer,new CsvExportOptions(delimiter,false));
            check(writer.ToString()=="\\N"+delimiter+"\"á😀;\t\"\"\r\n\"\r\n\"\""+delimiter+"\"\\N\"\r\n","Delimited export preserves NULL empty Unicode quotes and multiline: "+(int)delimiter);
        }
        string root=Path.Combine("artifacts","export-checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        foreach(bool bom in new[]{false,true})
        {
            string path=Path.Combine(root,bom+".csv");await CsvResults.SaveAsync(table,path,new CsvExportOptions(';',true,bom));byte[] bytes=await File.ReadAllBytesAsync(path);
            check(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())==bom,"CSV UTF8 BOM follows explicit option: "+bom);
            check((await File.ReadAllTextAsync(path)).StartsWith("\"a\";\"b\"\r\n"),"CSV header follows delimiter option");
        }
        string invalid=Path.Combine(root,"invalid.csv");bool rejected=false;try{await CsvResults.SaveAsync(table,invalid,new CsvExportOptions('\n'));}catch(ArgumentException){rejected=true;}
        check(rejected && !File.Exists(invalid) && !Directory.EnumerateFiles(root,"*.partial").Any(),"Invalid delimiter rejected before file creation");
        using var empty=table.Clone();using var noHeaders=new StringWriter();await CsvResults.WriteAsync(empty,noHeaders,new CsvExportOptions(IncludeHeaders:false));check(noHeaders.ToString()=="","Empty headerless export is empty");
        await JsonResultChecks.RunAsync(check);
        using var legacy=new StringWriter();await CsvResults.WriteAsync(table,legacy);check(legacy.ToString().StartsWith("\"a\",\"b\"\r\n"),"Legacy CSV defaults preserve comma and headers");
    }
}
