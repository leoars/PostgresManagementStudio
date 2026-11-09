using System.Diagnostics;
using System.Data;
using System.Text;
using Npgsql;

namespace PGMS.PostgreSql;

// Each batch transfers ownership to the consumer; the producer never mutates it again.
// Columns is present only when a result starts (including empty results).
[Flags]
public enum QueryLimitReason { None = 0, Rows = 1, ResultSets = 2, Columns = 4, Cells = 8, Text = 16 }

/// <summary>StatementEnd batches (only with <see cref="QueryExecutionOptions.ReportStatements"/>) carry no rows: they close one statement of the
/// SQL, with RowsAffected for statements that count rows (SELECT, INSERT, UPDATE, DELETE, MERGE, COPY, CREATE TABLE AS, FETCH, MOVE) and null otherwise.</summary>
public sealed record QueryBatch(int ResultIndex, IReadOnlyList<string>? Columns, IReadOnlyList<object[]> Rows, bool DisplayLimited = false, QueryLimitReason LimitReasons = QueryLimitReason.None, bool StatementEnd = false, long? RowsAffected = null, IReadOnlyList<string>? ColumnTypes = null, string? StatementType = null);
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
        QueryPlaceholders.ThrowIfUnbound(sql);
        await execution.WaitAsync(token).ConfigureAwait(false);
        void Notice(object sender,NpgsqlNoticeEventArgs args) { if(!TakePlanNotice(args.Notice,options.ActualPlans))options.Notices?.Add(args.Notice.InvariantSeverity,args.Notice.SqlState,args.Notice.MessageText); }
        connection.Notice+=Notice;
        try
        {
            if(options.ActualPlans is { } capture)await ArmPlanCaptureAsync(capture,token).ConfigureAwait(false);
            else await DisarmPlanCaptureAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds, AllResultTypesAreUnknown = true };
            // Npgsql's own token did not stop a later statement of the script while the reader waited for it, so the server is asked directly.
            await using var serverCancel = token.Register(() => _ = Task.Run(() => { try { command.Cancel(); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ObjectDisposedException) { } }));
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
                int? cellLimit = options.TextCellLimit;
                int statement = 0;
                // Npgsql moves past statements without a result set (UPDATE, DO, CREATE) inside NextResult, so completed statements are reported in
                // order whenever the reader stops: a statement is complete once its type is known (Unknown until its CommandComplete arrives).
                async ValueTask ReportCompleted()
                {
                    if (!options.ReportStatements) return;
                    // Per-statement counts of a multi-statement command; DbBatch would need the SQL split by PGMS instead of Npgsql.
#pragma warning disable CS0618
                    var statements = reader.Statements;
#pragma warning restore CS0618
                    while (statement < statements.Count && statements[statement] is { StatementType: not Npgsql.StatementType.Unknown } done)
                    {
                        long? affected = done.StatementType is Npgsql.StatementType.Select or Npgsql.StatementType.Insert or Npgsql.StatementType.Update or Npgsql.StatementType.Delete or Npgsql.StatementType.Merge or Npgsql.StatementType.Copy or Npgsql.StatementType.CreateTableAs or Npgsql.StatementType.Fetch or Npgsql.StatementType.Move
                            ? (long)done.Rows : null;
                        statement++;
                        await consume(new(Math.Max(0, resultIndex - 1), null, [], StatementEnd: true, RowsAffected: affected, StatementType: done.StatementType.ToString()), token).ConfigureAwait(false);
                    }
                }
                do
                {
                    await ReportCompleted().ConfigureAwait(false);
                    if (reader.FieldCount == 0) continue;
                    if (options.DiscardResults)
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) if (displayed < int.MaxValue) displayed++;
                        resultIndex++;
                        await ReportCompleted().ConfigureAwait(false);
                        continue;
                    }
                    // Text/File output (cellLimit) keeps every row and only a short prefix of each value, so the display budget does not apply.
                    bool retain = cellLimit is not null || !metadataLimited && resultIndex < QueryDisplayBudget.MaxResultSets &&
                        reader.FieldCount <= QueryDisplayBudget.MaxColumnsPerResult &&
                        retainedColumns + reader.FieldCount <= QueryDisplayBudget.MaxTotalColumns;
                    if (retain) retainedColumns += reader.FieldCount;
                    else if (!metadataLimited)
                    {
                        limitReasons |= resultIndex >= QueryDisplayBudget.MaxResultSets ? QueryLimitReason.ResultSets : QueryLimitReason.Columns;
                        metadataLimited = true;
                    }
                    if (retain)
                        await consume(new(resultIndex, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray(), [], ColumnTypes: Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToArray()), token).ConfigureAwait(false);
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
                        if (cellLimit is { } prefix)
                        {
                            var cells = new object[reader.FieldCount];
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                if (await reader.IsDBNullAsync(i).ConfigureAwait(false)) { cells[i] = DBNull.Value; continue; }
                                using var text = await reader.GetTextReaderAsync(i).ConfigureAwait(false);
                                cells[i] = await ReadPrefixAsync(text, prefix, scratch).ConfigureAwait(false);
                            }
                            rows.Add(cells); displayed++;
                            if (first || rows.Count >= 128 || elapsed.ElapsedMilliseconds >= 100)
                            {
                                await consume(new(resultIndex, null, rows.ToArray()), token).ConfigureAwait(false);
                                rows.Clear(); first = false; elapsed.Restart();
                            }
                            continue;
                        }
                        if (retain && !options.PagedDisplay && retainedCells + reader.FieldCount > QueryDisplayBudget.MaxCells) { cellsLimited = true; limitReasons |= QueryLimitReason.Cells; }
                        if (retain && !options.PagedDisplay && displayed >= QueryDisplayBudget.MaxRows) limitReasons |= QueryLimitReason.Rows;
                        if (!retain || textLimited || cellsLimited || !options.PagedDisplay && displayed >= QueryDisplayBudget.MaxRows)
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
                            // A cancellation in the middle of a row left the connection broken: it is honored between rows, by ReadAsync.
                            if (await reader.IsDBNullAsync(i).ConfigureAwait(false)) { values[i] = DBNull.Value; continue; }
                            int allowance = Math.Min(QueryDisplayBudget.MaxCellCharacters,
                                Math.Min(QueryDisplayBudget.MaxRowCharacters - rowCharacters, options.PagedDisplay ? int.MaxValue : QueryDisplayBudget.MaxTextCharacters - retainedCharacters - rowCharacters));
                            using var text = await reader.GetTextReaderAsync(i).ConfigureAwait(false);
                            var value = await ReadBoundedAsync(text, allowance, scratch).ConfigureAwait(false);
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
                        rows.Add(values); displayed++; batchCharacters += rowCharacters;
                        if (!options.PagedDisplay) { retainedCells += values.Length; retainedCharacters += rowCharacters; }
                        if (first || rows.Count >= 128 || batchCharacters >= QueryDisplayBudget.MaxBatchCharacters || elapsed.ElapsedMilliseconds >= 100)
                        {
                            await consume(new(resultIndex, null, rows.ToArray()), token).ConfigureAwait(false);
                            rows.Clear(); batchCharacters = 0; first = false; elapsed.Restart();
                        }
                    }
                    if (rows.Count > 0) await consume(new(resultIndex, null, rows.ToArray()), token).ConfigureAwait(false);
                    resultIndex++;
                    await ReportCompleted().ConfigureAwait(false);
                } while (await reader.NextResultAsync(token).ConfigureAwait(false));
                await ReportCompleted().ConfigureAwait(false);
                return new(displayed, reader.RecordsAffected, limited, limitReasons);
            }
            catch (PostgresException canceled) when (canceled.SqlState == PostgresErrorCodes.QueryCanceled && token.IsCancellationRequested)
            {
                throw new OperationCanceledException("Query was cancelled.", canceled, token);
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
        finally { if(options.ActualPlans is not null)await DisarmPlanCaptureAsync().ConfigureAwait(false); connection.Notice-=Notice; execution.Release(); }
    }

    /// <summary>First characters of a value; the rest is skipped (sequential access moves past it on the next column).</summary>
    private static async Task<string> ReadPrefixAsync(TextReader reader, int limit, char[] scratch)
    {
        var value = new StringBuilder();
        while (value.Length < limit)
        {
            int count = await reader.ReadAsync(scratch.AsMemory(0, Math.Min(scratch.Length, limit - value.Length))).ConfigureAwait(false);
            if (count == 0) break;
            value.Append(scratch, 0, count);
        }
        return value.ToString();
    }

    private static async Task<string?> ReadBoundedAsync(TextReader reader, int limit, char[] scratch, CancellationToken token = default)
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
