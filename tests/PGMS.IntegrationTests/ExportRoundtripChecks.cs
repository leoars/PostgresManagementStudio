using System.Data;
using Npgsql;
using PGMS.PostgreSql;

internal static class ExportRoundtripChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        using var table=new DataTable();table.Columns.Add("a");table.Columns.Add("b");
        table.Rows.Add(DBNull.Value,"");table.Rows.Add("\\N","á😀;\t\"\r\n");table.Rows.Add("123456789012345678901234567890.123456789","NaN");
        await using var c=new NpgsqlConnection(connection);await c.OpenAsync();
        await using(var create=new NpgsqlCommand("CREATE TEMP TABLE export_roundtrip (id integer GENERATED ALWAYS AS IDENTITY,a text,b text)",c)){await create.ExecuteNonQueryAsync();}
        string root=Path.Combine("artifacts","export-roundtrip",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        foreach(char delimiter in new[]{',',';','\t'})foreach(bool headers in new[]{false,true})
        {
            await using(var clear=new NpgsqlCommand("TRUNCATE export_roundtrip RESTART IDENTITY",c)){await clear.ExecuteNonQueryAsync();}
            string path=Path.Combine(root,$"{(int)delimiter}-{headers}.csv");await CsvResults.SaveAsync(table,path,new CsvExportOptions(delimiter,headers,true));
            // File.ReadAllText consumes the optional UTF8 BOM before COPY receives text.
            string csv=await File.ReadAllTextAsync(path);string separator=delimiter=='\t'?"E'\\t'":"'"+delimiter+"'";
            await using(var importer=await c.BeginTextImportAsync($"COPY export_roundtrip(a,b) FROM STDIN (FORMAT CSV, DELIMITER {separator}, HEADER {(headers?"true":"false")}, NULL E'\\\\N')")){await importer.WriteAsync(csv);}
            await using var read=new NpgsqlCommand("SELECT a,b FROM export_roundtrip ORDER BY id",c);await using var reader=await read.ExecuteReaderAsync();int row=0;bool equal=true;
            while(await reader.ReadAsync()){equal&=row<table.Rows.Count && Equals(reader.GetValue(0),table.Rows[row][0]) && Equals(reader.GetValue(1),table.Rows[row][1]);row++;}
            check(equal && row==table.Rows.Count,$"PostgreSQL COPY roundtrip delimiter {(int)delimiter}, headers {headers}, BOM decoded");
        }
        string directoryTarget=Path.Combine(root,"directory.csv");Directory.CreateDirectory(directoryTarget);await File.WriteAllTextAsync(Path.Combine(directoryTarget,"keep.txt"),"keep");
        foreach(bool json in new[]{false,true})
        {
            bool failed=false;try{if(json)await JsonResults.SaveAsync(table,directoryTarget,true);else await CsvResults.SaveAsync(table,directoryTarget,true);}catch(IOException){failed=true;}catch(UnauthorizedAccessException){failed=true;}
            check(failed && await File.ReadAllTextAsync(Path.Combine(directoryTarget,"keep.txt"))=="keep" && !Directory.EnumerateFiles(root,"*.partial").Any(),"Failed final rename preserves directory and removes partial: "+(json?"JSON":"CSV"));
        }
        using var canceled=new CancellationTokenSource();using var stream=new CancelingStream(canceled);using var large=table.Clone();for(int i=0;i<300;i++)large.Rows.Add(i.ToString(),"payload");bool stopped=false;
        try{await JsonResults.WriteAsync(large,stream,canceled.Token);}catch(OperationCanceledException){stopped=true;}
        check(stopped && stream.Length>0,"JSON cancellation after first flushed batch stops remaining rows");
    }
    private sealed class CancelingStream(CancellationTokenSource cancellation):MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)
        {var result=base.WriteAsync(buffer,token);cancellation.Cancel();return result;}
    }
}
