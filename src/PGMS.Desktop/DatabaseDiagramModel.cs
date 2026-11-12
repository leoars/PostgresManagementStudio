using System.Windows;
using PGMS.PostgreSql;
using Side = PGMS.Desktop.DiagramSide;

namespace PGMS.Desktop;

public enum DiagramSide { Left, Right, Top, Bottom }

/// <summary>A relationship line placed by hand. SSMS 22.11 lets you drag each segment of a line sideways (pilotagem de 07/11/2026, docs/MARCO-M1043.md):
/// an end segment slides its end along the side of the table and, dragged past a corner, moves the end to the next side; the other segments stay
/// where they were put, also when a table moves. Each end is a side of its table and a distance along it from the top or left end, so it follows the
/// table. Bends are the positions of the segments between the end segments, alternately x and y, starting with the axis the line leaves the key table on.</summary>
public sealed record DiagramManualRoute(DiagramSide PrimarySide, double PrimaryOffset, DiagramSide ForeignSide, double ForeignOffset, IReadOnlyList<double> Bends);

/// <summary>What a table shows in a diagram (Table View of SSMS 22.11, capture m1b of M1043). What each mode shows is inferred: SSMS was not piloted for it.</summary>
public enum DiagramTableView { ColumnNames, Standard, Keys, NameOnly, Custom }

/// <summary>The fields a column can show in the Standard and Custom views.</summary>
public static class DiagramColumnFields
{
    public const string Name = "Column Name", DataType = "Data Type", AllowNulls = "Allow Nulls", DefaultValue = "Default Value", Identity = "Identity", Collation = "Collation", Description = "Description";
    public static readonly string[] All = [DataType, AllowNulls, DefaultValue, Identity, Collation, Description], Standard = [DataType, AllowNulls];
    public static bool IsEditable(string field) => field is Name or DataType or DefaultValue or Description;
    public static string Text(DesignColumn column, string field) => field switch
    {
        Name => column.Name, DataType => column.Type, DefaultValue => column.Default ?? "", Collation => column.Collation ?? "", Description => column.Comment ?? "",
        Identity => column.Identity switch { ColumnIdentity.Always => "Always", ColumnIdentity.ByDefault => "By Default", _ => "" },
        _ => ""
    };
}

/// <summary>A text annotation on a diagram. It belongs to the layout, not to any table.</summary>
public sealed class DiagramAnnotation(string text, double x, double y)
{
    public string Text { get; set; } = text;
    public double X { get; set; } = x;
    public double Y { get; set; } = y;
}

/// <summary>One table shown in a diagram: the table as read, the keys, indexes and checks the diagram can change, and its position.</summary>
public sealed class DiagramTableModel
{
    private string baseline = "";
    public TableDesign Original { get; private set; }
    /// <summary>The columns as edited in Table Properties (name, type, nulls); saved with the diagram.</summary>
    public List<DesignColumn> Columns { get; } = [];
    public List<DesignerIndexRow> Indexes { get; } = [];
    public List<DesignerForeignKeyRow> ForeignKeys { get; } = [];
    public List<DesignerCheckRow> Checks { get; } = [];
    public double X { get; set; }
    public double Y { get; set; }
    public DiagramTableView View { get; set; }
    public List<string> CustomColumns { get; } = [.. DiagramColumnFields.Standard];
    /// <summary>The indexes of the table as the catalog has them (PGMS-044): what the Indexes section shows. Pending changes of Indexes/Keys are not here until saved.</summary>
    public DiagramTableIndexes Catalog { get; set; } = DiagramTableIndexes.Empty;
    /// <summary>The Indexes section is open; it starts closed (decisão do usuário, 11/11/2026).</summary>
    public bool IndexesExpanded { get; set; }
    /// <summary>The description of the table (COMMENT ON TABLE) as edited in the diagram (PGMS-045); Save writes it.</summary>
    public string? Comment { get; set; }
    /// <summary>The user owns the table (or is a member of its owner), so PostgreSQL lets them change its descriptions.</summary>
    public bool CanComment { get; set; } = true;
    /// <summary>The copy of the descriptions saved in the diagram, as the diagram was opened or last saved; null when the diagram had none (older format, or not saved).</summary>
    public IReadOnlyList<DiagramDescription>? SavedDescriptions { get; set; }
    /// <summary>Descriptions the user could not apply (not the owner), kept in the diagram until the catalog has them or the user discards them.</summary>
    public List<DiagramDescription> Unapplied { get; } = [];

    public DiagramTableModel(TableDesign design, double x, double y) { Original = design; X = x; Y = y; Reset(design); }

    public string Schema => Original.Schema;
    public string Name => Original.Name;
    public string Key => Schema + "." + Name;
    /// <summary>SSMS shows the schema in parentheses for tables outside the default schema (inferred for PostgreSQL's public).</summary>
    public string Caption => Schema == "public" ? Name : $"{Name} ({Schema})";
    public bool IsReadOnly => Original.ReadOnlyReason is not null;
    /// <summary>Made with New Table in this diagram and not saved yet.</summary>
    public bool IsNew => Original.Oid == 0;
    public IReadOnlyList<string> Fields => View switch { DiagramTableView.Standard => DiagramColumnFields.Standard, DiagramTableView.Custom => CustomColumns, _ => [] };
    /// <summary>The rows of the current Table View. Keys shows the columns of the keys and of every index, as SSMS 22.11 does (pilotagem de 11/11/2026,
    /// docs/MARCO-M1061.md: a column used only in the WHERE of a filtered index stays out); the columns of the foreign keys are inferred.</summary>
    public IReadOnlyList<DesignColumn> VisibleColumns
    {
        get
        {
            if (View == DiagramTableView.NameOnly) return [];
            if (View != DiagramTableView.Keys) return Columns;
            var keys = Indexes.SelectMany(i => i.Columns.Select(c => c.Name)).Concat(ForeignKeys.SelectMany(k => k.Columns)).ToHashSet(StringComparer.Ordinal);
            return Columns.Where(c => keys.Contains(c.Name)).ToArray();
        }
    }
    public IReadOnlyList<string> PrimaryKey => Indexes.FirstOrDefault(i => i.Kind == DesignIndexKind.PrimaryKey)?.Columns.Select(c => c.Name).ToArray() ?? [];
    public DesignColumn? Column(string name) => Columns.FirstOrDefault(c => c.Name == name);

    /// <summary>Takes the table as read again; pending changes are dropped.</summary>
    public void Reset(TableDesign design)
    {
        Original = design;
        Comment = design.Comment;
        Columns.Clear(); Indexes.Clear(); ForeignKeys.Clear(); Checks.Clear();
        Columns.AddRange(design.Columns);
        Indexes.AddRange(design.Indexes.Select(i => new DesignerIndexRow(i)));
        ForeignKeys.AddRange(design.ForeignKeys.Select(f => new DesignerForeignKeyRow(f)));
        Checks.AddRange(design.Checks.Select(c => new DesignerCheckRow(c)));
        baseline = Current().Fingerprint;
    }

    public TableDesign Current() => Original with
    {
        Comment = Comment, Columns = Columns.ToArray(), Indexes = Indexes.Select(i => i.ToDesign()).ToArray(), ForeignKeys = ForeignKeys.Select(f => f.ToDesign()).ToArray(), Checks = Checks.Select(c => c.ToDesign()).ToArray()
    };
    public bool SchemaChanged => IsNew || Current().Fingerprint != baseline;
    public string UniqueName(string name) => DesignerObjectKinds.UniqueName(name, Indexes, ForeignKeys, Checks);
    public bool References(DiagramTableModel other, DesignerForeignKeyRow key) => key.ReferencedSchema == other.Schema && key.ReferencedTable == other.Name;
}

/// <summary>A foreign key drawn between two tables of the diagram. Primary is the referenced table (key end), Foreign the table that owns the key (∞ end).</summary>
public sealed record DiagramRelation(DiagramTableModel Foreign, DesignerForeignKeyRow Key, DiagramTableModel Primary);

/// <summary>Arrange Tables (pedido do usuário, 08/11/2026): tables in a grid of aligned columns, so the gaps between columns and rows are free corridors for the lines,
/// in the order that gives the fewest lines behind or against tables they do not connect, then the fewest crossing lines, then the shortest lines.
/// The order starts with linked tables together and improves by swapping pairs while there is time.</summary>
public static class DiagramArrange
{
    public const double Spacing = 60;

    /// <summary>Segments of lines running behind, or closer than the routing clearance to, a table the line does not connect. Links are pairs of indexes into boxes, in the order of routes.</summary>
    public static int Blocked(IReadOnlyList<DiagramRouting.Route> routes, IReadOnlyList<Rect> boxes, IReadOnlyList<(int Primary, int Foreign)> links)
    {
        int blocked = 0;
        for (int i = 0; i < routes.Count; i++)
            foreach (var (a, b) in routes[i].Points.Zip(routes[i].Points.Skip(1)))
                for (int t = 0; t < boxes.Count; t++)
                    if (t != links[i].Primary && t != links[i].Foreign && DiagramRouting.Near(boxes[t]).IntersectsWith(new Rect(a, b))) blocked++;
        return blocked;
    }

    /// <summary>Crossings between segments of different lines: a horizontal segment and a vertical one cutting through each other.</summary>
    public static int Crossings(IReadOnlyList<DiagramRouting.Route> routes)
    {
        var segments = routes.SelectMany((r, i) => r.Points.Zip(r.Points.Skip(1), (a, b) => (Route: i, A: a, B: b))).Where(s => s.A != s.B).ToArray();
        int count = 0;
        foreach (var h in segments.Where(s => Math.Abs(s.A.Y - s.B.Y) < 0.5))
            foreach (var v in segments.Where(s => Math.Abs(s.A.X - s.B.X) < 0.5 && s.Route != h.Route))
            {
                double x = v.A.X, y = h.A.Y;
                if (x > Math.Min(h.A.X, h.B.X) + 0.5 && x < Math.Max(h.A.X, h.B.X) - 0.5 && y > Math.Min(v.A.Y, v.B.Y) + 0.5 && y < Math.Max(v.A.Y, v.B.Y) - 0.5) count++;
            }
        return count;
    }

    /// <summary>Positions for tables of the given sizes; links are pairs of indexes (key table, foreign key table).</summary>
    public static Point[] Arrange(IReadOnlyList<Size> sizes, IReadOnlyList<(int Primary, int Foreign)> links, double left, double top, int budgetMilliseconds = 1000)
    {
        int n = sizes.Count;
        if (n == 0) return [];
        var keys = Enumerable.Range(0, n).Select(i => (object)i).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Columns as wide as their widest table: without this, the gaps of one row end against the tables of the next and a line has nowhere to pass.
        Point[] Place(int[] order, int perRow, double gap = Spacing)
        {
            var widths = new double[perRow];
            for (int k = 0; k < n; k++) widths[k % perRow] = Math.Max(widths[k % perRow], sizes[order[k]].Width);
            var at = new Point[n]; double y = top;
            for (int start = 0; start < n; start += perRow)
            {
                double x = left, height = 0;
                for (int k = start; k < Math.Min(n, start + perRow); k++)
                {
                    int t = order[k]; at[t] = new(x, y); x += widths[k - start] + gap; height = Math.Max(height, sizes[t].Height);
                }
                y += height + gap;
            }
            return at;
        }
        (int Blocked, int Crossings, double Length) Cost(Point[] at)
        {
            var boxes = Enumerable.Range(0, n).Select(i => new Rect(at[i], sizes[i])).ToArray();
            var routes = DiagramRouting.Layout(links.Select(l => new DiagramRouting.Request(keys[l.Primary], boxes[l.Primary], keys[l.Foreign], boxes[l.Foreign])).ToArray(), boxes, detours: false);
            double length = routes.Sum(r => r.Points.Zip(r.Points.Skip(1), (a, b) => (b - a).Length).Sum());
            return (Blocked(routes, boxes, links), Crossings(routes), length);
        }
        // A line hidden by a table reads as a different relationship, so it weighs more than a crossing, which can still be followed.
        static bool Better((int B, int C, double L) a, (int B, int C, double L) b) => a.B != b.B ? a.B < b.B : a.C != b.C ? a.C < b.C : a.L < b.L - 0.5;

        // Linked tables next to each other: walk the links from the most linked table, then the tables left over.
        var neighbors = Enumerable.Range(0, n).Select(i => links.Where(l => l.Primary == i || l.Foreign == i).Select(l => l.Primary == i ? l.Foreign : l.Primary).Where(o => o != i).Distinct().ToList()).ToArray();
        var walked = new List<int>(); var seen = new bool[n];
        foreach (int root in Enumerable.Range(0, n).OrderByDescending(i => neighbors[i].Count).ThenBy(i => i))
        {
            if (seen[root]) continue;
            var queue = new Queue<int>([root]); seen[root] = true;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue(); walked.Add(t);
                foreach (int o in neighbors[t].OrderByDescending(o => neighbors[o].Count).ThenBy(o => o)) if (!seen[o]) { seen[o] = true; queue.Enqueue(o); }
            }
        }

        int baseRow = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        int[]? bestOrder = null; int bestRow = baseRow; (int, int, double) best = (int.MaxValue, int.MaxValue, double.MaxValue);
        var tries = (from perRow in new[] { baseRow, baseRow + 1, baseRow - 1 }.Where(r => r >= 1 && r <= n).Distinct()
                     from start in new[] { walked.ToArray(), Enumerable.Range(0, n).ToArray() }
                     select (perRow, start)).ToArray();
        for (int attempt = 0; attempt < tries.Length; attempt++)
            {
                var (perRow, start) = tries[attempt];
                long deadline = (long)budgetMilliseconds * (attempt + 1) / tries.Length;
                var order = (int[])start.Clone(); var cost = Cost(Place(order, perRow));
                // Swap pairs while it helps and there is time; each start gets its share of the time.
                for (bool improved = true; improved && clock.ElapsedMilliseconds < deadline;)
                {
                    improved = false;
                    for (int i = 0; i < n && clock.ElapsedMilliseconds < deadline; i++)
                        for (int j = i + 1; j < n; j++)
                        {
                            (order[i], order[j]) = (order[j], order[i]);
                            var tried = Cost(Place(order, perRow));
                            if (Better(tried, cost)) { cost = tried; improved = true; }
                            else (order[i], order[j]) = (order[j], order[i]);
                        }
                }
                if (bestOrder is null || Better(cost, best)) { best = cost; bestOrder = order; bestRow = perRow; }
            }
        // Many lines can need more corridor than the normal gap has: widen it only when that clears lines or crossings.
        var result = Place(bestOrder!, bestRow);
        foreach (double gap in new[] { Spacing * 1.5, Spacing * 2 })
        {
            var wider = Place(bestOrder!, bestRow, gap);
            var cost = Cost(wider);
            if (cost.Blocked < best.Item1 || cost.Blocked == best.Item1 && cost.Crossings < best.Item2) { best = cost; result = wider; }
        }
        return result;
    }
}

/// <summary>Orthogonal lines between the borders of tables, as SSMS draws relationships: not between columns, and never on top of each other.
/// All relationships are laid out together: the ends on one side of a table are spread along that side, ordered by where the other table is,
/// and each middle segment gets its own channel.</summary>
public static class DiagramRouting
{
    public sealed record Route(IReadOnlyList<Point> Points, Vector PrimaryOut, Vector ForeignOut);
    /// <summary>One relationship to route. The table objects identify the boxes, so ends on the same side of the same table share that side.</summary>
    public sealed record Request(object PrimaryTable, Rect Primary, object ForeignTable, Rect Foreign);

    private const double Gap = 16, Margin = 8, Spacing = 8;
    /// <summary>The distance a line keeps from a table it does not connect, so it does not look glued to it: the double line is 4 wide.</summary>
    private const double Clearance = 8;

    /// <summary>A table with the room a line should leave around it. Exactly at the clearance is fine.</summary>
    public static Rect Near(Rect table) { table.Inflate(Clearance - 0.5, Clearance - 0.5); return table; }

    /// <summary>Obstacles are the boxes of every table: a line avoids passing behind or against another table when there is room.</summary>
    /// <param name="detours">Whether a line with no clear corridor goes around the tables in its way (pedido do usuário, 08/11/2026). Arrange Tables searches without it, so it prefers orders that need no detour.</param>
    public static IReadOnlyList<Route> Layout(IReadOnlyList<Request> requests, IReadOnlyList<Rect>? obstacles = null, bool detours = true)
    {
        int n = requests.Count;
        var horizontal = new bool[n]; var primarySide = new Side[n]; var foreignSide = new Side[n];
        for (int i = 0; i < n; i++)
        {
            var (p, f) = (requests[i].Primary, requests[i].Foreign);
            if (ReferenceEquals(requests[i].PrimaryTable, requests[i].ForeignTable)) { horizontal[i] = true; primarySide[i] = foreignSide[i] = Side.Right; }
            else if (p.Right + Gap <= f.Left || f.Right + Gap <= p.Left)
            {
                horizontal[i] = true; bool primaryLeft = p.Right + Gap <= f.Left;
                primarySide[i] = primaryLeft ? Side.Right : Side.Left; foreignSide[i] = primaryLeft ? Side.Left : Side.Right;
            }
            else
            {
                bool primaryAbove = p.Top + p.Height / 2 <= f.Top + f.Height / 2;
                primarySide[i] = primaryAbove ? Side.Bottom : Side.Top; foreignSide[i] = primaryAbove ? Side.Top : Side.Bottom;
            }
        }

        // Ends grouped by table and side, then spread along the side in the order of the other end, so lines leaving one side do not cross.
        var at = new double[n, 2]; // along-side coordinate of the primary (0) and foreign (1) end
        var ends = new List<(object Table, Side Side, Rect Box, int Index, int End, double Order)>();
        for (int i = 0; i < n; i++)
        {
            var r = requests[i];
            bool self = ReferenceEquals(r.PrimaryTable, r.ForeignTable);
            double Along(Rect box, Side side) => side is Side.Left or Side.Right ? box.Top + box.Height / 2 : box.Left + box.Width / 2;
            ends.Add((r.PrimaryTable, primarySide[i], r.Primary, i, 0, self ? double.MinValue + i : Along(r.Foreign, primarySide[i])));
            ends.Add((r.ForeignTable, foreignSide[i], r.Foreign, i, 1, self ? double.MinValue + i + 0.5 : Along(r.Primary, foreignSide[i])));
        }
        var groupSize = new Dictionary<(object, Side), int>();
        foreach (var group in ends.GroupBy(e => (e.Table, e.Side)))
        {
            var ordered = group.OrderBy(e => e.Order).ThenBy(e => e.Index).ThenBy(e => e.End).ToArray();
            groupSize[group.Key] = ordered.Length;
            var box = ordered[0].Box; bool vertical = group.Key.Side is Side.Left or Side.Right;
            double start = (vertical ? box.Top : box.Left) + Margin, length = Math.Max(0, (vertical ? box.Height : box.Width) - 2 * Margin);
            for (int k = 0; k < ordered.Length; k++) at[ordered[k].Index, ordered[k].End] = start + (k + 0.5) * length / ordered.Length;
        }
        // A relationship alone on both sides goes straight across where the tables overlap, as before.
        for (int i = 0; i < n; i++)
        {
            var r = requests[i];
            if (ReferenceEquals(r.PrimaryTable, r.ForeignTable) || groupSize[(r.PrimaryTable, primarySide[i])] != 1 || groupSize[(r.ForeignTable, foreignSide[i])] != 1) continue;
            double low = horizontal[i] ? Math.Max(r.Primary.Top, r.Foreign.Top) : Math.Max(r.Primary.Left, r.Foreign.Left);
            double high = horizontal[i] ? Math.Min(r.Primary.Bottom, r.Foreign.Bottom) : Math.Min(r.Primary.Right, r.Foreign.Right);
            if (high - low > 24) at[i, 0] = at[i, 1] = (low + high) / 2;
        }

        // Ends a few pixels apart would leave a tiny step: the end alone on its side moves to line up with the other, when it stays on its table.
        for (int i = 0; i < n; i++)
        {
            var r = requests[i];
            if (ReferenceEquals(r.PrimaryTable, r.ForeignTable) || at[i, 0] == at[i, 1] || Math.Abs(at[i, 0] - at[i, 1]) > 2 * Spacing) continue;
            bool Fits(Rect box, Side side, double value) => side is Side.Left or Side.Right ? value > box.Top + 4 && value < box.Bottom - 4 : value > box.Left + 4 && value < box.Right - 4;
            if (groupSize[(r.ForeignTable, foreignSide[i])] == 1 && Fits(r.Foreign, foreignSide[i], at[i, 0])) at[i, 1] = at[i, 0];
            else if (groupSize[(r.PrimaryTable, primarySide[i])] == 1 && Fits(r.Primary, primarySide[i], at[i, 1])) at[i, 0] = at[i, 1];
        }

        // Middle channels: a vertical (for side-to-side lines) or horizontal segment never runs on another one.
        var channel = new double[n];
        var around = new bool[n];
        var placed = new List<(bool Vertical, double At, double From, double To)>();
        double Edge(Rect box, Side side) => side switch { Side.Left => box.Left, Side.Right => box.Right, Side.Top => box.Top, _ => box.Bottom };
        foreach (int i in Enumerable.Range(0, n).OrderBy(i => ReferenceEquals(requests[i].PrimaryTable, requests[i].ForeignTable) ? 1 : 0).ThenBy(i => i))
        {
            var r = requests[i];
            bool self = ReferenceEquals(r.PrimaryTable, r.ForeignTable);
            double ep = Edge(r.Primary, primarySide[i]), ef = Edge(r.Foreign, foreignSide[i]);
            double from = Math.Min(at[i, 0], at[i, 1]), to = Math.Max(at[i, 0], at[i, 1]);
            double ideal = self ? ep + 22 : (ep + ef) / 2, low = self ? ep + 14 : Math.Min(ep, ef) + 6, high = self ? ep + 400 : Math.Max(ep, ef) - 6;
            bool Free(double c) => !placed.Any(s => s.Vertical == horizontal[i] && Math.Abs(s.At - c) < Spacing - 0.5 && s.From < to + Spacing && from < s.To + Spacing);
            Point Across(double along, double c) => horizontal[i] ? new(c, along) : new(along, c);
            bool Open(Point a, Point b) => obstacles is null || !obstacles.Any(o => o != r.Primary && o != r.Foreign && Near(o).IntersectsWith(new Rect(a, b)));
            bool MiddleOpen(double c) => Open(Across(at[i, 0], c), Across(at[i, 1], c));
            bool WholeOpen(double c) => MiddleOpen(c) && Open(Across(at[i, 0], ep), Across(at[i, 0], c)) && Open(Across(at[i, 1], c), Across(at[i, 1], ef));
            double chosen = ideal;
            bool bent = at[i, 0] != at[i, 1] || self;
            if (bent)
            {
                bool found = false;
                // First a channel that keeps the whole line clear of other tables, then one that keeps at least its middle clear, then any that does not run on another line.
                foreach (Func<double, bool> acceptable in new Func<double, bool>[] { WholeOpen, MiddleOpen, _ => true })
                {
                    for (int step = 0; step < 400 && !found; step++)
                    {
                        double candidate = ideal + (step % 2 == 1 ? 1 : -1) * ((step + 1) / 2) * Spacing;
                        if (self && candidate < ideal || candidate < low || candidate > high) continue;
                        if (Free(candidate) && acceptable(candidate)) { chosen = candidate; found = true; }
                    }
                    if (found) break;
                }
            }
            around[i] = detours && !self && !WholeOpen(chosen);
            if (bent && !around[i]) placed.Add((horizontal[i], chosen, from, to));
            channel[i] = chosen;
        }

        var routes = new Route[n];
        for (int i = 0; i < n; i++)
        {
            var r = requests[i];
            static Vector Out(Side side) => side switch { Side.Left => new(-1, 0), Side.Right => new(1, 0), Side.Top => new(0, -1), _ => new(0, 1) };
            double ep = Edge(r.Primary, primarySide[i]), ef = Edge(r.Foreign, foreignSide[i]), ap = at[i, 0], af = at[i, 1], c = channel[i];
            bool self = ReferenceEquals(r.PrimaryTable, r.ForeignTable);
            Point[] points = horizontal[i]
                ? ap == af && !self ? [new(ep, ap), new(ef, af)] : [new(ep, ap), new(c, ap), new(c, af), new(ef, af)]
                : ap == af ? [new(ap, ep), new(af, ef)] : [new(ap, ep), new(ap, c), new(af, c), new(af, ef)];
            routes[i] = new(points, Out(primarySide[i]), Out(foreignSide[i]));
        }
        if (around.Any(a => a)) RouteAround(routes, around, obstacles!);
        return routes;
    }

    private const double BendCost = 40, CrossingCost = 120;

    /// <summary>Lines with no clear corridor go around the tables in their way, keeping their ends: the cheapest orthogonal path, by length, bends and crossings, along tracks
    /// beside the tables, on which no two lines run together. A line that finds no path (its end has no room to leave the table) keeps the direct shape.</summary>
    private static void RouteAround(Route[] routes, bool[] around, IReadOnlyList<Rect> tables)
    {
        static Point Stub(Point end, Vector outward) => end + outward * Gap;
        var stubs = routes.Where((_, i) => around[i]).SelectMany(r => new[] { Stub(r.Points[0], r.PrimaryOut), Stub(r.Points[^1], r.ForeignOut) }).ToArray();
        double[] Tracks(Func<Rect, double> low, Func<Rect, double> high, Func<Point, double> along) => tables
            .SelectMany(t => Enumerable.Range(0, 3).SelectMany(k => new[] { low(t) - Clearance - k * Spacing, high(t) + Clearance + k * Spacing }))
            .Where(v => v >= 2).Concat(stubs.Select(along)).Distinct().OrderBy(v => v).ToArray();
        // Axis 0 is x and 1 is y. An edge joins consecutive tracks of its axis, on a track of the other axis.
        double[][] tracks = [Tracks(t => t.Left, t => t.Right, p => p.X), Tracks(t => t.Top, t => t.Bottom, p => p.Y)];
        bool[][,] closed = [new bool[tracks[0].Length, tracks[1].Length], new bool[tracks[1].Length, tracks[0].Length]];
        bool[][,] crossed = [new bool[tracks[0].Length, tracks[1].Length], new bool[tracks[1].Length, tracks[0].Length]];

        foreach (var table in tables)
        {
            var near = Near(table);
            double[] low = [near.Left, near.Top], high = [near.Right, near.Bottom];
            foreach (int axis in new[] { 0, 1 })
                for (int across = 0; across < tracks[1 - axis].Length; across++)
                    if (tracks[1 - axis][across] >= low[1 - axis] && tracks[1 - axis][across] <= high[1 - axis])
                        for (int edge = 0; edge + 1 < tracks[axis].Length; edge++)
                            if (tracks[axis][edge] <= high[axis] && tracks[axis][edge + 1] >= low[axis]) closed[axis][edge, across] = true;
        }

        // A placed segment closes the tracks too near to run beside it and marks the edges that would cut through it.
        void Occupy(Point a, Point b)
        {
            if (a == b) return;
            int axis = Math.Abs(a.Y - b.Y) < 0.01 ? 0 : 1;
            double at = axis == 0 ? a.Y : a.X, from = Math.Min(axis == 0 ? a.X : a.Y, axis == 0 ? b.X : b.Y), to = Math.Max(axis == 0 ? a.X : a.Y, axis == 0 ? b.X : b.Y);
            for (int across = 0; across < tracks[1 - axis].Length; across++)
                if (Math.Abs(tracks[1 - axis][across] - at) < Spacing - 0.5)
                    for (int edge = 0; edge + 1 < tracks[axis].Length; edge++)
                        if (tracks[axis][edge] < to && tracks[axis][edge + 1] > from) closed[axis][edge, across] = true;
            for (int along = 0; along < tracks[axis].Length; along++)
                if (tracks[axis][along] > from && tracks[axis][along] < to)
                    for (int edge = 0; edge + 1 < tracks[1 - axis].Length; edge++)
                        if (tracks[1 - axis][edge] <= at && at < tracks[1 - axis][edge + 1]) crossed[1 - axis][edge, along] = true;
        }
        void OccupyLine(IReadOnlyList<Point> points) { for (int k = 0; k + 1 < points.Count; k++) Occupy(points[k], points[k + 1]); }

        for (int i = 0; i < routes.Length; i++) if (!around[i]) OccupyLine(routes[i].Points);
        for (int i = 0; i < routes.Length; i++)
        {
            if (!around[i]) continue;
            var path = FindPath(routes[i], tracks, closed, crossed);
            if (path is null) { OccupyLine(routes[i].Points); continue; }
            routes[i] = routes[i] with { Points = path };
            OccupyLine(path);
        }
    }

    private static List<Point>? FindPath(Route route, double[][] tracks, bool[][,] closed, bool[][,] crossed)
    {
        // Directions: 0 is +x, 1 is -x, 2 is +y, 3 is -y.
        static int Direction(Vector v) => Math.Abs(v.X) > 0.5 ? v.X > 0 ? 0 : 1 : v.Y > 0 ? 2 : 3;
        int nx = tracks[0].Length, ny = tracks[1].Length;
        Point first = route.Points[0], last = route.Points[^1];
        Point start = first + route.PrimaryOut * Gap, goal = last + route.ForeignOut * Gap;
        int startX = Array.BinarySearch(tracks[0], start.X), startY = Array.BinarySearch(tracks[1], start.Y), goalX = Array.BinarySearch(tracks[0], goal.X), goalY = Array.BinarySearch(tracks[1], goal.Y);
        if (startX < 0 || startY < 0 || goalX < 0 || goalY < 0) return null;
        int leaving = Direction(route.PrimaryOut), outward = Direction(route.ForeignOut), arriving = outward ^ 1;

        int State(int x, int y, int direction) => (x * ny + y) * 4 + direction;
        var best = new double[nx * ny * 4]; Array.Fill(best, double.PositiveInfinity);
        var previous = new int[best.Length]; Array.Fill(previous, -1);
        var queue = new PriorityQueue<int, double>();
        int origin = State(startX, startY, leaving);
        best[origin] = 0; queue.Enqueue(origin, 0);
        int finish = -1; double finishCost = double.PositiveInfinity;
        while (queue.TryDequeue(out int state, out double cost))
        {
            if (cost > best[state]) continue;
            if (cost >= finishCost) break;
            int direction = state % 4, node = state / 4, x = node / ny, y = node % ny;
            if (x == goalX && y == goalY && direction != outward)
            {
                double total = cost + (direction == arriving ? 0 : BendCost);
                if (total < finishCost) { finishCost = total; finish = state; }
            }
            for (int turn = 0; turn < 4; turn++)
            {
                if (turn == (direction ^ 1)) continue; // never straight back
                int axis = turn / 2, step = turn % 2 == 0 ? 1 : -1;
                int along = axis == 0 ? x : y, across = axis == 0 ? y : x, next = along + step, edge = Math.Min(along, next);
                if (next < 0 || next >= tracks[axis].Length || closed[axis][edge, across]) continue;
                double spent = cost + Math.Abs(tracks[axis][next] - tracks[axis][along]) + (turn == direction ? 0 : BendCost) + (crossed[axis][edge, across] ? CrossingCost : 0);
                int reached = axis == 0 ? State(next, y, turn) : State(x, next, turn);
                if (spent >= best[reached]) continue;
                best[reached] = spent; previous[reached] = state; queue.Enqueue(reached, spent);
            }
        }
        if (finish < 0) return null;

        var points = new List<Point> { last };
        for (int state = finish; state >= 0; state = previous[state]) points.Add(new(tracks[0][state / 4 / ny], tracks[1][state / 4 % ny]));
        points.Add(first);
        points.Reverse();
        // Only the corners stay, so the line can be taken by hand with the same shape.
        var corners = new List<Point> { points[0] };
        for (int k = 1; k < points.Count; k++)
        {
            if (points[k] == corners[^1]) continue;
            if (corners.Count >= 2 && Collinear(corners[^2], corners[^1], points[k])) corners[^1] = points[k]; else corners.Add(points[k]);
        }
        return corners;
    }

    private static bool Collinear(Point a, Point b, Point c) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(b.X - c.X) < 0.01 || Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(b.Y - c.Y) < 0.01;

    // ---- Lines placed by hand ----
    /// <summary>What sets the position of a segment of a line placed by hand: the key end, the ∞ end, one of the bends, or both ends (a straight line).</summary>
    public enum SegmentRole { PrimaryEnd, ForeignEnd, Bend, BothEnds, BendAndForeignEnd }
    public sealed record ManualPath(DiagramManualRoute Manual, Route Route, IReadOnlyList<(SegmentRole Role, int Bend)> Segments);

    private const double EndInset = 4;
    private static bool Across(Side side) => side is Side.Left or Side.Right;
    private static Vector Outward(Side side) => side switch { Side.Left => new(-1, 0), Side.Right => new(1, 0), Side.Top => new(0, -1), _ => new(0, 1) };
    private static Side SideOf(Vector outward) => Math.Abs(outward.X) >= Math.Abs(outward.Y) ? outward.X < 0 ? Side.Left : Side.Right : outward.Y < 0 ? Side.Top : Side.Bottom;
    private static double SideStart(Rect box, Side side) => Across(side) ? box.Top : box.Left;
    private static double SideLength(Rect box, Side side) => Across(side) ? box.Height : box.Width;
    private static double EdgeOf(Rect box, Side side) => side switch { Side.Left => box.Left, Side.Right => box.Right, Side.Top => box.Top, _ => box.Bottom };
    private static double ClampOffset(Rect box, Side side, double offset)
    {
        double length = SideLength(box, side);
        return length <= 2 * EndInset ? length / 2 : Math.Clamp(offset, EndInset, length - EndInset);
    }
    private static Point Attach(Rect box, Side side, double offset)
    {
        double along = SideStart(box, side) + ClampOffset(box, side, offset);
        return Across(side) ? new(EdgeOf(box, side), along) : new(along, EdgeOf(box, side));
    }
    /// <summary>Segment k moves along x when the line leaves the key table sideways and k is even, or leaves it up or down and k is odd.</summary>
    private static bool AlongX(Side primarySide, int segment) => Across(primarySide) ^ (segment % 2 == 1);

    /// <summary>A line routed automatically, as a line placed by hand with the same shape: the start of a drag.</summary>
    public static DiagramManualRoute FromRoute(Route route, Rect primary, Rect foreign)
    {
        var points = route.Points;
        Side ps = SideOf(route.PrimaryOut), fs = SideOf(route.ForeignOut);
        double Offset(Point p, Rect box, Side side) => (Across(side) ? p.Y : p.X) - SideStart(box, side);
        var bends = new List<double>();
        for (int i = 1; i <= points.Count - 3; i++) bends.Add(AlongX(ps, i - 1) ? points[i].X : points[i].Y);
        return new(ps, Offset(points[0], primary, ps), fs, Offset(points[^1], foreign, fs), bends);
    }

    /// <summary>The points of a line placed by hand, between the tables where they are now, and what sets each segment.</summary>
    public static ManualPath Build(DiagramManualRoute manual, Rect primary, Rect foreign) => BuildAsIs(FixEnds(manual, primary, foreign), primary, foreign);

    /// <summary>An end whose last segment points back into its table (a segment dragged over the table, or a table moved past the line) moves to the side
    /// the line really reaches (pedido do usuário, 08/11/2026: CartoesFixosBolao kept the ∞ on the right while the line came in from the top):
    /// the side the line crosses when the segment before runs across the table, or the opposite side when the line comes from beyond the table.</summary>
    private static DiagramManualRoute FixEnds(DiagramManualRoute manual, Rect primary, Rect foreign)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            foreach (bool key in new[] { false, true })
            {
                var points = BuildAsIs(manual, primary, foreign).Route.Points;
                if (points.Count < 3) continue;
                Point end = key ? points[0] : points[^1], next = key ? points[1] : points[^2], before = key ? points[2] : points[^3];
                Side side = key ? manual.PrimarySide : manual.ForeignSide; Rect box = key ? primary : foreign;
                if ((next - end) * Outward(side) >= -0.5) continue;
                // The segment before the end segment runs along the side; where it crosses the table, the line comes in through the side it crosses.
                bool across = Across(side);
                double at = across ? next.X : next.Y, from = across ? before.Y : before.X;
                double low = across ? box.Left : box.Top, high = across ? box.Right : box.Bottom;
                Side turned;
                double offset;
                var bends = manual.Bends.ToList();
                // Only past the far edge does the line come from beyond the table; just inside the near edge, it is still in front of it.
                bool beyond = side is Side.Left or Side.Top ? at >= high - EndInset : at <= low + EndInset;
                double lowEdge = across ? box.Top : box.Left, highEdge = across ? box.Bottom : box.Right;
                var reached = EntryInto(points, box, key);
                // Reached by a segment before the end segment, with the bend past the far side, the line only seemed to come from beyond: it ran behind the table first.
                bool passedThrough = beyond && reached is { } first && first.Segment != (key ? 0 : points.Count - 2);
                if (passedThrough || !beyond && from >= lowEdge && from <= highEdge)
                {
                    // The bends ended up behind or past the table (it moved over them; pedido do usuário, 08/11/2026: JogosUsuariosAndamento): the end goes to where the line reaches the table.
                    // Only the drawing changes: with the table moved away, the line placed by hand has its shape again.
                    var built = BuildAsIs(manual, primary, foreign);
                    if (reached is not { } entry) continue;
                    bends = (key ? built.Manual.Bends.Skip(entry.Segment) : built.Manual.Bends.Take(entry.Segment)).ToList();
                    manual = key ? manual with { PrimarySide = entry.Side, PrimaryOffset = entry.Offset, Bends = bends } : manual with { ForeignSide = entry.Side, ForeignOffset = entry.Offset, Bends = bends };
                    changed = true;
                    continue;
                }
                if (!beyond)
                {
                    if (from < lowEdge) turned = across ? Side.Top : Side.Left;
                    else turned = across ? Side.Bottom : Side.Right;
                    offset = at - (across ? box.Left : box.Top);
                    // The end segment and its bend go away: the segment before now runs into the table.
                    if (bends.Count == 0) continue;
                    if (key) bends.RemoveAt(0); else bends.RemoveAt(bends.Count - 1);
                }
                else
                {
                    // The line comes from beyond the table: the end goes to the opposite side, at the same place along it.
                    turned = side switch { Side.Left => Side.Right, Side.Right => Side.Left, Side.Top => Side.Bottom, _ => Side.Top };
                    offset = key ? manual.PrimaryOffset : manual.ForeignOffset;
                }
                manual = key ? manual with { PrimarySide = turned, PrimaryOffset = offset, Bends = bends } : manual with { ForeignSide = turned, ForeignOffset = offset, Bends = bends };
                changed = true;
            }
            if (!changed) break;
        }
        return manual;
    }

    /// <summary>Walking the line from its other end, the first place it goes into this table: the segment, the side it crosses and the distance along that side.</summary>
    private static (int Segment, Side Side, double Offset)? EntryInto(IReadOnlyList<Point> points, Rect box, bool key)
    {
        int count = points.Count - 1;
        for (int step = 0; step < count; step++)
        {
            int k = key ? count - 1 - step : step;
            Point from = key ? points[k + 1] : points[k], to = key ? points[k] : points[k + 1];
            bool horizontal = Math.Abs(from.Y - to.Y) < 0.5;
            double along = horizontal ? from.Y : from.X, start = horizontal ? box.Top : box.Left, finish = horizontal ? box.Bottom : box.Right;
            if (along <= start || along >= finish) continue;
            double near = horizontal ? box.Left : box.Top, far = horizontal ? box.Right : box.Bottom, a = horizontal ? from.X : from.Y, b = horizontal ? to.X : to.Y;
            if (a <= near && b > near + 0.5) return (k, horizontal ? Side.Left : Side.Top, along - start);
            if (a >= far && b < far - 0.5) return (k, horizontal ? Side.Right : Side.Bottom, along - start);
        }
        return null;
    }

    private static ManualPath BuildAsIs(DiagramManualRoute manual, Rect primary, Rect foreign)
    {
        var a = Attach(primary, manual.PrimarySide, manual.PrimaryOffset);
        var b = Attach(foreign, manual.ForeignSide, manual.ForeignOffset);
        bool foreignX = Across(manual.ForeignSide);
        // Bends with the wrong parity cannot reach the ∞ end along its axis: a bend halfway is added, unless the line is straight.
        var bends = manual.Bends.ToList();
        Point End(IReadOnlyList<double> list)
        {
            var p = a;
            for (int j = 0; j < list.Count; j++) p = AlongX(manual.PrimarySide, j) ? new(list[j], p.Y) : new(p.X, list[j]);
            return p;
        }
        var last = End(bends);
        bool lastAlongX = AlongX(manual.PrimarySide, bends.Count);
        // The last segment already lines up with the ∞ end: it runs straight into the table (a merged step, or a straight line).
        bool straight = lastAlongX == foreignX && Math.Abs(foreignX ? last.Y - b.Y : last.X - b.X) < 0.5;
        if (lastAlongX == foreignX && !straight)
        {
            bends.Add(lastAlongX ? (last.X + b.X) / 2 : (last.Y + b.Y) / 2);
            last = End(bends); lastAlongX = !lastAlongX;
        }
        manual = manual with { Bends = bends };

        var points = new List<Point> { a };
        var roles = new List<(SegmentRole, int)>();
        for (int j = 0; j < bends.Count; j++)
        {
            var p = points[^1];
            points.Add(AlongX(manual.PrimarySide, j) ? new(bends[j], p.Y) : new(p.X, bends[j]));
            roles.Add(j == 0 ? (SegmentRole.PrimaryEnd, -1) : (SegmentRole.Bend, j - 1));
        }
        if (straight) { points.Add(b); roles.Add(bends.Count == 0 ? (SegmentRole.BothEnds, -1) : (SegmentRole.BendAndForeignEnd, bends.Count - 1)); }
        else
        {
            points.Add(lastAlongX ? new(b.X, last.Y) : new(last.X, b.Y));
            roles.Add(bends.Count == 0 ? (SegmentRole.PrimaryEnd, -1) : (SegmentRole.Bend, bends.Count - 1));
            points.Add(b); roles.Add((SegmentRole.ForeignEnd, -1));
        }
        return new(manual, new Route(points, Outward(manual.PrimarySide), Outward(manual.ForeignSide)), roles);
    }

    /// <summary>The segment of a line nearest to a point.</summary>
    public static int SegmentAt(IReadOnlyList<Point> points, Point at)
    {
        int best = 0; double distance = double.MaxValue;
        for (int k = 0; k + 1 < points.Count; k++)
        {
            Point p = points[k], q = points[k + 1];
            var d = q - p; double length = d.LengthSquared;
            double t = length == 0 ? 0 : Math.Clamp(((at - p) * d) / length, 0, 1);
            double here = (at - (p + t * d)).Length;
            if (here < distance - 0.01) { distance = here; best = k; }
        }
        return best;
    }

    /// <summary>Whether segment k of a line runs along x (a horizontal segment, dragged up or down).</summary>
    public static bool IsHorizontal(DiagramManualRoute manual, int segment) => AlongX(manual.PrimarySide, segment);

    /// <summary>Drags segment k of the line sideways to the given point, as SSMS does: a bend moves, an end slides along its side and,
    /// past a corner, goes to the next side with the dragged segment kept where it was dropped.</summary>
    /// <param name="snap">Distance, in diagram units, within which the segment lines up with a parallel segment two steps away, so the step between them goes away (pedido do usuário, 08/11/2026).</param>
    public static DiagramManualRoute DragSegment(DiagramManualRoute manual, Rect primary, Rect foreign, int segment, Point to, double snap = 0)
    {
        var path = Build(manual, primary, foreign);
        manual = path.Manual;
        if (segment < 0 || segment >= path.Segments.Count) return manual;
        var (role, bend) = path.Segments[segment];
        bool horizontal = AlongX(manual.PrimarySide, segment);
        double value = horizontal ? to.Y : to.X;
        var points = path.Route.Points;
        foreach (int other in new[] { segment - 2, segment + 2 })
        {
            if (other < 0 || other + 1 >= points.Count) continue;
            double position = horizontal ? points[other].Y : points[other].X;
            if (Math.Abs(position - value) <= snap) { value = position; break; }
        }
        return Simplify(Dragged(), primary, foreign);

        DiagramManualRoute Dragged()
        {
        switch (role)
        {
            case SegmentRole.Bend:
                return manual with { Bends = manual.Bends.Select((c, j) => j == bend ? value : c).ToArray() };
            case SegmentRole.BendAndForeignEnd:
                return manual with { Bends = manual.Bends.Select((c, j) => j == bend ? value : c).ToArray(), ForeignOffset = value - SideStart(foreign, manual.ForeignSide) };
            case SegmentRole.BothEnds:
                return manual with { PrimaryOffset = value - SideStart(primary, manual.PrimarySide), ForeignOffset = value - SideStart(foreign, manual.ForeignSide) };
            default:
                bool key = role == SegmentRole.PrimaryEnd;
                Rect box = key ? primary : foreign; Side side = key ? manual.PrimarySide : manual.ForeignSide;
                double offset = value - SideStart(box, side), length = SideLength(box, side);
                if (offset >= EndInset && offset <= length - EndInset)
                    return key ? manual with { PrimaryOffset = offset } : manual with { ForeignOffset = offset };
                // Past a corner: the end goes to the side at that corner, next to it, and the dragged segment becomes a bend.
                Side next = Across(side) ? offset < EndInset ? Side.Top : Side.Bottom : offset < EndInset ? Side.Left : Side.Right;
                double corner = side is Side.Left or Side.Top ? EndInset : SideLength(box, next) - EndInset;
                return key
                    ? manual with { PrimarySide = next, PrimaryOffset = corner, Bends = [value, .. manual.Bends] }
                    : manual with { ForeignSide = next, ForeignOffset = corner, Bends = [.. manual.Bends, value] };
        }
        }
    }

    /// <summary>Removes the steps of zero height: when a segment lines up with the parallel segment two steps away, the short segment between
    /// them goes away and the two become one.</summary>
    public static DiagramManualRoute Simplify(DiagramManualRoute manual, Rect primary, Rect foreign)
    {
        var bends = manual.Bends.ToList();
        double first = SideStart(primary, manual.PrimarySide) + ClampOffset(primary, manual.PrimarySide, manual.PrimaryOffset);
        double lastEnd = SideStart(foreign, manual.ForeignSide) + ClampOffset(foreign, manual.ForeignSide, manual.ForeignOffset);
        bool foreignX = Across(manual.ForeignSide);
        // Position of segment k: the key end, the bends, then the ∞ end (when the bends reach it with a turn).
        for (bool changed = true; changed;)
        {
            changed = false;
            double Position(int i) => i == 0 ? first : i <= bends.Count ? bends[i - 1] : lastEnd;
            for (int k = 1; k <= bends.Count && !changed; k++)
            {
                // Segment k has no length when the segments before and after it sit at the same place.
                if (k == bends.Count && AlongX(manual.PrimarySide, bends.Count) == foreignX) continue;
                if (Math.Abs(Position(k + 1) - Position(k - 1)) >= 0.5) continue;
                if (k < bends.Count) bends.RemoveRange(k - 1, 2); else bends.RemoveAt(k - 1);
                changed = true;
            }
        }
        return manual with { Bends = bends };
    }

    /// <summary>Drags the key or ∞ end to any side of its table (pedido do usuário, 08/11/2026): the end goes to the nearest side, where it was dropped.
    /// When the side turns the line, a short segment leaves the table before the old bends.</summary>
    public static DiagramManualRoute MoveEnd(DiagramManualRoute manual, Rect primary, Rect foreign, bool keyEnd, Point to)
    {
        manual = Build(manual, primary, foreign).Manual;
        Rect box = keyEnd ? primary : foreign;
        var distances = new (Side Side, double Distance)[]
        {
            (Side.Left, Math.Abs(to.X - box.Left)), (Side.Right, Math.Abs(to.X - box.Right)), (Side.Top, Math.Abs(to.Y - box.Top)), (Side.Bottom, Math.Abs(to.Y - box.Bottom)),
        };
        Side side = distances.MinBy(d => d.Distance).Side;
        double offset = ClampOffset(box, side, (Across(side) ? to.Y : to.X) - SideStart(box, side));
        Side old = keyEnd ? manual.PrimarySide : manual.ForeignSide;
        var bends = manual.Bends.ToList();
        if (Across(side) != Across(old))
        {
            double stub = EdgeOf(box, side) + (side is Side.Left or Side.Top ? -Gap : Gap);
            if (keyEnd) bends.Insert(0, stub);
            // A last segment running straight into the old side already turns into the new one.
            else if (AlongX(manual.PrimarySide, bends.Count) != Across(old)) bends.Add(stub);
        }
        var moved = keyEnd ? manual with { PrimarySide = side, PrimaryOffset = offset, Bends = bends } : manual with { ForeignSide = side, ForeignOffset = offset, Bends = bends };
        return Simplify(moved, primary, foreign);
    }
}