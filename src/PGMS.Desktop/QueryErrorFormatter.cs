using System.Text;
using Npgsql;

namespace PGMS.Desktop;

internal static class QueryErrorFormatter
{
    internal static string Format(PostgresException error)
    {
        var text=new StringBuilder();
        Add("SQLSTATE",error.SqlState);Add("Severity",error.InvariantSeverity);Add("Message",error.MessageText);
        if(error.Position>0)Add("Server character position",error.Position.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("Hint",error.Hint);Add("Schema",error.SchemaName);Add("Table",error.TableName);
        Add("Column",error.ColumnName);Add("Data type",error.DataTypeName);Add("Constraint",error.ConstraintName);
        return text.ToString().TrimEnd();
        void Add(string label,string? value)
        {
            if(string.IsNullOrEmpty(value))return;
            const int limit=4096;
            if(value.Length>limit){int length=char.IsHighSurrogate(value[limit-1])?limit-1:limit;value=value[..length]+" [truncated]";}
            text.Append(label).Append(": ").AppendLine(value);
        }
    }
}
