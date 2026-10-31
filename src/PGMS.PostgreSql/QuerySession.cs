using System.Data;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record QueryResult(IReadOnlyList<DataTable> Tables, int RecordsAffected, bool Truncated, QueryLimitReason LimitReasons = QueryLimitReason.None);

/// <summary>Owns one physical connection for the lifetime of an editor tab.</summary>
public sealed partial class QuerySession : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly SemaphoreSlim execution = new(1, 1);
    public string Database => connection.Database;
    public bool IsConnected => connection.State == ConnectionState.Open;

    public QuerySession(string connectionString) => connection = new(connectionString);

    public async Task OpenAsync(CancellationToken token = default)
    {await connection.OpenAsync(token).ConfigureAwait(false);sessionIdentity=Guid.NewGuid();}

    // Materialized callers share the same display budgets and draining semantics as the UI.
    public Task<QueryResult> ExecuteAsync(string sql, CancellationToken token = default)
        => ExecuteAsync(sql,new QueryExecutionOptions(),token);

    public async Task<QueryResult> ExecuteAsync(string sql, QueryExecutionOptions options, CancellationToken token = default)
    {
        var tables = new List<DataTable>();
        var result = await StreamAsync(sql, (batch, _) =>
        {
            if (batch.Columns is not null)
            {
                var table = new DataTable();
                for (int i = 0; i < batch.Columns.Count; i++)
                    table.Columns.Add(new DataColumn($"c{i}", typeof(object)) { Caption = batch.Columns[i] });
                tables.Add(table);
            }
            foreach (var row in batch.Rows) tables[batch.ResultIndex].Rows.Add(row);
            return ValueTask.CompletedTask;
        }, options, token).ConfigureAwait(false);
        return new(tables, result.RecordsAffected, result.Truncated, result.LimitReasons);
    }

    public async ValueTask DisposeAsync()
    {
        await execution.WaitAsync();
        try { await connection.DisposeAsync(); }
        finally { execution.Release(); }
    }
}
