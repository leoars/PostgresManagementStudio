using System.Data;
using System.Globalization;
using System.Text.Json;

namespace PGMS.PostgreSql;

public static class JsonResults
{
    // Ordered arrays preserve duplicate column labels. PostgreSQL display values
    // remain strings, avoiding loss of numeric precision or type guesses.
    public static async Task WriteAsync(DataTable table,Stream stream,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(table);ArgumentNullException.ThrowIfNull(stream);token.ThrowIfCancellationRequested();
        await using var writer=new Utf8JsonWriter(stream);
        writer.WriteStartObject();writer.WriteStartArray("columns");
        foreach(DataColumn column in table.Columns)writer.WriteStringValue(column.Caption);
        writer.WriteEndArray();writer.WriteStartArray("rows");int count=0;
        foreach(DataRow row in table.Rows)
        {
            token.ThrowIfCancellationRequested();writer.WriteStartArray();
            foreach(object? value in row.ItemArray)
                if(value is null or DBNull)writer.WriteNullValue();else writer.WriteStringValue(Convert.ToString(value,CultureInfo.InvariantCulture));
            writer.WriteEndArray();if(++count%128==0)await writer.FlushAsync(token).ConfigureAwait(false);
        }
        writer.WriteEndArray();writer.WriteEndObject();await writer.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task SaveAsync(DataTable table,string path,bool overwrite=false,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(table);token.ThrowIfCancellationRequested();string destination=Path.GetFullPath(path);
        if(!overwrite && File.Exists(destination))throw new IOException("The destination already exists.");
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
            {await WriteAsync(table,stream,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);}
            token.ThrowIfCancellationRequested();File.Move(temporary,destination,overwrite);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
