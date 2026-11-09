using System.Globalization;
using System.Text;

namespace PGMS.PostgreSql;

// Quoted TSV: NULL is unquoted \N; empty/literal \N are quoted.
// Output describes displayed values, never reexecutes SQL.
public static class ResultCopyText
{
    public const int MaximumCharacters = 4 * 1024 * 1024;
    // Same representation in the grid and clipboard; arrays are display text, not a COPY input syntax.
    public static string DisplayValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "\\x" + Convert.ToHexString(bytes),
        Array array => "{" + string.Join(",", array.Cast<object?>().Select(DisplayValue)) + "}",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>Query Options ▸ Results ▸ Grid ▸ Retain CR/LF on copy or save, when off: each line break becomes one space.</summary>
    public static string FlattenLineBreaks(string text) => text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    public static string Format(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows, bool includeHeaders, int maximumCharacters = MaximumCharacters, bool retainLineBreaks = true)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        if (maximumCharacters < 0 || maximumCharacters > MaximumCharacters) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var text = new StringBuilder();
        void Append(char value)
        {
            if (text.Length >= maximumCharacters) throw new InvalidOperationException("Copy exceeds the 4 Mi character limit. Select fewer cells or export CSV.");
            text.Append(value);
        }
        void AppendText(string value) { foreach (char c in value) Append(c); }
        void Line(IReadOnlyList<object?> cells)
        {
            if (cells.Count != headers.Count) throw new ArgumentException("Copied row width differs from headers.");
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) Append('\t');
                if (cells[i] is null or DBNull) AppendText("\\N");
                else
                {
                    Append('"');
                    foreach (char c in retainLineBreaks ? DisplayValue(cells[i]) : FlattenLineBreaks(DisplayValue(cells[i])))
                    { if (c == '"') Append('"'); Append(c); }
                    Append('"');
                }
            }
            AppendText("\r\n");
        }
        if (includeHeaders) Line(headers.Cast<object?>().ToArray());
        foreach (var row in rows) Line(row);
        return text.ToString();
    }
}
