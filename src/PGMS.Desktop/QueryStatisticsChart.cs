using System.Globalization;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

/// <summary>One bar of the Query Statistics top-N chart. Built from the loaded snapshot; the chart never queries the server.</summary>
public sealed record QueryStatisticsChartBar(QueryStatisticsEntry Entry, int Rank, double Value, double Fraction, string ValueLabel, string Caption, string AccessibleName, string Detail);

/// <summary>Top-N bars in server order for the metric of the applied ordering, as in the bar chart of SSMS "Top Resource Consuming Queries".</summary>
public static class QueryStatisticsChart
{
    public const int CaptionLength = 60;
    private const int DetailLength = 400;
    /// <summary>Metric name, value and label of an entry for an ordering. Labels reuse the grid formats so chart and grid read the same.</summary>
    public static (string Metric, double Value, string Label) Measure(QueryStatisticsEntry entry, QueryStatisticsOrder order) => order switch
    {
        QueryStatisticsOrder.TotalExecTime => ("Total ms", entry.TotalExecMs, entry.TotalExecLabel),
        QueryStatisticsOrder.MeanExecTime => ("Mean ms", entry.MeanExecMs, entry.MeanExecLabel),
        QueryStatisticsOrder.MaxExecTime => ("Max ms", entry.MaxExecMs, entry.MaxExecLabel),
        QueryStatisticsOrder.Calls => ("Calls", entry.Calls, entry.Calls.ToString(CultureInfo.InvariantCulture)),
        QueryStatisticsOrder.Rows => ("Rows", entry.Rows, entry.Rows.ToString(CultureInfo.InvariantCulture)),
        QueryStatisticsOrder.StddevExecTime => ("Std dev ms", entry.StddevExecMs, entry.StddevExecLabel),
        QueryStatisticsOrder.SharedBlocks => ("Shared blocks", entry.SharedBlocks, entry.SharedBlocks.ToString(CultureInfo.InvariantCulture)),
        QueryStatisticsOrder.SharedBlocksRead => ("Blocks read", entry.SharedBlocksRead, entry.SharedBlocksRead.ToString(CultureInfo.InvariantCulture)),
        QueryStatisticsOrder.TempBlocks => ("Temp blocks", entry.TempBlocks, entry.TempBlocks.ToString(CultureInfo.InvariantCulture)),
        QueryStatisticsOrder.IoTime => ("I/O ms", entry.IoTimeMs, entry.IoTimeLabel),
        QueryStatisticsOrder.WalBytes => ("WAL bytes", entry.WalBytes, entry.WalBytes.ToString(CultureInfo.InvariantCulture)),
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };
    /// <summary>First <paramref name="count"/> entries in the given (server) order. Bars scale to the largest value; all-zero values give empty bars.</summary>
    public static IReadOnlyList<QueryStatisticsChartBar> Build(IEnumerable<QueryStatisticsEntry> serverOrder, QueryStatisticsOrder order, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var top = serverOrder.Take(count).Select(e => (Entry: e, Measure: Measure(e, order))).ToList();
        double max = top.Count == 0 ? 0 : top.Max(t => Math.Max(0, t.Measure.Value));
        return top.Select((t, i) =>
        {
            double fraction = max > 0 && double.IsFinite(max) ? Math.Clamp(t.Measure.Value / max, 0, 1) : 0;
            string sql = OneLine(t.Entry.Query);
            string caption = $"#{i + 1}  " + Shorten(sql, CaptionLength);
            string id = t.Entry.QueryId is { } q ? q.ToString(CultureInfo.InvariantCulture) : "hidden";
            string name = $"#{i + 1} {t.Measure.Metric} {t.Measure.Label}, database {t.Entry.Database}, user {t.Entry.User}: {Shorten(sql, CaptionLength)}";
            string detail = $"Query ID {id}\nDatabase {t.Entry.Database}, user {t.Entry.User}\n{t.Measure.Metric}: {t.Measure.Label}\n{Shorten(sql, DetailLength)}";
            return new QueryStatisticsChartBar(t.Entry, i + 1, t.Measure.Value, fraction, t.Measure.Label, caption, name, detail);
        }).ToArray();
    }
    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    // Cuts at a UTF-16 boundary without splitting a surrogate pair.
    private static string Shorten(string text, int length)
    {
        if (text.Length <= length) return text;
        int cut = length - 1; if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }
}
