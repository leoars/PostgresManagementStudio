using System.Collections.ObjectModel;
using System.Text;

namespace PGMS.PostgreSql;

public sealed record BackupTableSelection(string Schema,string Table);

public sealed class BackupSelection
{
    public ReadOnlyCollection<string> Schemas { get; }
    public ReadOnlyCollection<BackupTableSelection> Tables { get; } = Array.AsReadOnly(Array.Empty<BackupTableSelection>());
    public string FilterText { get; }

    public BackupSelection(IEnumerable<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        string[] names=schemas.Take(257).ToArray();
        if(names.Length is <1 or >256)throw new ArgumentException("Select 1 to 256 schemas.");
        var lines=new List<string>();
        foreach(string name in names)
        {
            lines.Add("include schema "+QuoteFilterIdentifier(name));
        }
        Schemas=Array.AsReadOnly(names);
        FilterText=string.Join("\n",lines)+"\n";
    }

    private BackupSelection(BackupTableSelection[] tables,string filterText)
    {Schemas=Array.AsReadOnly(Array.Empty<string>());Tables=Array.AsReadOnly(tables);FilterText=filterText;}

    public static BackupSelection ForTables(IEnumerable<BackupTableSelection> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var selected=tables.Take(257).ToArray();
        if(selected.Length is <1 or >256)throw new ArgumentException("Select 1 to 256 tables.");
        var lines=new List<string>();
        foreach(var table in selected)
        {
            ArgumentNullException.ThrowIfNull(table);
            lines.Add("include table "+QuoteFilterIdentifier(table.Schema)+"."+QuoteFilterIdentifier(table.Table));
        }
        return new(selected,string.Join("\n",lines)+"\n");
    }

    private static string QuoteFilterIdentifier(string name)
    {
        if(name is null || name.Any(c=>c is '\r' or '\n'))throw new ArgumentException("Object filters cannot contain line breaks.");
        _=new UTF8Encoding(false,true).GetByteCount(name);
        // filter.c unescapes backslashes before psql's pattern parser.
        // Quoting the entire identifier prevents wildcard expansion.
        return SqlIdentifier.Quote(name).Replace("\\","\\\\",StringComparison.Ordinal);
    }
}
