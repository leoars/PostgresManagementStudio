using System.Data;
using System.Globalization;
using System.Text;

namespace PGMS.PostgreSql;

public sealed record CsvExportOptions(char Delimiter=',',bool IncludeHeaders=true,bool Utf8Bom=false)
{
    public CsvExportOptions Validate()
    {
        if(Delimiter is not (',' or ';' or '\t'))throw new ArgumentException("Choose comma, semicolon or tab as the CSV delimiter.");
        return this;
    }
}

public static class CsvResults
{
    // PostgreSQL COPY CSV convention with explicit NULL '\N': quote every non-null
    // value, including empty strings and literal \N, so all three remain distinct.
    public static async Task WriteAsync(DataTable table, TextWriter writer, CancellationToken token = default)
        => await WriteAsync(table,writer,new CsvExportOptions(),token).ConfigureAwait(false);

    public static async Task WriteAsync(DataTable table,TextWriter writer,CsvExportOptions options,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(table);ArgumentNullException.ThrowIfNull(writer);ArgumentNullException.ThrowIfNull(options);options.Validate();
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        async Task LineAsync(IEnumerable<string> cells)
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteAsync((string.Join(options.Delimiter, cells) + "\r\n").AsMemory(), token).ConfigureAwait(false);
        }
        if(options.IncludeHeaders)await LineAsync(table.Columns.Cast<DataColumn>().Select(c => Quote(c.Caption))).ConfigureAwait(false);
        foreach (DataRow row in table.Rows)
            await LineAsync(row.ItemArray.Select(value => value is null or DBNull ? "\\N" : Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))).ConfigureAwait(false);
    }

    public static async Task SaveAsync(DataTable table, string path, bool overwrite = false, CancellationToken token = default)
        => await SaveAsync(table,path,new CsvExportOptions(),overwrite,token).ConfigureAwait(false);

    public static async Task SaveAsync(DataTable table,string path,CsvExportOptions options,bool overwrite=false,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(table);ArgumentNullException.ThrowIfNull(options);options.Validate();
        string destination = Path.GetFullPath(path);
        if (!overwrite && File.Exists(destination)) throw new IOException("The destination already exists.");
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(options.Utf8Bom)))
            {
                await WriteAsync(table, writer, options, token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
