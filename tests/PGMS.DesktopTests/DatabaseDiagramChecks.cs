using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Npgsql;
using PGMS.Desktop;
using PGMS.Desktop.ViewModels;
using PGMS.PostgreSql;

/// <summary>Database Diagrams tab (PGMS-028, M1043). Runs in its own throwaway database.</summary>
internal static class DatabaseDiagramChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        RoutingChecks(check);
        string database = "pgms_dgui_" + Guid.NewGuid().ToString("N")[..8];
        await using (var server = new NpgsqlConnection(connection))
        {
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", server); await create.ExecuteNonQueryAsync();
        }
        string target = new NpgsqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;
        try { await RunInDatabaseAsync(target, database, check); }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var server = new NpgsqlConnection(connection);
            await server.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", server); await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The case of the user's capture of 07/11/2026: many relationships reaching one side of a table, and channels that used to share the same line.</summary>
    private static void RoutingChecks(Action<bool, string> check)
    {
        var tables = new Dictionary<string, Rect>
        {
            ["loterias"] = new(470, 615, 160, 200), ["falhas"] = new(470, 225, 235, 150), ["cartoes"] = new(470, 40, 170, 100), ["grupos"] = new(765, 225, 145, 330),
            ["jogos"] = new(970, 225, 220, 275), ["sorteios"] = new(843, 738, 130, 95), ["precos"] = new(470, 885, 160, 60), ["extra"] = new(765, 40, 140, 100),
        };
        (string Primary, string Foreign)[] pairs = [("loterias", "falhas"), ("loterias", "cartoes"), ("loterias", "grupos"), ("loterias", "jogos"), ("loterias", "sorteios"), ("loterias", "precos"), ("loterias", "extra"), ("grupos", "jogos"), ("grupos", "jogos"), ("jogos", "jogos")];
        var requests = pairs.Select(p => new DiagramRouting.Request(p.Primary, tables[p.Primary], p.Foreign, tables[p.Foreign])).ToArray();
        var routes = DiagramRouting.Layout(requests, tables.Values.ToArray());
        if (Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") is { Length: > 0 } folder)
        {
            var canvas = new Canvas { Width = 1300, Height = 1000, Background = Brushes.White };
            foreach (var (name, box) in tables) { var b = new Border { Width = box.Width, Height = box.Height, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = new TextBlock { Text = name, Margin = new Thickness(4) } }; Canvas.SetLeft(b, box.X); Canvas.SetTop(b, box.Y); canvas.Children.Add(b); }
            foreach (var r in routes) { canvas.Children.Add(new Polyline { Points = new PointCollection(r.Points), Stroke = Brushes.DimGray, StrokeThickness = 4 }); canvas.Children.Add(new Polyline { Points = new PointCollection(r.Points), Stroke = Brushes.White, StrokeThickness = 2 }); }
            canvas.Measure(new Size(1300, 1000)); canvas.Arrange(new Rect(0, 0, 1300, 1000));
            var bitmap = new RenderTargetBitmap(1300, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(folder); using var file = File.Create(System.IO.Path.Combine(folder, "routing.png")); encoder.Save(file);
        }        var segments = routes.SelectMany((r, i) => r.Points.Zip(r.Points.Skip(1), (a, b) => (Route: i, A: a, B: b))).ToArray();
        bool Overlap((int Route, Point A, Point B) s, (int Route, Point A, Point B) u)
        {
            if (s.Route == u.Route) return false;
            bool sh = Math.Abs(s.A.Y - s.B.Y) < 0.5, uh = Math.Abs(u.A.Y - u.B.Y) < 0.5, sv = Math.Abs(s.A.X - s.B.X) < 0.5, uv = Math.Abs(u.A.X - u.B.X) < 0.5;
            static bool Shared(double a1, double a2, double b1, double b2) => Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) - Math.Max(Math.Min(a1, a2), Math.Min(b1, b2)) > 1;
            if (sh && uh && Math.Abs(s.A.Y - u.A.Y) < 2) return Shared(s.A.X, s.B.X, u.A.X, u.B.X);
            if (sv && uv && Math.Abs(s.A.X - u.A.X) < 2) return Shared(s.A.Y, s.B.Y, u.A.Y, u.B.Y);
            return false;
        }
        var overlapping = segments.SelectMany((s, i) => segments.Skip(i + 1).Where(u => Overlap(s, u)).Select(u => (s.Route, u.Route))).ToArray();
        check(overlapping.Length == 0, $"No two relationship lines run on top of each other ({overlapping.Length} overlaps)");
        var rightEnds = routes.Select((r, i) => (r, i)).Where(x => pairs[x.i].Primary == "loterias" && Math.Abs(x.r.Points[0].X - tables["loterias"].Right) < 0.5).Select(x => x.r.Points[0].Y).ToArray();
        check(rightEnds.Length == 4 && rightEnds.Distinct().Count() == 4 && rightEnds.Zip(rightEnds.Skip(1)).All(p => Math.Abs(p.First - p.Second) >= 8), "Ends on one side of a table sit side by side, as in SSMS");
        var lot = tables["loterias"];
        check(routes.Where((_, i) => pairs[i].Primary == "loterias").SelectMany(r => r.Points.Take(1)).All(p => p.X >= lot.Left - 0.5 && p.X <= lot.Right + 0.5 && p.Y >= lot.Top - 0.5 && p.Y <= lot.Bottom + 0.5), "Every end starts on the border of its table");
        var jogosToLoterias = routes[3];
        var grupos = tables["grupos"];
        check(jogosToLoterias.Points.Count == 4 && !(jogosToLoterias.Points[1].X > grupos.Left - 4 && jogosToLoterias.Points[1].X < grupos.Right + 4), "The middle segment goes around a table in the way when there is room");
        var self = routes[9];
        check(self.Points.Count == 4 && self.Points[0].Y != self.Points[3].Y && self.Points[1].X > tables["jogos"].Right, "A self reference loops outside the table");

        // Arrange Tables with the fewest crossing lines (pedido do usuário, 08/11/2026).
        Size cell = new(150, 80);
        (int, int)[] diagonals = [(0, 3), (1, 2)];
        var grid = new[] { new Point(20, 20), new Point(230, 20), new Point(20, 160), new Point(230, 160) };
        DiagramRouting.Route[] RoutesAt(IReadOnlyList<Point> at, IReadOnlyList<Size> sizes, IReadOnlyList<(int, int)> links)
        {
            var keys = Enumerable.Range(0, at.Count).Select(i => (object)i).ToArray();
            var boxes = at.Select((p, i) => new Rect(p, sizes[i])).ToArray();
            return DiagramRouting.Layout(links.Select(l => new DiagramRouting.Request(keys[l.Item1], boxes[l.Item1], keys[l.Item2], boxes[l.Item2])).ToArray(), boxes).ToArray();
        }
        DiagramRouting.Route Path(params Point[] points) => new(points, new Vector(1, 0), new Vector(-1, 0));
        check(DiagramArrange.Crossings([Path(new(0, 50), new(100, 50)), Path(new(50, 0), new(50, 100))]) == 1 && DiagramArrange.Crossings([Path(new(0, 50), new(100, 50)), Path(new(150, 0), new(150, 100))]) == 0
            && DiagramArrange.Crossings([Path(new(0, 50), new(60, 50), new(60, 150)), Path(new(30, 0), new(30, 100), new(200, 100))]) == 2, "Crossings counts every place where two lines cut through each other");
        int captureBefore = DiagramArrange.Crossings(routes);
        var four = Enumerable.Repeat(cell, 4).ToArray();
        (int, int)[] dense = [(0, 3), (1, 2), (0, 1), (2, 3), (0, 2), (3, 1)];
        static IEnumerable<int[]> Orders(int[] items) => items.Length <= 1 ? [items] : items.SelectMany(first => Orders(items.Where(i => i != first).ToArray()).Select(rest => (int[])[first, .. rest]));
        foreach (var links in new[] { diagonals, dense })
        {
            var every = Orders([0, 1, 2, 3]).Select(order => DiagramArrange.Crossings(RoutesAt(Enumerable.Range(0, 4).Select(t => grid[Array.IndexOf(order, t)]).ToArray(), four, links))).ToArray();
            int got = DiagramArrange.Crossings(RoutesAt(DiagramArrange.Arrange(four, links, 20, 20), four, links));
            check(got <= every.Min(), $"Arrange Tables crosses no more lines than the best order of the grid ({links.Length} links: {got} crossings; the 24 orders range from {every.Min()} to {every.Max()})");
        }
        var names = tables.Keys.ToArray();
        var sizesOfCapture = names.Select(n => tables[n].Size).ToArray();
        var captureLinks = pairs.Select(p => (Array.IndexOf(names, p.Primary), Array.IndexOf(names, p.Foreign))).ToArray();
        var captureArranged = DiagramArrange.Arrange(sizesOfCapture, captureLinks, 20, 20);
        var captureBoxes = captureArranged.Select((p, i) => new Rect(p, sizesOfCapture[i])).ToArray();
        int captureCrossings = DiagramArrange.Crossings(RoutesAt(captureArranged, sizesOfCapture, captureLinks));
        check(captureBoxes.All(a => captureBoxes.All(b => a == b || !a.IntersectsWith(b))) && captureBefore > 0 && captureCrossings < captureBefore,
            $"Arranging the tables of the user's capture overlaps no tables and crosses fewer lines than the capture ({captureCrossings} crossings; the capture has {captureBefore})");

        // A dense real-world layout: a line used to run behind a third table and glue to a fourth.
        // A line must keep clear of the tables it does not connect: 6 units is the double line (2 to each side) plus 4 of background.
        (int Near, double Closest) NearTables(IReadOnlyList<Point> at, IReadOnlyList<Size> sizes, IReadOnlyList<(int Primary, int Foreign)> links)
        {
            var boxes = at.Select((p, i) => new Rect(p, sizes[i])).ToArray();
            var lines = RoutesAt(at, sizes, links.Select(l => (l.Primary, l.Foreign)).ToArray());
            int tooClose = 0; double closest = double.MaxValue;
            for (int l = 0; l < lines.Length; l++)
                foreach (var (a, b) in lines[l].Points.Zip(lines[l].Points.Skip(1)))
                    for (int t = 0; t < boxes.Length; t++)
                    {
                        if (t == links[l].Primary || t == links[l].Foreign) continue;
                        double dx = Math.Max(0, Math.Max(boxes[t].Left - Math.Max(a.X, b.X), Math.Min(a.X, b.X) - boxes[t].Right));
                        double dy = Math.Max(0, Math.Max(boxes[t].Top - Math.Max(a.Y, b.Y), Math.Min(a.Y, b.Y) - boxes[t].Bottom));
                        double distance = Math.Sqrt(dx * dx + dy * dy);
                        closest = Math.Min(closest, distance);
                        if (distance < 6) tooClose++;
                    }
            return (tooClose, closest);
        }
        Size[] hubLayout = [new(220, 274), new(143, 326), new(230, 148), new(152, 148), new(160, 260), new(158, 111), new(150, 130), new(170, 130)]; // Six tables around a hub and two below it
        (int, int)[] hubLayoutLinks = [(3, 0), (4, 0), (4, 1), (4, 2), (4, 3), (4, 5), (4, 6), (4, 7)];
        var hubLayoutAt = DiagramArrange.Arrange(hubLayout, hubLayoutLinks, 20, 20);
        var hubLayoutNear = NearTables(hubLayoutAt, hubLayout, hubLayoutLinks);
        check(hubLayoutNear.Near == 0, $"Arrange Tables keeps every line clear of the tables it does not connect ({hubLayoutNear.Near} too close; closest {hubLayoutNear.Closest:0.#})");
        void Render(string name, IReadOnlyList<Rect> rendered, IReadOnlyList<DiagramRouting.Route> lines)
        {
            if (Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") is not { Length: > 0 } renderFolder) return;
            double width = rendered.Max(b => b.Right) + 40, height = rendered.Max(b => b.Bottom) + 40;
            var canvas = new Canvas { Width = width, Height = height, Background = Brushes.White };
            for (int i = 0; i < rendered.Count; i++) { var b = new Border { Width = rendered[i].Width, Height = rendered[i].Height, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = new TextBlock { Text = i.ToString(), Margin = new Thickness(4) } }; Canvas.SetLeft(b, rendered[i].X); Canvas.SetTop(b, rendered[i].Y); canvas.Children.Add(b); }
            foreach (var r in lines) { canvas.Children.Add(new Polyline { Points = new PointCollection(r.Points), Stroke = Brushes.DimGray, StrokeThickness = 4 }); canvas.Children.Add(new Polyline { Points = new PointCollection(r.Points), Stroke = Brushes.White, StrokeThickness = 2 }); }
            canvas.Measure(new Size(width, height)); canvas.Arrange(new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(renderFolder); using var file = File.Create(System.IO.Path.Combine(renderFolder, name)); encoder.Save(file);
        }
        Render("arrange.png", hubLayoutAt.Select((p, i) => new Rect(p, hubLayout[i])).ToArray(), RoutesAt(hubLayoutAt, hubLayout, hubLayoutLinks));

        // A table right in the way: the line goes around it (pedido do usuário, 08/11/2026).
        static bool Keeps(DiagramRouting.Route line, Rect table) => line.Points.Zip(line.Points.Skip(1)).All(s => !DiagramRouting.Near(table).IntersectsWith(new Rect(s.First, s.Second)));
        static bool Orthogonal(DiagramRouting.Route line) => line.Points.Zip(line.Points.Skip(1)).All(s => s.First != s.Second && (Math.Abs(s.First.X - s.Second.X) < 0.01 || Math.Abs(s.First.Y - s.Second.Y) < 0.01));
        Rect rowA = new(20, 100, 150, 100), rowB = new(230, 100, 150, 100), rowC = new(440, 100, 150, 100);
        var aroundRow = DiagramRouting.Layout([new DiagramRouting.Request("a", rowA, "c", rowC)], [rowA, rowB, rowC])[0];
        check(Keeps(aroundRow, rowB) && Orthogonal(aroundRow) && aroundRow.Points.Count == 6 && aroundRow.Points[0].X == rowA.Right && aroundRow.Points[^1].X == rowC.Left, $"A line goes around the table between two tables of a row ({aroundRow.Points.Count} points)");
        check(aroundRow.Points[1].X - rowA.Right >= 16 && rowC.Left - aroundRow.Points[^2].X >= 16, "A line going around leaves room for the key and the ∞ at its ends");
        check(DiagramRouting.Build(DiagramRouting.FromRoute(aroundRow, rowA, rowC), rowA, rowC).Route.Points.SequenceEqual(aroundRow.Points), "A line going around keeps its shape when it is taken by hand");
        Rect colA = new(100, 20, 150, 100), colB = new(100, 180, 150, 100), colC = new(100, 340, 150, 100);
        var aroundColumn = DiagramRouting.Layout([new DiagramRouting.Request("a", colA, "c", colC)], [colA, colB, colC])[0];
        check(Keeps(aroundColumn, colB) && Orthogonal(aroundColumn) && aroundColumn.Points[0].Y == colA.Bottom && aroundColumn.Points[^1].Y == colC.Top, "A line goes around the table between two tables of a column");
        var aroundTwo = DiagramRouting.Layout([new DiagramRouting.Request("a", rowA, "c", rowC), new DiagramRouting.Request("a", rowA, "c", rowC), new DiagramRouting.Request("a", rowA, "c", rowC)], [rowA, rowB, rowC]);
        var aroundSegments = aroundTwo.SelectMany((r, i) => r.Points.Zip(r.Points.Skip(1), (a, b) => (Route: i, A: a, B: b))).ToArray();
        check(aroundTwo.All(r => Keeps(r, rowB)) && !aroundSegments.Any(s => aroundSegments.Any(u => Overlap(s, u))), "Several lines going around one table do not run on top of each other");
        var withoutDetour = DiagramRouting.Layout([new DiagramRouting.Request("a", rowA, "c", rowC)], [rowA, rowB, rowC], detours: false)[0];
        check(!Keeps(withoutDetour, rowB) && withoutDetour.Points.Count == 2, "The search of Arrange Tables can route without going around, so it prefers orders that need no detour");
        Rect tightA = new(20, 100, 150, 100), tightB = new(175, 100, 150, 100), tightC = new(330, 100, 150, 100);
        var tight = DiagramRouting.Layout([new DiagramRouting.Request("a", tightA, "c", tightC)], [tightA, tightB, tightC])[0];
        check(Orthogonal(tight) && tight.Points[0].X == tightA.Right && tight.Points[^1].X == tightC.Left, "With no room for the ends to leave their tables, the line keeps its direct shape");
        var denseSizes = Enumerable.Repeat(new Size(150, 100), 9).ToArray();
        (int, int)[] denseLinks = [(0, 8), (2, 6), (1, 7), (3, 5), (0, 2), (6, 8), (0, 6), (2, 8), (1, 3), (5, 7), (0, 4), (4, 8), (2, 4), (4, 6)];
        var denseAt = DiagramArrange.Arrange(denseSizes, denseLinks, 20, 20, 300);
        var denseNear = NearTables(denseAt, denseSizes, denseLinks);
        Render("dense.png", denseAt.Select((p, i) => new Rect(p, denseSizes[i])).ToArray(), RoutesAt(denseAt, denseSizes, denseLinks));
        check(denseNear.Near == 0, $"Arrange Tables leaves no line behind a table in a dense diagram ({denseNear.Near} too close; closest {denseNear.Closest:0.#}; {DiagramArrange.Crossings(RoutesAt(denseAt, denseSizes, denseLinks))} crossings)");
        Render("around.png", [rowA, rowB, rowC], aroundTwo);
        var bigBoxes = Enumerable.Range(0, 40).Select(i => new Rect(20 + i % 8 * 220, 20 + i / 8 * 180, 160, 120)).ToArray();
        var bigKeys = bigBoxes.Select((_, i) => (object)i).ToArray();
        var bigRequests = Enumerable.Range(0, 20).Select(i => new DiagramRouting.Request(bigKeys[i], bigBoxes[i], bigKeys[39 - i], bigBoxes[39 - i])).ToArray();
        var bigClock = System.Diagnostics.Stopwatch.StartNew();
        var bigRoutes = DiagramRouting.Layout(bigRequests, bigBoxes);
        bigClock.Stop();
        check(bigClock.ElapsedMilliseconds < 500 && bigRoutes.All(Orthogonal), $"Going around stays fast enough to redraw while a table moves (40 tables, 20 long lines: {bigClock.ElapsedMilliseconds} ms)");
        // Bigger diagrams with tables of mixed sizes: the search still has to leave the lines clear where it can.
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var random = new Random(seed);
            var sizes = Enumerable.Range(0, 12).Select(_ => new Size(120 + random.Next(120), 80 + random.Next(240))).ToArray();
            var links = Enumerable.Range(1, 11).Select(t => (random.Next(t), t)).Concat(Enumerable.Range(0, 5).Select(_ => (random.Next(12), random.Next(12)))).Where(l => l.Item1 != l.Item2).Distinct().ToArray();
            var placed = DiagramArrange.Arrange(sizes, links, 20, 20, 300);
            var placedBoxes = placed.Select((p, i) => new Rect(p, sizes[i])).ToArray();
            var placedNear = NearTables(placed, sizes, links);
            check(placedBoxes.All(a => placedBoxes.All(b => a == b || !a.IntersectsWith(b))), $"Arrange Tables overlaps no tables in a mixed diagram (seed {seed}: {links.Length} lines, {placedNear.Near} too close to a table,{DiagramArrange.Crossings(RoutesAt(placed, sizes, links))} crossings)");
        }
        // Lines placed by hand, as SSMS 22.11 does (pilotagem de 07/11/2026, captures in artifacts/acceptance/M1043-line-routing).
        Rect key = new(20, 100, 216, 90), fk = new(710, 400, 216, 90);
        var auto = DiagramRouting.Layout([new DiagramRouting.Request("k", key, "f", fk)])[0];
        var manual = DiagramRouting.FromRoute(auto, key, fk);
        check(auto.Points.Count == 4 && DiagramRouting.Build(manual, key, fk).Route.Points.SequenceEqual(auto.Points), "A line taken by hand starts with the shape it had");
        var middleMoved = DiagramRouting.Build(DiagramRouting.DragSegment(manual, key, fk, 1, new Point(600, 300)), key, fk).Route.Points;
        check(middleMoved[1].X == 600 && middleMoved[2].X == 600 && middleMoved[0] == auto.Points[0] && middleMoved[^1] == auto.Points[^1], "Dragging the middle segment moves it sideways; the ends stay");
        var slid = DiagramRouting.Build(DiagramRouting.DragSegment(manual, key, fk, 2, new Point(700, 420)), key, fk).Route.Points;
        check(slid[^1] == new Point(fk.Left, 420) && slid[^2] == new Point(auto.Points[1].X, 420), "Dragging an end segment slides the end along the side of its table");
        var wrapped = DiagramRouting.DragSegment(manual, key, fk, 2, new Point(700, 700));
        var wrappedPoints = DiagramRouting.Build(wrapped, key, fk).Route.Points;
        check(wrapped.ForeignSide == DiagramSide.Bottom && wrappedPoints.Count == 5 && wrappedPoints[^1] == new Point(fk.Left + 4, fk.Bottom) && wrappedPoints[^2] == new Point(fk.Left + 4, 700) && wrappedPoints[2] == new Point(auto.Points[1].X, 700),
            "Dragged past the corner, the end goes to the next side and the segment stays where it was dropped");
        Rect fkMoved = new(fk.X + 100, fk.Y - 50, fk.Width, fk.Height);
        var followed = DiagramRouting.Build(wrapped, key, fkMoved).Route.Points;
        check(followed[1].X == auto.Points[1].X && followed[2].Y == 700 && followed[^1] == new Point(fkMoved.Left + 4, fkMoved.Bottom), "Moving a table keeps the bends and the end follows the table");
        var topWrapped = DiagramRouting.Build(DiagramRouting.DragSegment(manual, key, fk, 0, new Point(100, 60)), key, fk).Route.Points;
        check(topWrapped[0] == new Point(key.Right - 4, key.Top) && topWrapped[1] == new Point(key.Right - 4, 60) && topWrapped[2].Y == 60, "The key end goes to the top side when its segment is dragged above the table");
        Rect left = new(0, 0, 100, 90), right = new(300, 0, 100, 90);
        var straight = DiagramRouting.Layout([new DiagramRouting.Request("l", left, "r", right)])[0];
        var straightMoved = DiagramRouting.Build(DiagramRouting.DragSegment(DiagramRouting.FromRoute(straight, left, right), left, right, 0, new Point(200, 60)), left, right).Route.Points;
        // Capture of 08/11/2026 (JogosUsuariosAndamento moved left): the bends of lines placed by hand ended up behind the table, and the ∞ stayed on the border, away from where the line arrives.
        Rect hidKey = new(330, 110, 150, 140), hidLower = new(325, 315, 160, 200), hidTable = new(800, 100, 220, 270);
        var hidFirst = new DiagramManualRoute(DiagramSide.Right, 30, DiagramSide.Left, 100, [830]);
        var hidFirstPath = DiagramRouting.Build(hidFirst, hidKey, hidTable);
        check(hidFirstPath.Route.Points.SequenceEqual([new Point(480, 140), new Point(800, 140)]) && hidFirstPath.Manual.ForeignSide == DiagramSide.Left, $"A bend left behind the moved table: the end goes to where the line reaches the table ({string.Join(" ", hidFirstPath.Route.Points)})");
        var hidSecondPath = DiagramRouting.Build(new DiagramManualRoute(DiagramSide.Top, 150, DiagramSide.Left, 135, [285, 830]), hidLower, hidTable);
        check(hidSecondPath.Route.Points.SequenceEqual([new Point(475, 315), new Point(475, 285), new Point(800, 285)]), $"With two bends, only the part behind the table goes away ({string.Join(" ", hidSecondPath.Route.Points)})");
        check(hidFirstPath.Route.ForeignOut == new Vector(-1, 0), "The ∞ turns to the side the line arrives on");
        Rect hidBarely = new(828, 100, 220, 270);
        var hidBarelyPath = DiagramRouting.Build(hidFirst, hidKey, hidBarely);
        check(hidBarelyPath.Manual.ForeignSide == DiagramSide.Left && hidBarelyPath.Route.Points[^1] == new Point(828, 140), $"A table moved just past the bend does not throw the end to the far side ({hidBarelyPath.Manual.ForeignSide}; {string.Join(" ", hidBarelyPath.Route.Points)})");
        var hidBack = DiagramRouting.Build(hidFirst, hidKey, new Rect(900, 100, 220, 270)).Route.Points;
        check(hidBack.SequenceEqual([new Point(480, 140), new Point(830, 140), new Point(830, 200), new Point(900, 200)]), "With the table moved back, the line placed by hand has its shape again");
        // The next capture of 08/11/2026: the table moved past the bend, which is now beyond its far side. The line ran behind the table, looped around and came back to the right side.
        Rect hidPassed = new(600, 100, 220, 270);
        var hidPassedPath = DiagramRouting.Build(hidFirst, hidKey, hidPassed);
        check(hidPassedPath.Route.Points.SequenceEqual([new Point(480, 140), new Point(600, 140)]) && hidPassedPath.Manual.ForeignSide == DiagramSide.Left && hidPassedPath.Route.ForeignOut == new Vector(-1, 0), $"A bend left beyond the far side of the table: the line still ends where it first reaches the table ({hidPassedPath.Manual.ForeignSide}; {string.Join(" ", hidPassedPath.Route.Points)})");
        var hidFromRight = DiagramRouting.Build(new DiagramManualRoute(DiagramSide.Left, 30, DiagramSide.Left, 100, [900]), new Rect(1000, 110, 150, 140), hidPassed);
        check(hidFromRight.Manual.ForeignSide == DiagramSide.Right && hidFromRight.Route.Points[^1] == new Point(820, 200), $"A line that really comes from beyond the table ends on that side ({hidFromRight.Manual.ForeignSide}; {string.Join(" ", hidFromRight.Route.Points)})");
        var hidKeyPath = DiagramRouting.Build(new DiagramManualRoute(DiagramSide.Right, 30, DiagramSide.Left, 45, [350]), new Rect(200, 100, 220, 270), new Rect(600, 100, 150, 90));
        check(hidKeyPath.Manual.PrimarySide == DiagramSide.Right && hidKeyPath.Route.Points[0].X == 420 && hidKeyPath.Route.Points[0].Y == 145 && hidKeyPath.Route.Points.Count == 2, $"The key end does the same when the bend is behind its table ({string.Join(" ", hidKeyPath.Route.Points)})");

        // Lining up two parallel segments removes the step between them; the key and ∞ ends go to any side (pedidos do usuário, 08/11/2026).
        var stepped = new DiagramManualRoute(DiagramSide.Right, 45, DiagramSide.Left, 45, [400, 200, 500]);
        check(DiagramRouting.Build(stepped, key, fk).Route.Points.Count == 6, "A line with a step has its bends");
        var near = DiagramRouting.Build(DiagramRouting.DragSegment(stepped, key, fk, 2, new Point(450, 149), snap: 6), key, fk).Route.Points;
        check(near.Count == 4 && near[1] == new Point(500, 145) && near[2] == new Point(500, 445), "Dropping a segment near a parallel one lines them up and the step goes away");
        check(DiagramRouting.Build(DiagramRouting.DragSegment(stepped, key, fk, 2, new Point(450, 149)), key, fk).Route.Points.Count == 6, "Without snapping the step stays");
        var intoTable = DiagramRouting.Build(DiagramRouting.DragSegment(new DiagramManualRoute(DiagramSide.Right, 45, DiagramSide.Left, 45, [400, 300, 600]), key, fk, 2, new Point(500, 441), snap: 6), key, fk).Route.Points;
        check(intoTable.Count == 4 && intoTable[2] == new Point(400, 445) && intoTable[3] == new Point(fk.Left, 445), "A segment lined up with the end segment runs straight into the table");
        var keyOnTop = DiagramRouting.MoveEnd(manual, key, fk, true, new Point(100, 96));
        var keyOnTopPoints = DiagramRouting.Build(keyOnTop, key, fk).Route.Points;
        check(keyOnTop.PrimarySide == DiagramSide.Top && keyOnTopPoints[0] == new Point(100, key.Top) && keyOnTopPoints[1] == new Point(100, key.Top - 16) && keyOnTopPoints[2] == new Point(auto.Points[1].X, key.Top - 16),
            "Dragging the key to the top of its table makes the line leave from the top");
        var endBelow = DiagramRouting.MoveEnd(manual, key, fk, false, new Point(800, fk.Bottom + 3));
        var endBelowPoints = DiagramRouting.Build(endBelow, key, fk).Route.Points;
        check(endBelow.ForeignSide == DiagramSide.Bottom && endBelowPoints[^1] == new Point(800, fk.Bottom) && endBelowPoints[^2] == new Point(800, fk.Bottom + 16),
            "Dragging the ∞ to the bottom of its table makes the line arrive from below");
        var endLeft = DiagramRouting.MoveEnd(endBelow, key, fk, false, new Point(fk.Left - 2, 470));
        check(endLeft.ForeignSide == DiagramSide.Left && DiagramRouting.Build(endLeft, key, fk).Route.Points[^1] == new Point(fk.Left, 470), "The end can go back to a side it left");
        // The case of CartoesFixosBolao (pedido do usuário, 08/11/2026): the vertical segment dragged over the table makes the line come in from the top,
        // so the ∞ goes to the top instead of staying on the right, detached from the line.
        Rect lotBox = new(280, 230, 160, 190), cards = new(20, 410, 165, 90);
        var cardsRoute = new DiagramManualRoute(DiagramSide.Left, 150, DiagramSide.Right, 47, [235]);
        check(DiagramRouting.Build(cardsRoute, lotBox, cards).Route.Points.SequenceEqual([new Point(280, 380), new Point(235, 380), new Point(235, 457), new Point(185, 457)]), "The line of CartoesFixosBolao before the drag");
        var overTable = DiagramRouting.Build(DiagramRouting.DragSegment(cardsRoute, lotBox, cards, 1, new Point(95, 420)), lotBox, cards);
        check(overTable.Manual.ForeignSide == DiagramSide.Top && overTable.Route.Points.SequenceEqual([new Point(280, 380), new Point(95, 380), new Point(95, 410)]), "Dragged over the table, the line comes in from the top and the ∞ goes there");
        var beyond = DiagramRouting.Build(DiagramRouting.DragSegment(cardsRoute, lotBox, cards, 1, new Point(0, 420)), lotBox, cards);
        check(beyond.Manual.ForeignSide == DiagramSide.Left && beyond.Route.Points[^1] == new Point(20, 457) && beyond.Route.Points[^2] == new Point(0, 457), "Dragged past the table, the ∞ goes to the side the line now reaches");
        check(straight.Points.Count == 2 && straightMoved.Count == 2 && straightMoved[0] == new Point(100, 60) && straightMoved[1] == new Point(300, 60), "A straight line moves both ends together");
    }

    private static double Distance((Point A, Point B) segment, Point at)
    {
        var d = segment.B - segment.A; double length = d.LengthSquared;
        double t = length == 0 ? 0 : Math.Clamp(((at - segment.A) * d) / length, 0, 1);
        return (at - (segment.A + t * d)).Length;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    private static async Task RunInDatabaseAsync(string connection, string database, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        await Sql("""
            CREATE TABLE public.customers (id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE public.orders (id integer PRIMARY KEY, customer_id integer NOT NULL CONSTRAINT fk_orders_customers REFERENCES public.customers(id), placed date);
            CREATE TABLE public.products (id integer PRIMARY KEY, code text NOT NULL);
            CREATE TABLE public.order_lines (order_id integer NOT NULL, product_id integer, quantity integer);
            CREATE TABLE public.scratch (id integer);
            """);
        static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var service = new DiagramService(connection);
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 850 }; window.Show();
        try
        {
            await window.ConnectExplorerAsync(connection);
            var tree = (TreeView)window.FindName("ExplorerTree");
            var catalog = new CatalogService(connection);
            async Task<string[]> Menu(CatalogEntry entry)
            {
                tree.Items.Clear(); var node = new CatalogNode(entry, catalog); tree.Items.Add(node); node.IsSelected = true;
                await Idle();
                var menu = tree.ContextMenu; menu.PlacementTarget = tree; menu.IsOpen = true;
                await Idle();
                var names = menu.Items.OfType<MenuItem>().Select(m => m.Name).ToArray(); menu.IsOpen = false;
                return names;
            }
            check((await Menu(new CatalogEntry(CatalogKind.DatabaseDiagrams, "Database Diagrams", database))).SequenceEqual(["ExplorerNewDiagram", "ExplorerInstallDiagramSupport", "ExplorerRefresh"]), "Database Diagrams folder menu: New Database Diagram, Install Diagram Support, Refresh");
            check((await Menu(new CatalogEntry(CatalogKind.DatabaseDiagram, "Sales", database, null, 7))).SequenceEqual(["ExplorerNewDiagram", "ExplorerModifyDiagram", "ExplorerRenameDiagram", "ExplorerDeleteDiagram", "ExplorerRefresh"]), "Diagram menu: New Database Diagram, Modify, Rename, Delete, Refresh");

            check((await Menu(new CatalogEntry(CatalogKind.Views, "Views", database, "public"))).First() == "ExplorerNewView", "The Views folder menu starts with New View, as in SSMS");
            check((await Menu(new CatalogEntry(CatalogKind.Relation, "v", database, "public", 7) { RelationType = "v" })).Take(4).SequenceEqual(["ExplorerNewView", "ExplorerDesignView", "ExplorerSelectTop", "ExplorerEditTop"]), "A view menu starts with New View, Design, Select Top, Edit Top, as in SSMS");
            check(!(await Menu(new CatalogEntry(CatalogKind.Relation, "m", database, "public", 7) { RelationType = "m" })).Contains("ExplorerDesignView"), "A materialized view has no Design: CREATE OR REPLACE does not apply to it");

            // Install Diagram Support is asked first.
            string? asked = null;
            window.DiagramConfirmPrompt = q => { asked = q; return false; };
            check(await window.NewDiagramAsync(connection, showAddTable: false) is null && asked?.Contains("support objects required to use database diagramming") == true && !await service.IsInstalledAsync(), "New Database Diagram asks to install support; No creates nothing");
            window.DiagramConfirmPrompt = _ => true;
            var view = (await window.NewDiagramAsync(connection, showAddTable: false))!;
            await Idle();
            check(await service.IsInstalledAsync() && view.IsNew && view.DiagramName == "Diagram_0" && !view.IsDirty && window.DatabaseDiagrams.Single() == view, "Yes installs support and opens an empty Diagram_0 tab");

            var candidates = await view.CandidateTablesAsync();
            check(candidates.Select(c => c.Name).SequenceEqual(["customers", "order_lines", "orders", "products", "scratch"]), "Add Table lists the tables of the database, not the diagram table");
            await view.AddTablesAsync([candidates.Single(c => c.Name == "customers")]);
            check(view.Tables.Single().X == 20 && view.IsDirty && view.Title == "Diagram_0*", "Adding a table shows it and marks the tab");
            check((await view.CandidateTablesAsync()).All(c => c.Name != "customers"), "A table in the diagram is no longer offered");
            await view.AddTablesAsync(candidates.Where(c => c.Name is "orders" or "products" or "order_lines").ToArray());
            await Idle();
            Rect B(DiagramTableModel t) => new(t.X, t.Y, view.BoxOf(t).ActualWidth, view.BoxOf(t).ActualHeight);
            var all = view.Tables.ToArray();
            check(all.All(a => all.All(b => a == b || !B(a).IntersectsWith(B(b)))), "Several tables added at once do not overlap");
            DiagramTableModel T(string name) => view.Tables.Single(t => t.Name == name);
            check(view.Relations.Single() is { Key.Name: "fk_orders_customers" } r && r.Primary == T("customers") && r.Foreign == T("orders"), "The existing foreign key is drawn between its tables");
            check(view.Lines.Children.OfType<Polyline>().Any(p => System.Windows.Automation.AutomationProperties.GetName(p) == "Relationship fk_orders_customers") && view.Lines.Children.OfType<Canvas>().Count(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Foreign key end") == 1 && view.Lines.Children.OfType<Canvas>().Count(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Primary key end") == 1, "The relationship line has its key and ∞ ends");
            static double Angle(Canvas c) => ((RotateTransform)c.RenderTransform).Angle;
            var ends = view.Lines.Children.OfType<Canvas>().Where(c => System.Windows.Automation.AutomationProperties.GetName(c)?.EndsWith(" end") == true).ToArray();
            check(ends.Single(c => c.Tag is DiagramLineEnd { Key: false }).Children.OfType<System.Windows.Shapes.Path>().Single().Data is StreamGeometry && !ends.SelectMany(c => c.Children.OfType<Ellipse>()).Any()
                && ends.Single(c => c.Tag is DiagramLineEnd { Key: true }).Children.OfType<System.Windows.Shapes.Path>().First().Fill is LinearGradientBrush, "The ends are a gold key with a gradient and a real ∞ sign (proposal A of 08/11/2026)");
            check(ends.Length == 2 && ends.All(c => Math.Abs(Angle(c) % 90) < 0.01) && Math.Abs(Math.Abs(Angle(ends[0]) - Angle(ends[1])) - 180) < 0.01, "Key and ∞ turn with the line: along it, pointing away from each table");

            // Menus.
            string[] Items(object? target) => view.FillMenu(new ContextMenu(), target).Items.OfType<MenuItem>().Select(m => m.Name).ToArray();
            view.SelectAt(new Point(T("customers").X + 10, T("customers").Y + 8));
            check(view.SelectedTables.Single() == T("customers"), "Clicking a table title selects the table");
            check(Items(new DiagramHit(T("customers"), null, DiagramHitPart.Title)).SequenceEqual(["DiagramTableView", "DiagramSetPrimaryKey", "DiagramInsertColumn", "DiagramDeleteColumn", "DiagramDeleteTables", "DiagramRemoveTables", "DiagramAddRelated", "DiagramRelationships", "DiagramIndexes", "DiagramChecks", "DiagramScript", "DiagramProperties", "DiagramDesign"]), "Table menu follows SSMS, plus Design at the end");
            var hitLine = view.Lines.Children.OfType<Polyline>().Single(p => p.Tag is DiagramRelation);
            int half = hitLine.Points.Count / 2; var middle = new Point((hitLine.Points[half - 1].X + hitLine.Points[half].X) / 2, (hitLine.Points[half - 1].Y + hitLine.Points[half].Y) / 2);
            check(view.SelectAt(middle) is DiagramRelation && view.SelectedRelation is not null && view.Lines.Children.Contains(hitLine)
                && view.Lines.Children.OfType<Polyline>().Any(p => p.Tag is null && p.Stroke is SolidColorBrush { Color: { R: 0x00, G: 0x78, B: 0xD7 } }), "Clicking a line selects it in blue and keeps the clicked line, so its context menu opens (found by mouse piloting)");
            check(Items(view.Relations.Single()).SequenceEqual(["DiagramDeleteRelationship", "DiagramRelationshipProperties"]), "Relationship menu: Delete Relationships from Database, Properties");
            check(Items(null).SequenceEqual(["DiagramNewTable", "DiagramAddTable", "DiagramNewAnnotation", "DiagramSelectAll", "DiagramShowLabels", "DiagramShowIndexes", "DiagramArrange", "DiagramZoom", "DiagramViewPageBreaks", "DiagramRecalculatePageBreaks", "DiagramCopy", "DiagramRefresh"]), "Empty area menu follows SSMS (capture m4), without Paste, plus Show Indexes and Refresh of PGMS-044");

            // Ctrl: the hand drags the visible area; Ctrl + wheel zooms (pedidos do usuário de 07/11/2026).
            var scroller = (ScrollViewer)view.FindName("Scroller");
            view.ShowPanCursor(true);
            check(view.IsPanCursorShown && ((FrameworkElement)view.FindName("Surface")).Cursor != Cursors.Hand, "Holding Ctrl shows the open drag hand over the diagram, not the pointing hand of links");
            view.ShowPanCursor(true, dragging: true);
            check(view.IsGrabCursorShown && !view.IsPanCursorShown, "Dragging with Ctrl shows the closed hand");
            view.ShowPanCursor(true);
            check(view.IsPanCursorShown, "Stopping the drag with Ctrl still held shows the open hand again");
            foreach (bool closedHand in new[] { false, true })
            {
                var image = HandCursors.Render(closedHand); var pixels = new byte[32 * 32 * 4]; image.CopyPixels(pixels, 128, 0);
                int opaque = Enumerable.Range(0, 1024).Count(i => pixels[i * 4 + 3] == 255), white = Enumerable.Range(0, 1024).Count(i => pixels[i * 4 + 3] == 255 && pixels[i * 4] > 240);
                var file = HandCursors.CursorFile(image, 15, 15);
                check(pixels[3] == 0 && pixels[(15 * 32 + 15) * 4] > 240 && white > 120 && opaque - white > 40 && file.Length == 22 + 40 + 4096 + 128 && BitConverter.ToUInt16(file, 2) == 2 && BitConverter.ToUInt16(file, 10) == 15,
                    $"The {(closedHand ? "closed" : "open")} hand is a white hand outlined in black, with the hot spot on the palm");
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(new TransformedBitmap(image, new ScaleTransform(8, 8))));
                Directory.CreateDirectory(System.IO.Path.Combine("artifacts", "acceptance", "M1043-hand-cursor"));
                using (var stream = File.Create(System.IO.Path.Combine("artifacts", "acceptance", "M1043-hand-cursor", (closedHand ? "closed" : "open") + "-hand-x8.png"))) png.Save(stream);
            }
            view.ShowPanCursor(false);
            check(!view.IsPanCursorShown, "Releasing Ctrl brings the normal cursors back");
            view.Pan(new Vector(-120, -60)); scroller.UpdateLayout();
            check(scroller.HorizontalOffset == Math.Min(120, scroller.ScrollableWidth) && scroller.VerticalOffset == Math.Min(60, scroller.ScrollableHeight) && scroller.HorizontalOffset > 0, "Dragging with the hand scrolls the visible area");
            view.Pan(new Vector(0, 0)); scroller.UpdateLayout();
            view.WheelZoom(true, new Point(200, 200), new Point(100, 100)); scroller.UpdateLayout();
            check(Math.Abs(view.Zoom - 1.5) < 0.001 && Math.Abs(scroller.HorizontalOffset - 200) < 1 && Math.Abs(scroller.VerticalOffset - 200) < 1, "Ctrl + wheel up zooms in to the next level and keeps the point under the mouse");
            view.WheelZoom(false, new Point(200, 200), new Point(100, 100)); view.WheelZoom(false, new Point(200, 200), new Point(100, 100));
            check(Math.Abs(view.Zoom - 0.75) < 0.001, "Ctrl + wheel down zooms out level by level");
            view.SetZoom(1); scroller.ScrollToHome(); await Idle();

            // The zoom can be typed; To Fit fills the tab and the box keeps showing the percentage (pedidos do usuário, 08/11/2026).
            var zoomBox = (ComboBox)view.FindName("ZoomBox");
            check(zoomBox.IsEditable && !zoomBox.IsReadOnly && !zoomBox.IsTextSearchEnabled, "The zoom box accepts typing");
            check(view.ApplyTypedZoom("125") && Math.Abs(view.Zoom - 1.25) < 0.001 && zoomBox.Text == "125%", "Typing 125 zooms to 125%");
            check(view.ApplyTypedZoom(" 62,5 % ") && Math.Abs(view.Zoom - 0.625) < 0.001, "A typed zoom may have a decimal comma and the percent sign");
            check(!view.ApplyTypedZoom("abc") && Math.Abs(view.Zoom - 0.625) < 0.001 && zoomBox.Text == $"{Math.Round(62.5)}%", "A zoom that is not a number puts the current zoom back");
            check(view.ApplyTypedZoom("1000") && view.Zoom == 4, "A typed zoom stops at 400%");
            view.SetZoom(1); await Idle();
            zoomBox.SelectedItem = "To Fit"; await Idle(); await Idle();
            var tabArea = new Rect(0, 0, scroller.ViewportWidth, scroller.ViewportHeight);
            var shown = view.Tables.Select(t => { var box = view.BoxOf(t); return new Rect(box.TranslatePoint(new Point(0, 0), scroller), box.TranslatePoint(new Point(box.ActualWidth, box.ActualHeight), scroller)); }).ToArray();
            var extent = shown.Aggregate(Rect.Union);
            check(zoomBox.Text == $"{Math.Round(view.Zoom * 100)}%", $"After To Fit the zoom box shows the percentage, not an empty box ('{zoomBox.Text}')");
            check(shown.All(r => r.Left >= -0.5 && r.Top >= -0.5 && r.Right <= tabArea.Right + 0.5 && r.Bottom <= tabArea.Bottom + 0.5), "To Fit shows every table");
            double fitMargin = 2 * 20 * view.Zoom;
            check(extent.Width + fitMargin >= tabArea.Width * 0.98 || extent.Height + fitMargin >= tabArea.Height * 0.98,
                $"To Fit fills the width or the height of the tab, with a margin of 20 px (tables {extent.Width:0}×{extent.Height:0} in {tabArea.Width:0}×{tabArea.Height:0} at {view.Zoom:P0})");
            // Every table keeps its frame below 100%: a 1 unit border is less than a pixel and could vanish (pedido do usuário, 08/11/2026).
            var missing = new List<string>();
            foreach (double level in new[] { 0.92, 0.85, 0.75, 0.66, 0.5, 0.33 })
            {
                view.SetZoom(level); scroller.ScrollToHome(); await Idle();
                int w = (int)view.ActualWidth, h = (int)view.ActualHeight;
                var frame = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); frame.Render(view);
                var pixels = new byte[w * h * 4]; frame.CopyPixels(pixels, w * 4, 0);
                bool Dark(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && pixels[(y * w + x) * 4 + 2] < 180;
                foreach (var t in view.Tables)
                {
                    var box = view.BoxOf(t);
                    var corner = box.TranslatePoint(new Point(box.ActualWidth / 2, box.ActualHeight), view);
                    var side = box.TranslatePoint(new Point(box.ActualWidth, box.ActualHeight / 2), view);
                    if (corner.Y >= h - 2 || side.X >= w - 2) continue;
                    int bx = (int)corner.X, by = (int)Math.Round(corner.Y), sx = (int)Math.Round(side.X), sy = (int)side.Y;
                    if (!Enumerable.Range(by - 2, 4).Any(y => Dark(bx, y))) missing.Add($"{t.Name} bottom at {level:P0}");
                    if (!Enumerable.Range(sx - 2, 4).Any(x => Dark(x, sy))) missing.Add($"{t.Name} right at {level:P0}");
                }
            }
            check(missing.Count == 0, "Every table frame is drawn at zooms below 100%" + (missing.Count == 0 ? "" : ": missing " + string.Join(", ", missing)));
            view.SetZoom(1); scroller.ScrollToHome(); await Idle();

            // Moving.
            view.SelectAt(new Point(T("customers").X + 10, T("customers").Y + 8));
            double x = T("customers").X;
            view.MoveSelection(new Vector(100, 40)); view.EndMove();
            check(T("customers").X == x + 100 && view.IsDirty, "Dragging the title moves the table");

            // Relationship by dragging a column (pedido do usuário, 08/11/2026): key cursor, a line like the relationship from the primary key table,
            // the column under the mouse highlighted, and one New Relationship dialog.
            view.UpdateLayout();
            int before = view.Relations.Count;
            Point RowCenter(string table, string column) { var row = view.RowOf(T(table), column); return row.TranslatePoint(new Point(row.ActualWidth / 2, row.ActualHeight / 2), view.DiagramSurface); }
            Brush RowBackground(string table, string column) => view.RowOf(T(table), column).Background;
            var cursorImage = DatabaseDiagramView.RenderKeyCursor(); var cursorPixels = new byte[32 * 32 * 4]; cursorImage.CopyPixels(cursorPixels, 32 * 4, 0);
            check(cursorPixels[3] == 0 && Enumerable.Range(0, 32 * 32).Count(i => cursorPixels[i * 4 + 3] > 200 && cursorPixels[i * 4 + 2] > 180 && cursorPixels[i * 4] < 120) > 40, "The drag cursor is the gold key on a transparent background");
            check(view.SelectAt(RowCenter("products", "code")) is DiagramHit { Column: "code", Part: DiagramHitPart.Column }, "The name of a column is part of its row, so the drag can start there");
            Point keyRow = RowCenter("products", "id"), targetRow = RowCenter("order_lines", "product_id");
            view.StartColumnDrag(new DiagramHit(T("products"), "id", DiagramHitPart.Column), keyRow);
            view.DragColumnTo(keyRow + new Vector(1, 1));
            check(view.DragPreview.Children.Count == 0 && !view.IsKeyCursorShown, "A click without moving starts no relationship");
            var empty = new Point(view.DiagramSurface.Width - 20, view.DiagramSurface.Height - 20);
            view.DragColumnTo(empty);
            check(view.IsKeyCursorShown && view.DropTarget is null && view.DragPreview.Children.OfType<Polyline>().Count() == 2 && view.DragPreview.Children.OfType<Canvas>().Count() == 1, "Dragging shows the key cursor and a double line with the key from the primary key table");
            var preview = view.DragPreview.Children.OfType<Polyline>().First().Points;
            var productsRect = new Rect(T("products").X, T("products").Y, view.BoxOf(T("products")).ActualWidth, view.BoxOf(T("products")).ActualHeight);
            check(preview[^1] == empty && Math.Abs(preview[0].Y - keyRow.Y) < 0.5 && (Math.Abs(preview[0].X - productsRect.Right) < 0.5 || Math.Abs(preview[0].X - productsRect.Left) < 0.5)
                && Enumerable.Range(0, preview.Count - 1).All(i => Math.Abs(preview[i].X - preview[i + 1].X) < 0.01 || Math.Abs(preview[i].Y - preview[i + 1].Y) < 0.01), "The line leaves the side of the dragged row and reaches the mouse in right angles");
            view.DragColumnTo(targetRow);
            check(view.DropTarget is { Column: "product_id" } && view.DropTarget.Table == T("order_lines") && RowBackground("order_lines", "product_id") != Brushes.White
                && view.DragPreview.Children.OfType<Canvas>().Count() == 2, "Over a column of another table the column is highlighted and the line ends there with the ∞");
            if (Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") is { Length: > 0 } dragFolder)
            {
                await Idle();
                void SavePng(BitmapSource image, string file) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); Directory.CreateDirectory(dragFolder); using var stream = File.Create(System.IO.Path.Combine(dragFolder, file)); encoder.Save(stream); }
                var shot = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); shot.Render(view); SavePng(shot, "drag.png");
                var enlarged = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(enlarged, BitmapScalingMode.NearestNeighbor);
                using (var dc = enlarged.RenderOpen()) { dc.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, 256, 256)); dc.DrawImage(cursorImage, new Rect(0, 0, 256, 256)); dc.DrawEllipse(Brushes.Red, null, new Point(16 * 8 + 4, 27 * 8 + 4), 3, 3); }
                var big = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); big.Render(enlarged); SavePng(big, "key-cursor.png");
            }
            view.DragColumnTo(keyRow);
            check(view.DropTarget is null && view.IsNoDropCursorShown && RowBackground("order_lines", "product_id") == Brushes.White, "Over the dragged column itself the drop is refused and the highlight goes away");
            NewRelationshipDialog? seen = null; int linesDuringDialog = 0;
            view.RelationshipPrompt = d => { seen = d; linesDuringDialog = view.Relations.Count; return false; };
            view.DragColumnTo(targetRow); view.EndColumnDrag(targetRow); view.UpdateLayout();
            check(seen is not null && view.Relations.Count == before && !view.TableChanged(T("order_lines")), "Cancel in New Relationship undoes the relationship");
            check(view.DragPreview.Children.Count == 0 && !view.IsKeyCursorShown && view.DropTarget is null && RowBackground("order_lines", "product_id") == Brushes.White, "Dropping clears the line, the cursor and the highlight");
            seen = null;
            view.StartColumnDrag(new DiagramHit(T("products"), "id", DiagramHitPart.Selector), keyRow);
            view.DragColumnTo(empty); view.EndColumnDrag(empty); view.UpdateLayout();
            check(seen is null && view.Relations.Count == before, "Dropping on the empty area creates nothing");
            var title = view.TitleOf(T("order_lines")); var onTitle = title.TranslatePoint(new Point(title.ActualWidth / 2, title.ActualHeight / 2), view.DiagramSurface);
            view.StartColumnDrag(new DiagramHit(T("products"), "id", DiagramHitPart.Column), keyRow);
            view.DragColumnTo(onTitle);
            check(view.DropTarget is { Column: null, Part: DiagramHitPart.Title } && view.BoxOf(T("order_lines")).BorderBrush != view.BoxOf(T("customers")).BorderBrush, "Over the title of another table its frame is highlighted");
            view.EndColumnDrag(onTitle); view.UpdateLayout();
            check(seen is not null && seen.Columns.Count == 0 && seen.Primary == T("products"), "Dropping on the title opens New Relationship with the foreign key column to choose");
            view.RelationshipPrompt = d =>
            {
                seen = d; linesDuringDialog = view.Relations.Count;
                d.DeleteRule.SelectedItem = "Cascade"; d.DescriptionBox.Text = "line of a product";
                return true;
            };
            view.StartColumnDrag(new DiagramHit(T("products"), "id", DiagramHitPart.Column), keyRow);
            view.DragColumnTo(targetRow); view.EndColumnDrag(targetRow); view.UpdateLayout();
            var created = view.Relations.SingleOrDefault(r => r.Key.Name == "fk_order_lines_products");
            check(created is not null && view.Relations.Count == before + 1, "Dropping a key column on a column of another table creates a relationship");
            check(seen!.NameBox.Text == "fk_order_lines_products" && seen.Primary == T("products") && seen.Columns.SequenceEqual([("id", "product_id")]) && seen.Warning.Length == 0 && seen.Title == "New Relationship",
                "New Relationship suggests fk_<foreign>_<primary> and the dropped pair");
            check(created!.Key is { OnDelete: ReferentialAction.Cascade, OnUpdate: ReferentialAction.NoAction, CheckExisting: true, Description: "line of a product" }, "The rules and description chosen in the same dialog go to the relationship");
            check(linesDuringDialog == before + 1, "The line shows while the dialog is open");
            // Double click on the line opens its properties (pedido do usuário, 08/11/2026). The list of that dialog widens with its splitter, so long names show whole (pedido do usuário, 07/11/2026).
            var createdRoute = view.RoutePoints(created); var onLine = new Point((createdRoute[0].X + createdRoute[1].X) / 2, (createdRoute[0].Y + createdRoute[1].Y) / 2);
            DesignerObjectsDialog? properties = null; bool splitterOk = false;
            view.DialogShown = w =>
            {
                if (w is not DesignerObjectsDialog d) return;
                properties = d; d.ShowInTaskbar = false;
                d.Loaded += (_, _) =>
                {
                    var list = (FrameworkElement)d.FindName("Items"); var splitter = (GridSplitter)d.FindName("ListSplitter");
                    double listWidth = list.ActualWidth;
                    splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                    splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(120, 0));
                    splitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(120, 0, false));
                    d.UpdateLayout();
                    splitterOk = Math.Abs(list.ActualWidth - listWidth - 120) < 1 && ((FrameworkElement)d.FindName("Properties")).ActualWidth >= 300;
                    d.Dispatcher.BeginInvoke(d.Close);
                };
            };
            check(view.DoubleClick(view.SelectAt(onLine)) && properties is not null && properties.Title == "Foreign Key Relationships" && ((ListBox)properties.FindName("Items")).SelectedItem == created.Key && view.SelectedRelation == created,
                "Double click on a line opens Foreign Key Relationships with that relationship");
            check(splitterOk, "Dragging the splitter widens the relationship list and keeps room for the properties");
            check(!view.DoubleClick(view.SelectAt(empty)), "Double click on the empty area opens nothing");
            view.DialogShown = null;
            check(view.TableTitle(T("order_lines")).EndsWith('*') && view.TableTitle(T("products")).EndsWith('*') && !view.TableTitle(T("customers")).EndsWith('*'), "Both tables of the new relationship show *");
            view.RelationshipPrompt = d => { seen = d; return false; };
            view.CreateRelationship(T("customers"), "id", T("products"), "code");
            check(seen!.Warning.Contains("different data types"), "New Relationship warns when the column types differ");
            view.RelationshipPrompt = d => { seen = d; return false; };
            view.CreateRelationship(T("products"), "code", T("order_lines"), "quantity");
            check(seen!.Warning.Contains("no primary or unique key"), "New Relationship warns when the referenced columns have no key");

            string render = Environment.GetEnvironmentVariable("PGMS_DIAGRAM_RENDER") ?? "";
            if (render.Length > 0)
            {
                await Idle();
                var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(render); using var file = File.Create(System.IO.Path.Combine(render, "diagram.png")); encoder.Save(file);
            }

            // Save.
            TableDesignChange? reviewed = null;
            view.NamePromptOverride = _ => "Sales";
            view.ReviewPrompt = c => { reviewed = c; return false; };
            check(!await view.SaveAsync() && view.IsDirty && view.IsNew && (long)(await Scalar("SELECT count(*) FROM pgms.diagrams"))! == 0, "Refusing the script saves nothing");
            check(reviewed!.Sql.Contains("ADD CONSTRAINT \"fk_order_lines_products\" FOREIGN KEY (\"product_id\") REFERENCES \"public\".\"products\" (\"id\")"), "The script shows the new foreign key");
            check(reviewed.Sql.Contains("ON DELETE CASCADE"), "The script has the delete rule chosen in New Relationship");
            view.ReviewPrompt = _ => true;
            check(await view.SaveAsync() && !view.IsDirty && view.Title == "Sales" && view.Info is { Name: "Sales", Version: 1 }, "Save names the diagram, applies the script and clears *");
            check((long)(await Scalar("SELECT count(*) FROM pg_constraint WHERE conname='fk_order_lines_products'"))! == 1 && (long)(await Scalar("SELECT count(*) FROM pgms.diagrams WHERE name='Sales'"))! == 1, "The foreign key and the diagram are in the database");
            check(!view.Tables.Any(view.TableChanged), "After saving no table shows *");
            check((await catalog.ChildrenAsync(new CatalogEntry(CatalogKind.DatabaseDiagrams, "Database Diagrams", database))).Single().Name == "Sales", "Object Explorer lists the saved diagram");

            // Delete Relationships from Database.
            view.DeleteRelationship(view.Relations.Single(r => r.Key.Name == "fk_orders_customers"));
            check(view.IsDirty && view.Relations.All(r => r.Key.Name != "fk_orders_customers"), "Deleting a relationship removes the line and marks the tab");
            view.ReviewPrompt = c => { reviewed = c; return true; };
            check(await view.SaveAsync() && reviewed!.Sql.Contains("DROP CONSTRAINT \"fk_orders_customers\"") && (long)(await Scalar("SELECT count(*) FROM pg_constraint WHERE conname='fk_orders_customers'"))! == 0, "Saving drops the relationship in the database");

            // Double click on a table opens Table Properties; its Columns tab edits the diagram's table (pedido do usuário, 08/11/2026).
            await Idle();
            TablePropertiesDialog? tableProperties = null; bool editedInDialog = false, keptAfterInspect = false, refusedDuplicate = false, keyLocked = false;
            view.DialogShown = w =>
            {
                if (w is not TablePropertiesDialog d) return;
                tableProperties = d; d.ShowInTaskbar = false; d.Opacity = 0;
                d.Loaded += async (_, _) =>
                {
                    try
                    {
                        var key = d.ColumnRows.Single(r => r.Name == "id"); var code = d.ColumnRows.Single(r => r.Name == "code");
                        keyLocked = !key.CanAllowNulls && code.CanAllowNulls && !((DataGrid)d.FindName("ColumnsGrid")).IsReadOnly;
                        key.Name = "product_key"; d.CommitColumns();
                        d.ColumnRows.Single(r => r.Name == "code").Type = "varchar(30)"; d.CommitColumns();
                        d.ColumnRows.Single(r => r.Name == "code").Nullable = true; d.CommitColumns();
                        editedInDialog = T("products").Column("product_key") is { Nullable: false } && T("products").Column("code") is { Type: "varchar(30)", Nullable: true }
                            && T("products").PrimaryKey.SequenceEqual(["product_key"]) && T("order_lines").ForeignKeys.Single(k => k.Name == "fk_order_lines_products").ReferencedColumns.SequenceEqual(["product_key"])
                            && view.RowOf(T("products"), "product_key") is not null && view.TableTitle(T("products")).EndsWith('*');
                        d.ColumnRows.Single(r => r.Name == "code").Name = "product_key"; d.CommitColumns();
                        refusedDuplicate = d.ColumnRows.Any(r => r.Name == "code") && ((TextBlock)d.FindName("Feedback")).Text.Contains("more than once") && T("products").Column("code") is not null;
                        for (int i = 0; i < 200 && d.Snapshot is null; i++) await Task.Delay(50);
                        keptAfterInspect = d.Snapshot is not null && d.ColumnRows.Any(r => r.Name == "product_key");
                    }
                    finally { d.Close(); }
                };
            };
            var productsTitle = view.TitleOf(T("products")).TranslatePoint(new Point(10, 8), view.DiagramSurface);
            check(view.DoubleClick(view.SelectAt(productsTitle)) && tableProperties is { ColumnTarget: not null } && view.SelectedTables.SequenceEqual([T("products")]), "Double click on a table opens Table Properties of that table");
            view.DialogShown = null;
            check(keyLocked, "The key column cannot allow nulls and the other columns can be edited");
            check(editedInDialog, "Renaming, changing the type and allowing nulls in Columns update the diagram's table, its key and the foreign key that uses it");
            check(refusedDuplicate, "A column name used twice is refused and the column goes back");
            check(keptAfterInspect, "Reading the table from the server keeps the diagram's pending column changes");
            view.ReviewPrompt = c => { reviewed = c; return true; };
            check(await view.SaveAsync() && reviewed!.Sql.Contains("RENAME COLUMN \"id\" TO \"product_key\"") && reviewed.Sql.Contains("TYPE varchar(30)") && reviewed.Sql.Contains("\"code\" DROP NOT NULL")
                && !reviewed.Sql.Contains("DROP CONSTRAINT \"fk_order_lines_products\""), "Save renames the column, changes type and nulls, and keeps the foreign key that uses it");
            check(((string)(await Scalar("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='fk_order_lines_products'"))!).Contains("product_key")
                && (string)(await Scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid='public.products'::regclass AND attname='code'"))! == "character varying(30)", "The database has the renamed key column and the new type");
            check(!view.IsDirty && view.Tables.All(t => !view.TableChanged(t)), "After saving, the diagram has no pending changes");

            // Design in the table menu, right below Properties, asks the owner to open the Table Designer (pedido do usuário, 08/11/2026);
            // after a save there, the diagram reads its tables again.
            DiagramTableModel? designed = null; view.DesignRequested += t => designed = t;
            view.SelectAt(productsTitle);
            var tableMenu = view.FillMenu(new ContextMenu(), new DiagramHit(T("products"), null, DiagramHitPart.Title));
            var menuNames = tableMenu.Items.OfType<MenuItem>().Select(i => i.Name).ToList();
            var designItem = tableMenu.Items.OfType<MenuItem>().Single(i => i.Name == "DiagramDesign");
            check(menuNames.IndexOf("DiagramDesign") == menuNames.IndexOf("DiagramProperties") + 1 && tableMenu.Items.IndexOf(designItem) == tableMenu.Items.Count - 1 && designItem.IsEnabled, "Design comes right below Properties in the table menu");
            designItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var designer = window.TableDesigners.SingleOrDefault();
            check(designed == T("products") && designer is { Schema: "public", Title: "public.products" }, "Design opens the Table Designer of that table in its own tab");
            check(await window.CloseDesignerAsync(designer!) && window.TableDesigners.Count == 0, "The Table Designer opened from the diagram closes as usual");
            check(window.OpenDiagram(connection, view.Info!.Id, view.DiagramName) == view, "The diagram tab is still open behind it");
            await Idle();
            await Sql("ALTER TABLE public.customers ADD COLUMN email text");
            await view.RefreshTablesAsync(); await Idle();
            check(T("customers").Column("email") is not null && view.RowOf(T("customers"), "email") is not null && !view.IsDirty, "After a save elsewhere the diagram reads its tables again and stays saved");

            // Delete Tables from Database.
            await view.AddTablesAsync((await view.CandidateTablesAsync()).Where(c => c.Name == "scratch").ToArray());
            await Idle();
            view.SelectAt(new Point(T("scratch").X + 10, T("scratch").Y + 8));
            view.DeleteSelectedTables();
            check(view.Tables.All(t => t.Name != "scratch") && view.HasSchemaChanges, "Delete Tables from Database removes the table from the diagram until save");
            check(await view.SaveAsync() && reviewed!.Sql.Contains("DROP TABLE \"public\".\"scratch\" RESTRICT") && await Scalar("SELECT to_regclass('public.scratch') IS NULL") is true, "Saving drops the deleted table");

            // A line moved by hand (pedido do usuário, 07/11/2026): the segment is dragged sideways and stays there.
            var line = view.Relations.Single(r => r.Key.Name == "fk_order_lines_products");
            var drawn = view.RoutePoints(line);
            var grab = new Point((drawn[0].X + drawn[1].X) / 2, (drawn[0].Y + drawn[1].Y) / 2);
            var drop = Math.Abs(drawn[0].Y - drawn[1].Y) < 0.5 ? grab + new Vector(0, 6) : grab + new Vector(6, 0);
            check(!view.IsPlacedByHand(line) && view.StartLineDrag(line, grab), "A line can be grabbed by any segment");
            view.DragLineTo(drop); view.EndLineDrag();
            var placed = view.RoutePoints(line);
            check(view.IsPlacedByHand(line) && view.IsDirty && placed.Zip(placed.Skip(1)).Any(s => Distance(s, drop) < 0.6), "Dragging a segment of a line moves it sideways and the line keeps that shape");
            check(view.Lines.Children.OfType<Line>().Count(l => l.Tag is DiagramRelation) is > 0 and var segmentCount && segmentCount <= placed.Count - 1 && view.Lines.Children.OfType<Line>().All(l => l.Cursor == Cursors.SizeNS || l.Cursor == Cursors.SizeWE), "Each segment shows a cursor across its direction");
            // The key end dragged to the top of its table (pedido do usuário, 08/11/2026).
            var products = T("products"); var productsBox = view.BoxOf(products);
            var keyEnd = view.Lines.Children.OfType<Canvas>().Single(c => c.Tag is DiagramLineEnd { Key: true });
            check(keyEnd.Cursor == Cursors.SizeAll && view.StartLineDrag(line, view.RoutePoints(line)[0], endKey: true), "The key end of a line can be grabbed");
            var topCenter = new Point(products.X + productsBox.ActualWidth / 2, products.Y - 2);
            view.DragLineTo(topCenter); view.EndLineDrag();
            var fromTop = view.RoutePoints(line);
            check(Math.Abs(fromTop[0].Y - products.Y) < 0.5 && Math.Abs(fromTop[0].X - topCenter.X) < 0.5 && fromTop[1].Y < products.Y, "Dropping the key on the top of its table makes the line leave from the top");
            var outward = fromTop[^2] - fromTop[^1]; outward.Normalize();
            await Idle();
            check(view.SelectAt(fromTop[^1] + outward * 8) is DiagramRelation, "Clicking an end selects its line");

            // Undo (pedido do usuário, 08/11/2026): Arrange Tables records the layout first; moves are undone too; the toolbar button does the same as Ctrl+Z.
            var undoButton = (Button)view.FindName("UndoButton");
            int depth = view.UndoDepth;
            check(depth >= 3 && undoButton.IsEnabled, "Moving tables and lines already recorded steps to undo");
            var beforeArrange = view.Tables.ToDictionary(t => t.Name, t => (t.X, t.Y));
            var lineBeforeArrange = view.RoutePoints(line).ToArray();
            view.ArrangeTables(); await Idle();
            check(view.UndoDepth == depth + 1 && !view.IsPlacedByHand(line), "Arrange Tables records where the tables and lines were before arranging");
            var arranged = view.Tables.ToDictionary(t => t.Name, t => (t.X, t.Y));
            view.SelectAll(); view.MoveSelection(new Vector(30, 20)); view.EndMove();
            check(view.UndoDepth == depth + 2, "Moving tables after Arrange is one more step");
            view.Undo(); await Idle();
            check(view.Tables.All(t => (t.X, t.Y) == arranged[t.Name]) && view.UndoDepth == depth + 1, "Undo after a move puts the tables back where Arrange left them");
            undoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            check(view.Tables.All(t => (t.X, t.Y) == beforeArrange[t.Name]) && view.IsPlacedByHand(line) && view.RoutePoints(line).SequenceEqual(lineBeforeArrange) && view.UndoDepth == depth,
                "The Undo button undoes Arrange Tables: tables and the line placed by hand come back");
            // The surface starts at the left and top of the tab: a table at the left edge of the diagram is at the left edge of the tab (pedido do usuário, 07/11/2026).
            view.SetZoom(0.25); await Idle();
            var surface = (FrameworkElement)view.FindName("Surface");
            check(Math.Abs(surface.TranslatePoint(new Point(0, 0), scroller).X) < 0.5 && Math.Abs(surface.TranslatePoint(new Point(0, 0), scroller).Y) < 0.5, "A diagram smaller than the tab is not centered, so the empty space on the left is not mistaken for room to move");
            view.SetZoom(1); await Idle();

            // Several tables with the mouse (pedido do usuário, 08/11/2026): the same Press, DragTo and Release the mouse events call.
            var placesBefore = view.Tables.ToDictionary(t => t, t => (t.X, t.Y));
            var inRow = view.Tables.ToArray();
            for (int i = 0; i < inRow.Length; i++) { inRow[i].X = 40 + i * 320; inRow[i].Y = 60; }
            view.SelectAll(); view.MoveSelection(new Vector(0, 0)); view.EndMove(); await Idle();
            check(inRow.Length >= 3, $"The diagram has tables to select ({inRow.Length})");
            int undoBeforeMarquee = view.UndoDepth;
            bool Selection(params DiagramTableModel[] expected) => view.SelectedTables.ToHashSet().SetEquals(expected);
            view.Press(new Point(10, 20));
            check(view.SelectedTables.Count == 0 && !view.IsMarqueeShown, "Pressing on the empty area clears the selection and draws nothing yet");
            view.DragTo(new Point(11, 21));
            check(!view.IsMarqueeShown, "A move of a pixel or two is still a click");
            view.DragTo(new Point(B(inRow[0]).Right - 5, 90));
            check(view.IsMarqueeShown && view.DragPreview.Children.OfType<Rectangle>().Single() is { Width: > 100, Height: 70 } && Selection(inRow[0]), "Dragging from the empty area draws the rectangle and selects what it touches, while it grows");
            view.DragTo(new Point(B(inRow[1]).Left + 5, 90));
            check(Selection(inRow[0], inRow[1]), "A table the rectangle touches is selected before being enclosed");
            view.DragTo(new Point(B(inRow[0]).Right - 5, 90));
            check(Selection(inRow[0]), "A table the rectangle stops touching is no longer selected");
            view.DragTo(new Point(B(inRow[1]).Left + 5, 90));
            view.Release(new Point(B(inRow[1]).Left + 5, 90)); await Idle();
            check(!view.IsMarqueeShown && view.DragPreview.Children.Count == 0 && Selection(inRow[0], inRow[1]), "Releasing takes the rectangle away and keeps the selection");
            check(view.UndoDepth == undoBeforeMarquee, "Selecting is not a step of Undo");
            view.Press(new Point(B(inRow[2]).Right + 20, 20), extend: true);
            check(Selection(inRow[0], inRow[1]), "Shift keeps the selection when pressing on the empty area");
            view.DragTo(new Point(B(inRow[2]).Right - 5, 90));
            view.Release(new Point(B(inRow[2]).Right - 5, 90)); await Idle();
            check(Selection(inRow[0], inRow[1], inRow[2]), "Shift + drag adds what the rectangle touches");
            view.Press(new Point(inRow[1].X + 10, inRow[1].Y + 8), extend: true); view.Release(new Point(inRow[1].X + 10, inRow[1].Y + 8));
            check(Selection(inRow[0], inRow[2]), "Shift + click takes a selected table out");
            view.Press(new Point(inRow[1].X + 10, inRow[1].Y + 8), extend: true); view.Release(new Point(inRow[1].X + 10, inRow[1].Y + 8));
            check(Selection(inRow[0], inRow[1], inRow[2]), "Shift + click adds a table");
            var titleOfFirst = new Point(inRow[0].X + 10, inRow[0].Y + 8);
            view.Press(titleOfFirst);
            view.DragTo(titleOfFirst + new Vector(15, 25));
            view.Release(titleOfFirst + new Vector(15, 25)); await Idle();
            check(inRow.Take(3).All(t => (t.X, t.Y) == (40 + Array.IndexOf(inRow, t) * 320 + 15, 85)) && Selection(inRow[0], inRow[1], inRow[2]), "Dragging the title of one selected table moves all of them and keeps them selected");
            check(view.UndoDepth == undoBeforeMarquee + 1, "A move with the mouse is a step of Undo (the mouse drag never recorded it)");
            view.Undo(); await Idle();
            check(inRow.Take(3).All(t => (t.X, t.Y) == (40 + Array.IndexOf(inRow, t) * 320, 60)) && view.UndoDepth == undoBeforeMarquee, "Undo puts all the tables moved together back");
            view.SelectAll();
            view.Press(new Point(inRow[1].X + 10, inRow[1].Y + 8));
            check(view.SelectedTables.Count == inRow.Length, "Pressing on one of several selected tables keeps them, so they can be moved together");
            view.Release(new Point(inRow[1].X + 10, inRow[1].Y + 8));
            check(Selection(inRow[1]), "A click without dragging leaves only that table selected");
            view.Press(new Point(B(inRow[^1]).Right + 20, 20), extend: true);
            view.DragTo(new Point(40, 90));
            check(view.SelectedTables.Count == inRow.Length, "A rectangle dragged up and to the left selects too");
            view.EndMarquee(cancel: true); view.Release(new Point(40, 90)); await Idle();
            check(Selection(inRow[1]) && !view.IsMarqueeShown, "Esc on the rectangle puts the selection back");
            view.Press(new Point(10, 20)); view.DragTo(new Point(B(inRow[0]).Right - 5, 90));
            bool capturedForMarquee = ((UIElement)view.FindName("Surface")).IsMouseCaptured;
            ((UIElement)view.FindName("Surface")).ReleaseMouseCapture();
            check(capturedForMarquee && !view.IsMarqueeShown && view.DragPreview.Children.Count == 0, "Losing the mouse to another window takes the rectangle away");
            view.Release(new Point(10, 20));
            view.SetZoom(0.5); await Idle();
            view.Press(new Point(10, 20)); view.DragTo(new Point(B(inRow[0]).Right - 5, 90));
            check(view.DragPreview.Children.OfType<Rectangle>().Single().StrokeThickness == 2 && Selection(inRow[0]), "At 50% the rectangle keeps a one pixel border and selects in diagram coordinates");
            view.Release(new Point(B(inRow[0]).Right - 5, 90));
            view.SetZoom(1);
            foreach (var (table, place) in placesBefore) { table.X = place.X; table.Y = place.Y; }
            view.SelectAll(); view.MoveSelection(new Vector(0, 0)); view.EndMove(); await Idle();

            // Layout only, then close and reopen.
            double savedX = T("customers").X;
            view.SelectAll(); view.MoveSelection(new Vector(10, 0)); view.EndMove();
            var lineBeforeSave = view.RoutePoints(line).ToArray();
            reviewed = null;
            check(await view.SaveAsync() && reviewed is null && view.Info!.Version == 5, "A layout-only save needs no script");
            view.SetZoom(0.5);
            MessageBoxResult answer = MessageBoxResult.Cancel;
            window.DiagramClosePrompt = _ => answer;
            check(!await window.CloseDiagramAsync(view) && window.DatabaseDiagrams.Contains(view), "Cancel keeps a changed diagram open");
            int id = view.Info!.Id;
            // Renaming an open diagram (pedido do usuário, 07/11/2026): the tab takes the name and version and keeps its unsaved changes.
            check(await window.RenameDiagramAsync(connection, id, "Sales", "Quarter") && view.DiagramName == "Quarter" && view.Title == "Quarter*" && view.IsDirty && view.Zoom == 0.5 && view.Info is { Name: "Quarter", Version: 6 }
                && (await service.ListAsync()).Single() is { Name: "Quarter", Version: 6 }, "Renaming an open diagram renames its tab and keeps the unsaved changes");
            check(await window.RenameDiagramAsync(connection, id, "Quarter", "Sales") && view.Title == "Sales*" && view.Info!.Version == 7, "The open tab can be renamed again with the version it got back");
            await Sql($"UPDATE pgms.diagrams SET version = version + 1 WHERE diagram_id = {id}");
            check(!await window.RenameDiagramAsync(connection, id, "Sales", "Stale") && view.DiagramName == "Sales" && (await service.ListAsync()).Single().Name == "Sales"
                && ((TextBlock)window.FindName("Status")).Text.Contains("changed or deleted since it was opened"), "A diagram changed elsewhere since the tab read it is not renamed");
            answer = MessageBoxResult.No;
            check(await window.CloseDiagramAsync(view) && window.DatabaseDiagrams.Count == 0, "Don't Save closes the tab");
            var reopened = window.OpenDiagram(connection, id, "Sales");
            for (int i = 0; i < 100 && (reopened.IsBusy || reopened.Tables.Count == 0); i++) await Task.Delay(50);
            check(reopened.Tables.Count == 4 && reopened.Tables.Single(t => t.Name == "customers").X == savedX + 10 && reopened.Zoom == 1 && !reopened.IsDirty, "Reopening shows the saved tables and positions, not the discarded zoom");
            await Idle();
            var reopenedLine = reopened.Relations.Single(r => r.Key.Name == "fk_order_lines_products");
            var reopenedPoints = reopened.RoutePoints(reopenedLine);
            check(reopened.IsPlacedByHand(reopenedLine) && reopenedPoints.Count == lineBeforeSave.Length && reopenedPoints.Zip(lineBeforeSave).All(p => (p.First - p.Second).Length < 1.5), "The line moved by hand is saved with the diagram");
            check(window.OpenDiagram(connection, id, "Sales") == reopened && window.DatabaseDiagrams.Count == 1, "Opening an open diagram activates its tab");
            check(await window.CloseDiagramAsync(reopened), "An unchanged diagram closes without asking");

            window.DiagramNamePrompt = (_, _) => "Renamed";
            check(await window.RenameDiagramAsync(connection, id, "Sales") && (await service.ListAsync()).Single().Name == "Renamed", "Rename changes the diagram name");

            // In place, as in SSMS: F2 / Rename / a slow click on the selected label; Escape keeps the name, Enter applies it.
            tree.Items.Clear();
            var folder = new CatalogNode(new CatalogEntry(CatalogKind.DatabaseDiagrams, "Database Diagrams", database), catalog) { IsExpanded = true };
            tree.Items.Add(folder); await folder.LoadAsync(); await Idle();
            var diagramNode = folder.Children.Single(n => n.Entry.IsDiagram);
            diagramNode.IsSelected = true; tree.Focus(); await Idle();
            TreeViewItem ItemOf(CatalogNode node) => (TreeViewItem)((TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(folder)).ItemContainerGenerator.ContainerFromItem(node);
            static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is T found && match(found)) return found;
                    if (Find(child, match) is { } deeper) return deeper;
                }
                return null;
            }
            void Press(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            TextBox Box() => Find<TextBox>(ItemOf(diagramNode), b => b.Name == "ExplorerRenameBox")!;
            Press(ItemOf(diagramNode), Key.F2); await Idle();
            check(diagramNode.IsRenaming && Box().IsVisible && Box().IsKeyboardFocused && Box().SelectedText == "Renamed", "F2 edits the diagram name in the tree, with the name selected");
            Box().Text = "Ignored"; Press(Box(), Key.Escape); await Idle();
            check(!diagramNode.IsRenaming && (await service.ListAsync()).Single().Name == "Renamed" && diagramNode.IsSelected, "Escape leaves the name as it was");

            var label = Find<TextBlock>(ItemOf(diagramNode), t => t.Text == "Renamed")!;
            MouseButtonEventArgs Click(int count)
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = label };
                typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.GetSetMethod(true)!.Invoke(args, [count]);
                return args;
            }
            label.RaiseEvent(Click(1)); label.RaiseEvent(Click(2));
            await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 300)); await Idle();
            check(!diagramNode.IsRenaming, "A double click does not start the rename");
            label.RaiseEvent(Click(1)); await Idle();
            check(!diagramNode.IsRenaming, "The rename waits for the double click time, so a double click can still open the diagram");
            await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 300)); await Idle();
            check(diagramNode.IsRenaming && Box().IsKeyboardFocused, "A slow click on the selected diagram label starts the rename");
            Box().Text = "  Quarterly  "; Press(Box(), Key.Enter);
            CatalogNode? renamedNode = null;
            for (int i = 0; i < 100 && (renamedNode = folder.Children.SingleOrDefault(n => n.Entry.IsDiagram)) is not { Name: "Quarterly", IsSelected: true }; i++) await Task.Delay(50);
            await Idle();
            check((await service.ListAsync()).Single().Name == "Quarterly" && renamedNode?.Name == "Quarterly" && renamedNode.IsSelected && !renamedNode.IsRenaming, "Enter renames the diagram, trims the name and keeps it selected in the tree");
            window.DiagramNamePrompt = (_, _) => "Renamed";
            check(await window.RenameDiagramAsync(connection, id, "Quarterly"), "The diagram name goes back for the next checks");
            await DiagramEditingChecks.RunAsync(window, connection, check);
            await DiagramIndexUiChecks.RunAsync(window, connection, check);
            await ViewDesignerChecks.RunAsync(window, connection, check);
            await QueryDesignerChecks.RunAsync(window, connection, check);
            check(await window.DeleteDiagramAsync(connection, id, "Renamed") && (await service.ListAsync()).Count == 0 && await Scalar("SELECT to_regclass('public.customers') IS NOT NULL") is true, "Delete removes the diagram and keeps its tables");
        }
        finally
        {
            window.DiagramClosePrompt = _ => MessageBoxResult.No;
            foreach (var open in window.DatabaseDiagrams.ToArray()) await window.CloseDiagramAsync(open);
            window.Close();
        }
    }
}
