using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace PGMS.Desktop;

/// <summary>Line chart of the Activity Monitor Overview: dark plot with a green grid, like the SSMS 22.11 Overview charts. Null values are gaps.</summary>
public sealed class ActivityChart : FrameworkElement
{
    private static readonly Brush Plot = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)));
    private static readonly Pen Grid = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00))), 1));
    private static readonly Pen Line = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x7C, 0xFC, 0x00))), 1.5) { LineJoin = PenLineJoin.Round });
    private static readonly Brush Label = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
    private static readonly Pen Tick = Freeze(new Pen(Label, 1));
    /// <summary>Horizontal grid bands; the axis labels 0 … Scale sit on their edges, like the SSMS 0, 2 … 10 axis.</summary>
    public const int Divisions = 5;
    private const double AxisWidth = 34;
    private IReadOnlyList<double?> values = [];
    /// <summary>Points the horizontal axis spans; older readings scroll off the left edge.</summary>
    public int Capacity { get; set; } = PGMS.PostgreSql.ActivityMetricsHistory.Capacity;
    public IReadOnlyList<double?> Values => values;
    /// <summary>Top of the vertical axis after the last update: the first 1, 2 or 5 × 10ⁿ above the largest value (at least 1).</summary>
    public double Scale { get; private set; } = 1;

    public void SetValues(IReadOnlyList<double?> next)
    {
        values = next.TakeLast(Capacity).ToArray();
        double max = values.Where(v => v is { } x && double.IsFinite(x)).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        Scale = NiceCeiling(max);
        InvalidateVisual();
    }

    // Strictly above the value: a line equal to the top would lie on the plot border and vanish (M1030).
    internal static double NiceCeiling(double value)
    {
        if (!(value >= 1)) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double step in new[] { 1d, 2d, 5d, 10d }) if (step * magnitude > value) return step * magnitude;
        return 20 * magnitude;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // The plot keeps half a label of room above and below so the top and bottom axis labels are not clipped.
        const double inset = 6;
        var bounds = new Rect(0, inset, ActualWidth - AxisWidth, ActualHeight - 2 * inset);
        if (bounds.Width < 2 || bounds.Height < 2) return;
        dc.DrawRectangle(Plot, null, bounds);
        double half = Grid.Thickness / 2;
        for (int i = 1; i < 10; i++) { double x = Math.Round(bounds.Width * i / 10) + half; dc.DrawLine(Grid, new Point(x, bounds.Top), new Point(x, bounds.Bottom)); }
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= Divisions; i++)
        {
            double y = Math.Round(bounds.Top + bounds.Height * i / Divisions) + half;
            if (i > 0 && i < Divisions) dc.DrawLine(Grid, new Point(bounds.Left, y), new Point(bounds.Right, y));
            dc.DrawLine(Tick, new Point(bounds.Right, y), new Point(bounds.Right + 3, y));
            var label = new FormattedText(AxisLabel(i), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Label, dip);
            dc.DrawText(label, new Point(bounds.Right + 5, y - label.Height / 2));
        }
        if (values.Count == 0) return;
        double step = bounds.Width / Math.Max(1, Capacity - 1), offset = Capacity - values.Count;
        Point Map(int i, double v) => new((offset + i) * step, bounds.Bottom - 1 - Math.Clamp(v / Scale, 0, 1) * (bounds.Height - 2));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } v || !double.IsFinite(v)) { open = false; continue; }
                var p = Map(i, v);
                if (!open) { g.BeginFigure(p, false, false); open = true; }
                else g.LineTo(p, true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, Line, geometry);
        // A single reading has no segment; mark it so the chart is not empty.
        for (int i = 0; i < values.Count; i++)
            if (values[i] is { } v && double.IsFinite(v) && (i == 0 || values[i - 1] is null) && (i == values.Count - 1 || values[i + 1] is null))
                dc.DrawEllipse(Line.Brush, null, Map(i, v), 2, 2);
    }

    /// <summary>Axis label of grid line <paramref name="line"/>, counted from the top: Scale at 0, zero at <see cref="Divisions"/>.</summary>
    public string AxisLabel(int line) => (Scale * (Divisions - line) / Divisions).ToString("G4", CultureInfo.InvariantCulture);

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);
    private sealed class ChartPeer(ActivityChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(ActivityChart);
    }

    private static T Freeze<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
