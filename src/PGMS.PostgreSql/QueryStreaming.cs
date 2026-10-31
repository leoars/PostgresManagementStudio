using System.Diagnostics;
using System.Data;
using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

// Each batch transfers ownership to the consumer; the producer never mutates it again.
// Columns is present only when a result starts (including empty results).
[Flags]
public enum QueryLimitReason { None = 0, Rows = 1, ResultSets = 2, Columns = 4, Cells = 8, Text = 16 }

public sealed record QueryBatch(int ResultIndex, IReadOnlyList<string>? Columns, IReadOnlyList<object[]> Rows, bool DisplayLimited = false, QueryLimitReason LimitReasons = QueryLimitReason.None);
public sealed record QueryCompletion(int DisplayedRows, int RecordsAffected, bool Truncated, QueryLimitReason LimitReasons = QueryLimitReason.None);

public sealed partial class QuerySession
{
    /// <summary>Awaits each consumer before reading more: no unbounded UI notification queue.</summary>
    public Task<QueryCompletion> StreamAsync(string sql, Func<QueryBatch, CancellationToken, ValueTask> consume, CancellationToken token = default)
        => StreamAsync(sql,consume,new QueryExecutionOptions(),token);

    public async Task<QueryCompletion> StreamAsync(string sql, Func<QueryBatch, CancellationToken, ValueTask> consume, QueryExecutionOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentNullException.ThrowIfNull(options);
        await execution.WaitAsync(token).ConfigureAwait(false);
        void Notice(object sender,NpgsqlNoticeEventArgs args) => options.Notices?.Add(args.Notice.InvariantSeverity,args.Notice.SqlState,args.Notice.MessageText);
        if(options.Notices is not null)connection.Notice+=Notice;
        try
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds, AllResultTypesAreUnknown = true };
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
            try
            {
                int resultIndex = 0, displayed = 0, retainedCharacters = 0, retainedColumns = 0, retainedCells = 0;
                bool cellsLimited = false;
                bool metadataLimited = false;
                QueryLimitReason limitReasons = QueryLimitReason.None;
                bool textLimited = false;
                var scratch = new char[4096];
                bool limited = false;
                do
                {
                    if (reader.FieldCount == 0) continue;
                    bool retain = !metadataLimited && resultIndex < QueryDisplayBudget.MaxResultSets &&
                        reader.FieldCount <= QueryDisplayBudget.MaxColumnsPerResult &&
                        retainedColumns + reader.FieldCount <= QueryDisplayBudget.MaxTotalColumns;
                    if (retain) retainedColumns += reader.FieldCount;
                    else if (!metadataLimited)
                    {
                        limitReasons |= resultIndex >= QueryDisplayBudget.MaxResultSets ? QueryLimitReason.ResultSets : QueryLimitReason.Columns;
                        metadataLimited = true;
                    }
                    if (retain)
                        await consume(new(resultIndex, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray(), []), token).ConfigureAwait(false);
                    else if (!limited)
                    {
                        limited = true;
                        await consume(new(resultIndex, null, [], true, limitReasons), token).ConfigureAwait(false);
                    }
                    var rows = new List<object[]>(128);
                    int batchCharacters = 0;
                    var elapsed = Stopwatch.StartNew();
                    bool first = true;
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (retain && retainedCells + reader.FieldCount > QueryDisplayBudget.MaxCells) { cellsLimited = true; limitReasons |= QueryLimitReason.Cells; }
                        if (retain && displayed >= QueryDisplayBudget.MaxRows) limitReasons |= QueryLimitReason.Rows;
                        if (!retain || textLimited || cellsLimited || displayed >= QueryDisplayBudget.MaxRows)
                        {
                            if (!limited) { limited = true; await consume(new(resultIndex, null, [], true, limitReasons), token).ConfigureAwait(false); }
                            continue;
                        }
                        if (rows.Count > 0 && (rows.Count + 1) * reader.FieldCount > QueryDisplayBudget.MaxBatchCells)
                        {
                            await consume(new(resultIndex, null, rows.ToArray()), token).ConfigureAwait(false);
                            rows.Clear(); batchCharacters = 0; first = false; elapsed.Restart();
                        }
                        var values = new object[reader.FieldCount];
                        int rowCharacters = 0;
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (await reader.IsDBNullAsync(i, token).ConfigureAwait(false)) { values[i] = DBNull.Value; continue; }
                            int allowance = Math.Min(QueryDisplayBudget.MaxCellCharacters,
                                Math.Min(QueryDisplayBudget.MaxRowCharacters - rowCharacters, QueryDisplayBudget.MaxTextCharacters - retainedCharacters - rowCharacters));
                            using var text = await reader.GetTextReaderAsync(i, token).ConfigureAwait(false);
                            var value = await ReadBoundedAsync(text, allowance, scratch, token).ConfigureAwait(false);
                            if (value is null) { textLimited = true; limitReasons |= QueryLimitReason.Text; break; }
                            values[i] = value; rowCharacters += value.Length;
                        }
                        if (textLimited)
                        {
                            if (rows.Count > 0) { await consume(new(resultIndex, null, rows.ToArray()), token).ConfigureAwait(false); rows.Clear(); }
                            limited = true;
                            await consume(new(resultIndex, null, [], true, limitReasons), token).ConfigureAwait(false);
                            continue;
                        }
                        rows.Add(values); displayed++; retainedCells += values.Length; retainedCharacters += rowCharacters; batchCharacters += rowCharacters;
                        if (first || rows.Count >= 128 || batchCharacters >= QueryDisplayBudget.MaxBatchCharacters || elapsed.ElapsedMilliseconds >= 100)
                        {
                            await consume(new(resultIndex, null, rows.ToArray()), token).ConfigureAwait(false);
                            rows.Clear(); batchCharacters = 0; first = false; elapsed.Restart();
                        }
                    }
                    if (rows.Count > 0) await consume(new(resultIndex, null, rows.ToArray()), token).ConfigureAwait(false);
                    resultIndex++;
                } while (await reader.NextResultAsync(token).ConfigureAwait(false));
                return new(displayed, reader.RecordsAffected, limited, limitReasons);
            }
            catch
            {
                // Cancellation can occur in the consumer, outside Npgsql's ReadAsync.
                // Cancel the server command before DisposeAsync tries to drain its reader.
                try { await Task.Run(command.Cancel).ConfigureAwait(false); }
                catch { /* Preserve the original failure; disposal still releases the reader. */ }
                throw;
            }
        }
        finally { if(options.Notices is not null)connection.Notice-=Notice; execution.Release(); }
    }

    private static async Task<string?> ReadBoundedAsync(TextReader reader, int limit, char[] scratch, CancellationToken token)
    {
        var value = new StringBuilder();
        while (true)
        {
            int count = await reader.ReadAsync(scratch.AsMemory(0, Math.Min(scratch.Length, limit - value.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) return value.ToString();
            if (value.Length + count > limit) return null;
            value.Append(scratch, 0, count);
        }
    }
}
