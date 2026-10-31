using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;

internal static class QueryLoadChecks
{
    public static async Task RunAsync(string connection, Action<bool, string> check)
    {
        var query = new QueryView();
        var window = new Window { Content = query, Width = 900, Height = 650, ShowInTaskbar = false, Opacity = 0 };
        window.Show();
        try
        {
            await query.ConnectAsync(connection);
            var editor = (ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor");
            editor.Text = "SELECT " + string.Join(",", Enumerable.Range(1, 32).Select(i => $"NULL::text AS c{i}")) + " FROM generate_series(1,10000);";
            long before = GC.GetTotalMemory(true), allocations = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            var memory = new LoadMemorySampler();
            try { await query.ExecuteAsync(); } finally { await memory.DisposeAsync(); }
            long elapsedMs = watch.ElapsedMilliseconds;
            var sets = (TabControl)query.FindName("ResultSets");
            var table = ((DataView)((DataGrid)((TabItem)sets.Items[0]).Content).ItemsSource).Table!;
            check(table.Columns.Count == 32 && table.Rows.Count > 0, "Wide NULL load displays retained rows and schema");
            check(table.Rows.Count == PGMS.PostgreSql.QueryDisplayBudget.MaxCells / 32, "Wide load obeys global cell budget with whole rows");
            check(table.Rows.Cast<DataRow>().All(r => r.ItemArray.All(v => v is DBNull)), "Wide load preserves database NULL values");
            var report = new { milestone = BuildInfo.Milestone, scenario = "10000 rows x32 NULL columns", rows = table.Rows.Count,
                memory = new { memory.Baseline, memory.Maximum, memory.Samples, intervalMs = 20 },
                columns = table.Columns.Count, cells = (long)table.Rows.Count * table.Columns.Count, elapsedMs,
                allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocations, retainedHeapDelta = GC.GetTotalMemory(true) - before,
                limitation = "Single synthetic WPF run; post-GC delta is retained heap, sampled maxima can miss peaks and include the whole process; no SLA; excludes server memory." };
            string directory = Path.GetFullPath(Path.Combine("artifacts", "query-load", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "measurement.json"), json);
            Console.WriteLine(json); Console.WriteLine("Query load report: " + directory);
            check(((TextBox)query.FindName("Messages")).Text.Contains("Display limit reached (Cells)") && ((TextBlock)query.FindName("ExportScope")).Text.Contains("display limit reached"),
                "Cell limit is explicit in completion message and export scope");
            sets.SelectedIndex = 0;
            string csv = Path.Combine(directory, "retained.csv");
            await query.ExportSelectedCsvAsync(csv);
            check(File.ReadLines(csv).Count() == table.Rows.Count + 1, "Limited CSV contains exactly retained whole rows and header");
            editor.Text = "SELECT " + string.Join(",", Enumerable.Range(1, 257).Select(i => $"NULL::text AS c{i}"));
            await query.ExecuteAsync();
            check(sets.Items.Count == 0 && !((Button)query.FindName("ExportCsvButton")).IsEnabled && ((TextBox)query.FindName("Messages")).Text.Contains("Display limit reached (Columns)"),
                "Oversize schema creates no partial grid and explains column limit");
            editor.Text = "SELECT 'after metadata limit'";
            await query.ExecuteAsync();
            check(sets.Items.Count == 1 && !((TextBlock)query.FindName("ExportScope")).Text.Contains("display limit reached"),
                "Query after metadata limit restores normal grid and export scope");

            editor.Text = "SELECT repeat('á', 1024) AS payload FROM generate_series(1,9000);";
            var textMemory = new LoadMemorySampler();
            watch.Restart();
            try { await query.ExecuteAsync(); } finally { await textMemory.DisposeAsync(); }
            var textTable = ((DataView)((DataGrid)((TabItem)sets.Items[0]).Content).ItemsSource).Table!;
            check(textTable.Rows.Count == PGMS.PostgreSql.QueryDisplayBudget.MaxTextCharacters / 1024,
                "Text load retains exactly the UTF-16 text budget in whole rows");
            check(textTable.Rows.Cast<DataRow>().All(row => (string)row[0] == new string('á', 1024)),
                "Text load preserves complete Unicode values without cell truncation");
            check(((TextBox)query.FindName("Messages")).Text.Contains("Display limit reached (Text)"),
                "Text load explains the text limit after draining remaining server rows");
            string textCsv = Path.Combine(directory, "text-retained.csv");
            await query.ExportSelectedCsvAsync(textCsv);
            check(File.ReadLines(textCsv).Count() == textTable.Rows.Count + 1,
                "Text load CSV contains only retained rows and header");
            var textReport = new { milestone = BuildInfo.Milestone, scenario = "9000 rows x1024 Unicode characters",
                rows = textTable.Rows.Count, executionAndExportMs = watch.ElapsedMilliseconds,
                memory = new { textMemory.Baseline, textMemory.Maximum, textMemory.Samples, intervalMs = 20 },
                limitation = "Whole-process sampled maxima can miss peaks; includes previous query allocations; excludes server memory; no SLA." };
            string textJson = JsonSerializer.Serialize(textReport, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "text-measurement.json"), textJson);
            Console.WriteLine(textJson);

        }
        finally { query.Cancel(); await query.DisposeAsync(); window.Close(); }
    }
}
