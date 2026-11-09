using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PGMS.PostgreSql;

/// <summary>What one execution measured on the client (Query ▸ Include Client Statistics). PostgreSQL's protocol has no packet counters exposed by the
/// driver, so the network rows are the SQL sent and the characters of the values received by this client.</summary>
public sealed record ClientTrial(DateTime At, int DmlStatements, long DmlRows, int SelectStatements, long SelectRows, long BytesSent, long CharactersReceived, double WaitMilliseconds, double ClientMilliseconds, double TotalMilliseconds);

/// <summary>Measures one execution from outside the streaming loop: wraps the consumer and counts what passes through it.</summary>
public sealed class ClientTrialMeter(string sql)
{
    private readonly Stopwatch total = Stopwatch.StartNew();
    private long clientTicks;
    private double? firstReply;
    private int dmlStatements, selectStatements;
    private long dmlRows, selectRows, characters;

    public Func<QueryBatch, CancellationToken, ValueTask> Wrap(Func<QueryBatch, CancellationToken, ValueTask> consume) => async (batch, token) =>
    {
        firstReply ??= total.Elapsed.TotalMilliseconds;
        Observe(batch);
        long start = Stopwatch.GetTimestamp();
        await consume(batch, token).ConfigureAwait(false);
        clientTicks += Stopwatch.GetTimestamp() - start;
    };

    private void Observe(QueryBatch batch)
    {
        foreach (var row in batch.Rows)
            foreach (var value in row)
                if (value is string text) characters += text.Length;
        if (!batch.StatementEnd) return;
        long rows = batch.RowsAffected ?? 0;
        switch (batch.StatementType)
        {
            case "Select": selectStatements++; selectRows += rows; break;
            case "Insert" or "Update" or "Delete" or "Merge": dmlStatements++; dmlRows += rows; break;
        }
    }

    public ClientTrial Finish(DateTime at) => new(at, dmlStatements, dmlRows, selectStatements, selectRows, Encoding.UTF8.GetByteCount(sql), characters,
        firstReply ?? total.Elapsed.TotalMilliseconds, clientTicks * 1000.0 / Stopwatch.Frequency, total.Elapsed.TotalMilliseconds);
}

/// <summary>The trials of one query tab, shown newest first with the average in the last column, as SSMS lays them out. An arrow compares a trial with the one before it.</summary>
public sealed class ClientStatisticsLog
{
    public const int MaxTrials = 10;
    private readonly List<(int Number, ClientTrial Trial)> trials = [];
    private int count;

    public int Count => trials.Count;
    public void Add(ClientTrial trial)
    {
        trials.Add((++count, trial));
        if (trials.Count > MaxTrials) trials.RemoveAt(0);
    }
    public void Reset() { trials.Clear(); count = 0; }

    private static readonly (string Label, Func<ClientTrial, double>? Value)[] Rows =
    [
        ("Query Profile Statistics", null),
        ("  Number of INSERT, DELETE and UPDATE statements", t => t.DmlStatements),
        ("  Rows affected by INSERT, DELETE, or UPDATE statements", t => t.DmlRows),
        ("  Number of SELECT statements", t => t.SelectStatements),
        ("  Rows returned by SELECT statements", t => t.SelectRows),
        ("Network Statistics", null),
        ("  Bytes sent (SQL text)", t => t.BytesSent),
        ("  Characters received in values", t => t.CharactersReceived),
        ("Time Statistics (ms)", null),
        ("  Client processing time", t => Math.Round(t.ClientMilliseconds)),
        ("  Total execution time", t => Math.Round(t.TotalMilliseconds)),
        ("  Wait time on server replies", t => Math.Round(t.WaitMilliseconds)),
    ];

    /// <summary>Headers and one text array per row, in display order: label, trials newest first, average.</summary>
    public (IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows) Table()
    {
        var culture = CultureInfo.InvariantCulture;
        var newest = trials.AsEnumerable().Reverse().ToArray();
        var headers = new List<string> { "" };
        headers.AddRange(newest.Select(t => "Trial " + t.Number.ToString(culture)));
        headers.Add("Average");
        var rows = new List<string[]>();
        var time = new List<string> { "Client Execution Time" };
        time.AddRange(newest.Select(t => t.Trial.At.ToString("HH:mm:ss", culture)));
        time.Add("");
        rows.Add([.. time]);
        foreach (var (label, value) in Rows)
        {
            var cells = new List<string> { label };
            for (int i = 0; i < newest.Length; i++)
            {
                if (value is null) { cells.Add(""); continue; }
                double current = value(newest[i].Trial);
                string arrow = i + 1 >= newest.Length ? "" : Math.Sign(current.CompareTo(value(newest[i + 1].Trial))) switch { > 0 => " ↑", < 0 => " ↓", _ => " →" };
                cells.Add(current.ToString("0", culture) + arrow);
            }
            cells.Add(value is null || newest.Length == 0 ? "" : newest.Average(t => value(t.Trial)).ToString("0.0", culture));
            rows.Add([.. cells]);
        }
        return (headers, rows);
    }
}
