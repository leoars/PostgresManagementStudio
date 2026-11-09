using System.Text;

namespace PGMS.PostgreSql;

/// <summary>Divides one result into pages of the grid. The first page stays in memory with the caller; later rows go to a temporary file, the way SSMS 22.11
/// stores its grid results (docs/MARCO-M1053.md), so a large result costs one page of memory. The file disappears when this object is disposed or the process ends.
/// One thread adds rows while another may read pages.</summary>
public sealed class ResultPages : IDisposable
{
    /// <summary>A page closes early when its text reaches this size (UTF-16 characters), so a page of very long values cannot exhaust memory.</summary>
    public const long MaxPageCharacters = 256L * 1024 * 1024;

    private sealed class Page(long offset, long firstRow) { public long Offset = offset; public long FirstRow = firstRow; public int Rows; public long Characters; }

    private readonly object gate = new();
    private readonly int pageRows, columns;
    private readonly long maxCharacters;
    private readonly List<Page> filePages = [];
    private int firstPageRows;
    private long firstPageCharacters, totalRows;
    private bool firstPageClosed, disposed;
    private string? path;
    private FileStream? stream;
    private BinaryWriter? writer;

    public ResultPages(int pageRows, int columns, long maxPageCharacters = MaxPageCharacters)
    {
        maxCharacters = maxPageCharacters;
        ArgumentOutOfRangeException.ThrowIfLessThan(pageRows, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(columns);
        this.pageRows = pageRows; this.columns = columns;
    }

    public long TotalRows { get { lock (gate) return totalRows; } }
    public int PageCount { get { lock (gate) return (firstPageRows > 0 || filePages.Count == 0 ? 1 : 0) + filePages.Count; } }
    /// <summary>Zero-based position in the result of the first row of a page.</summary>
    public long FirstRowOf(int page) { lock (gate) return page == 0 ? 0 : filePages[page - 1].FirstRow; }
    public int RowsOf(int page) { lock (gate) return page == 0 ? firstPageRows : filePages[page - 1].Rows; }

    /// <summary>True when the row belongs to the first page and the caller keeps it; false when it went to the file.</summary>
    public bool Add(object[] row)
    {
        long characters = 0;
        foreach (object value in row) if (value is string text) characters += text.Length;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            totalRows++;
            if (!firstPageClosed)
            {
                if (firstPageRows == 0 || firstPageRows < pageRows && firstPageCharacters + characters <= maxCharacters)
                {
                    firstPageRows++; firstPageCharacters += characters;
                    return true;
                }
                firstPageClosed = true;
            }
            Page? page = filePages.Count > 0 ? filePages[^1] : null;
            if (page is null || page.Rows >= pageRows || page.Characters + characters > maxCharacters && page.Rows > 0)
            {
                OpenFile();
                filePages.Add(page = new(stream!.Position, totalRows - 1));
            }
            foreach (object value in row)
            {
                if (value is string text) { writer!.Write(true); writer.Write(text); }
                else writer!.Write(false);
            }
            page.Rows++; page.Characters += characters;
            return false;
        }
    }

    /// <summary>Rows of a page after the first, read from the file through a handle of its own.</summary>
    public IReadOnlyList<object[]> ReadPage(int page, CancellationToken token = default)
    {
        string file; long offset; int count;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(page, filePages.Count);
            writer!.Flush(); stream!.Flush();
            file = path!; offset = filePages[page - 1].Offset; count = filePages[page - 1].Rows;
        }
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        input.Position = offset;
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var rows = new object[count][];
        for (int r = 0; r < count; r++)
        {
            if (r % 256 == 0) token.ThrowIfCancellationRequested();
            var cells = new object[columns];
            for (int c = 0; c < columns; c++) cells[c] = reader.ReadBoolean() ? reader.ReadString() : DBNull.Value;
            rows[r] = cells;
        }
        return rows;
    }

    private void OpenFile()
    {
        if (stream is not null) return;
        path = Path.GetTempFileName();
        // DeleteOnClose: Windows removes the file even if the process is killed.
        stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary);
        writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            writer?.Dispose();
            stream?.Dispose();
        }
    }
}
