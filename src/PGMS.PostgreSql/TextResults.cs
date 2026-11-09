using System.Globalization;
using System.Text;

namespace PGMS.PostgreSql;

/// <summary>Results to Text / Results to File output, laid out as SSMS 22.11 does (M1044): columns separated by one space, a line of dashes under the
/// names, every column padded except the last, NULL as "NULL", values cut at <see cref="MaxCharacters"/>, "(n rows affected)" per statement and a
/// final "Completion time". Unlike SSMS, a column is as wide as its name or its longest value (up to the limit), not as its declared type (decision
/// of the user): values arrive as text and unbounded text columns are common in PostgreSQL. So a result is written when it ends.</summary>
public sealed class TextResultsWriter(TextWriter output, bool messagesOnly = false, int maxCharacters = TextResultsWriter.DefaultMaxCharacters, TextResultsFormat? format = null) : IDisposable
{
    public const int DefaultMaxCharacters = 256;
    private readonly TextResultsFormat layout = format ?? TextResultsFormat.Default;
    /// <summary>Query Options ▸ Execution ▸ Advanced ▸ SET NOCOUNT: no "(n rows affected)".</summary>
    public bool NoCount { get; init; }
    /// <summary>Query Options ▸ Execution ▸ Advanced ▸ Show completion time.</summary>
    public bool ShowCompletionTime { get; init; } = true;
    public static string CompletionLine(DateTimeOffset at) => "Completion time: " + at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
    public int MaxCharacters { get; } = maxCharacters is >= 1 and <= QueryDisplayBudget.MaxCellCharacters ? maxCharacters : throw new ArgumentOutOfRangeException(nameof(maxCharacters));
    /// <summary>Messages only (Results to File shows only Messages): counts, notices, errors and completion, no result blocks.</summary>
    public bool MessagesOnly { get; } = messagesOnly;
    private TextResultBlock? block;
    private bool written;
    private bool counted;

    /// <summary>Starts a result; with <paramref name="spool"/> its rows go to a temporary file until it ends, so any number of rows can be written.</summary>
    public void StartResult(IReadOnlyList<string> columns, bool spool = false, IReadOnlyList<string>? types = null)
    {
        EndResult();
        if (!MessagesOnly) block = new TextResultBlock(columns, MaxCharacters, spool, layout, types);
    }

    public void AddRows(IEnumerable<object?[]> rows) { if (block is not null) foreach (var row in rows) block.Add(row); }

    public void EndResult()
    {
        if (block is null) return;
        using (var finished = block) { block = null; Separate(); finished.WriteTo(output); }
    }

    /// <summary>Closes a statement; statements that count rows print "(n rows affected)".</summary>
    public void Statement(long? rowsAffected)
    {
        EndResult();
        if (NoCount || rowsAffected is not { } rows) return;
        if (MessagesOnly || written) output.WriteLine();
        output.WriteLine(rows == 1 ? "(1 row affected)" : $"({rows.ToString(CultureInfo.InvariantCulture)} rows affected)");
        written = counted = true;
    }

    /// <summary>A server notice or error text, in the order it arrived.</summary>
    public void Message(string text)
    {
        EndResult();
        if (!MessagesOnly) Separate();
        output.WriteLine(text);
        written = true;
    }

    public void Complete(DateTimeOffset at, bool succeeded = true)
    {
        EndResult();
        if (!written && succeeded) { output.WriteLine("Commands completed successfully."); written = true; }
        if (ShowCompletionTime)
        {
            if (written) output.WriteLine();
            output.WriteLine(CompletionLine(at));
        }
        output.Flush();
    }

    /// <summary>True once some statement printed a row count.</summary>
    public bool Counted => counted;

    private void Separate() { if (written) output.WriteLine(); written = true; }

    public void Dispose() { block?.Dispose(); block = null; }
}

/// <summary>Query Options ▸ Results ▸ Text ▸ Output format. Stored by number: keep the values.</summary>
public enum TextOutputFormat { ColumnAligned = 0, CommaDelimited = 1, TabDelimited = 2, SpaceDelimited = 3, CustomDelimited = 4 }

/// <summary>How a result block is laid out. Delimited values are not quoted or padded; numeric alignment applies only to aligned columns.</summary>
public sealed record TextResultsFormat(TextOutputFormat Output = TextOutputFormat.ColumnAligned, char CustomDelimiter = ';', bool IncludeHeaders = true, bool RightAlignNumeric = false)
{
    public static readonly TextResultsFormat Default = new();
    internal char? Delimiter => Output switch
    {
        TextOutputFormat.CommaDelimited => ',',
        TextOutputFormat.TabDelimited => '\t',
        TextOutputFormat.SpaceDelimited => ' ',
        TextOutputFormat.CustomDelimited => CustomDelimiter,
        _ => null
    };
    /// <summary>Numeric PostgreSQL types by the name the driver reports, with or without a modifier such as numeric(10,2). Arrays are not numeric.</summary>
    public static bool IsNumericType(string type)
    {
        int modifier = type.IndexOf('(');
        return (modifier >= 0 ? type[..modifier] : type).Trim() is "smallint" or "integer" or "bigint" or "numeric" or "real" or "double precision" or "money" or "oid";
    }
}

/// <summary>One result set: widths are known only after the last row, so rows are kept (in memory or in a temporary file) until it ends.</summary>
internal sealed class TextResultBlock : IDisposable
{
    private readonly string[] columns;
    private readonly int maxCharacters;
    private readonly int[] widths;
    private readonly List<string?[]>? memory;
    private readonly string? spoolPath;
    private BinaryWriter? spool;
    private readonly TextResultsFormat layout;
    private readonly bool[] rightAligned;

    internal TextResultBlock(IReadOnlyList<string> columns, int maxCharacters, bool toDisk, TextResultsFormat layout, IReadOnlyList<string>? types)
    {
        this.columns = [.. columns];
        this.maxCharacters = maxCharacters;
        this.layout = layout;
        rightAligned = this.columns.Select((_, i) => layout.RightAlignNumeric && layout.Delimiter is null && types is not null && i < types.Count && TextResultsFormat.IsNumericType(types[i])).ToArray();
        widths = this.columns.Select(c => c.Length).ToArray();
        if (toDisk)
        {
            spoolPath = Path.Combine(Path.GetTempPath(), "pgms-results-" + Guid.NewGuid().ToString("N") + ".tmp");
            spool = new BinaryWriter(new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose), Encoding.UTF8);
        }
        else memory = [];
    }

    internal void Add(object?[] row)
    {
        var cells = new string?[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            object? value = i < row.Length ? row[i] : null;
            string? text = value is null or DBNull ? null : ResultCopyText.DisplayValue(value);
            if (text is not null && text.Length > maxCharacters) text = text[..maxCharacters];
            cells[i] = text;
            widths[i] = Math.Max(widths[i], text?.Length ?? 4);
        }
        if (memory is not null) { memory.Add(cells); return; }
        spool!.Write(cells.Length);
        foreach (var cell in cells) { spool.Write(cell is not null); if (cell is not null) spool.Write(cell); }
    }

    internal void WriteTo(TextWriter output)
    {
        var line = new StringBuilder();
        void Row(IReadOnlyList<string?> cells, bool dashes = false)
        {
            line.Clear();
            for (int i = 0; i < columns.Length; i++)
            {
                if (i > 0) line.Append(layout.Delimiter ?? ' ');
                if (dashes) { line.Append('-', widths[i]); continue; }
                string text = cells[i] ?? "NULL";
                // Delimited output has no padding: the delimiter alone separates the values.
                int padding = layout.Delimiter is null ? Math.Max(0, widths[i] - text.Length) : 0;
                if (rightAligned[i]) line.Append(' ', padding);
                line.Append(text);
                if (!rightAligned[i] && i < columns.Length - 1) line.Append(' ', padding);
            }
            output.WriteLine(line.ToString());
        }
        if (layout.IncludeHeaders)
        {
            Row(columns);
            Row(columns, dashes: true);
        }
        if (memory is not null) { foreach (var cells in memory) Row(cells); return; }
        spool!.Flush();
        var stream = spool.BaseStream; stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var row = new string?[columns.Length];
        while (stream.Position < stream.Length)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++) row[i] = reader.ReadBoolean() ? reader.ReadString() : null;
            Row(row);
        }
    }

    public void Dispose() { spool?.Dispose(); spool = null; }
}
