using System.Data;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PGMS.Desktop;

// Desempenho da grade de resultados sem servidor (M1132): mede no mesmo processo, várias vezes, a primeira passada de layout de uma grade
// com 10.000 × 20 linhas e o custo de rolar, para comparar mudanças com menos ruído que o gate M948. Não é critério de aceite.
internal static class GridBenchChecks
{
    private static DataTable Table(int rows)
    {
        var table = new DataTable();
        for (int i = 0; i < 20; i++) table.Columns.Add(new DataColumn($"c{i}", typeof(object)) { Caption = i == 0 ? "id" : $"c{i:00}" });
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        table.BeginLoadData();
        for (long g = 1; g <= rows; g++)
            table.Rows.Add(g, (int)(g % 1000), "cliente " + g, g.ToString("x32"), g * 1.25m, start.AddMinutes(g), g % 3 == 0, "ref " + g, (int)(g % 97),
                "São Paulo " + g % 50, DateOnly.FromDateTime(start).AddDays((int)(g % 365)), g * 13, (g * 11).ToString("x8"), g / 7.0m, "pedido " + g,
                (int)(g % 10), (g * 3).ToString("x8"), start.AddSeconds(g), g % 5 == 0 ? DBNull.Value : "obs " + g, new Guid((int)g, 7, 9, new byte[8]));
        table.EndLoadData();
        return table;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void DumpPeers(DataGrid grid)
    {
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(grid);
        void Walk(System.Windows.Automation.Peers.AutomationPeer p, int depth, int max)
        {
            string value = p.GetPattern(System.Windows.Automation.Peers.PatternInterface.Value) is System.Windows.Automation.Provider.IValueProvider v ? " value=" + v.Value : "";
            Console.WriteLine($"GRIDBENCH peer {new string(' ', depth * 2)}{p.GetType().Name} type={p.GetAutomationControlType()} name='{p.GetName()}'{value}");
            if (depth >= max) return;
            var children = p.GetChildren();
            if (children is null) return;
            foreach (var c in children.Take(depth == 0 ? 2 : 4)) Walk(c, depth + 1, max);
            Console.WriteLine($"GRIDBENCH peer {new string(' ', depth * 2)}  ({children.Count} children)");
        }
        Walk(peer, 0, 3);
    }

    private static void DumpAutomationEvents(Window window)
    {
        var lmType = typeof(UIElement).Assembly.GetType("System.Windows.ContextLayoutManager")!;
        var lm = lmType.GetMethod("From", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(null, [window.Dispatcher])!;
        var list = lmType.GetProperty("AutomationEvents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lm)!;
        var copy = list.GetType().GetMethod("CopyToArray", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(list, null) as Array;
        Console.WriteLine($"GRIDBENCH automation-events count={copy?.Length}");
        if (copy is null) return;
        foreach (var group in copy.Cast<object>().Select(e => e.GetType().GetProperty("Target")?.GetValue(e)?.GetType().Name ?? e.GetType().Name).GroupBy(n => n)) Console.WriteLine($"GRIDBENCH   {group.Key} x{group.Count()}");
    }

    private static double Median(List<double> values) { var s = values.OrderBy(v => v).ToList(); return s[s.Count / 2]; }

    /// <summary>Saves the results grid as PNG in Cool Breeze and Dark, with the current cell, a selected cell and NULLs, to compare drawings between versions.</summary>
    public static async Task CaptureAsync(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        foreach (var theme in new[] { ThemeChoice.CoolBreeze, ThemeChoice.Dark })
        {
            Theme.Apply(theme);
            var query = new QueryView(Guid.NewGuid(), _ => { });
            var window = new Window { Content = query, Width = 2600, Height = 600, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            window.Show();
            var sets = (TabControl)query.FindName("ResultSets");
            query.AddResultGridForTests(Table(40));
            sets.SelectedIndex = 0;
            var grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            window.UpdateLayout();
            grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[0]);
            grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[2], grid.Columns[1]));
            grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[4], grid.Columns[18]));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (int dpi in new[] { 96, 144 }) {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(grid.ActualWidth * dpi / 96), (int)(grid.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(grid);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"grid-{theme}-{dpi}.png"))) encoder.Save(file);
            }
            window.Close();
        }
        Theme.Apply(ThemeChoice.CoolBreeze);
    }

    public static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("PGMS_BENCH_CAPTURE") is { Length: > 0 } capture) { await CaptureAsync(capture); return; }
        var query = new QueryView(Guid.NewGuid(), _ => { });
        var window = new Window { Content = query, Width = 1280, Height = 800, ShowInTaskbar = false, Opacity = 0 };
        window.Show();
        var sets = (TabControl)query.FindName("ResultSets");
        var first = new List<double>(); var scroll = new List<double>();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        for (int round = 0; round < 7; round++)
        {
            sets.Items.Clear();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var table = Table(10000);
            var watch = Stopwatch.StartNew();
            query.AddResultGridForTests(table);
            sets.SelectedIndex = 0;
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            first.Add(watch.Elapsed.TotalMilliseconds);
            var grid = (DataGrid)((TabItem)sets.Items[0]).Content;
            if (round == 0 && Environment.GetEnvironmentVariable("PGMS_BENCH_PEERS") == "1") { DumpPeers(grid); DumpAutomationEvents(window); }
            var scroller = FindChild<ScrollViewer>(grid)!;
            for (int offset = 500; offset <= (Environment.GetEnvironmentVariable("PGMS_BENCH_FIRSTONLY") == "1" ? 0 : 10000); offset += 500)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                watch.Restart();
                scroller.ScrollToVerticalOffset(offset);
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (round > 0) scroll.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        Console.WriteLine($"GRIDBENCH first-layout median={Median(first.Skip(1).ToList()):F0} all=[{string.Join(",", first.Select(v => v.ToString("F0")))}]");
        Console.WriteLine($"GRIDBENCH scroll median={(scroll.Count > 0 ? Median(scroll) : 0):F0} p90={(scroll.Count > 0 ? scroll.OrderBy(v => v).ElementAt(scroll.Count * 9 / 10) : 0):F0} max={(scroll.Count > 0 ? scroll.Max() : 0):F0} n={scroll.Count}");
        window.Close();
    }
}
