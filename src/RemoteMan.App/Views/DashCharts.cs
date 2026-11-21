using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RemoteMan.App.Views;

/// <summary>A donut slice or legend row.</summary>
public sealed record ChartSlice(string Name, int Count, Brush Brush, string? Note = null);

/// <summary>A point on an area chart: one value per stacked series (bottom first), the x label and the hover text.</summary>
public sealed record ChartPoint(double[] Values, string? Label, string Hover);

/// <summary>A column: value, x label (null = no label), hover text, and whether it is the highlighted one (today).</summary>
public sealed record ChartColumn(double Value, string? Label, string Hover, bool Highlight = false);

/// <summary>Shared drawing for the dashboard's small charts: theme colors, text and the hover label.</summary>
public abstract class DashChart : FrameworkElement
{
    internal static readonly Brush Grid = Frozen("#22272C");
    internal static readonly Brush Axis = Frozen("#2A2F35");
    internal static readonly Brush Faint = Frozen("#7D8690");
    internal static readonly Brush Muted = Frozen("#9AA3AD");
    internal static readonly Brush Strong = Frozen("#F4F6F8");
    internal static readonly Brush TipBg = Frozen("#1D2125");
    internal static readonly Brush TipBorder = Frozen("#353B42");
    internal static readonly Brush Panel = Frozen("#16191C");
    internal static readonly Brush Cross = Frozen("#4A525B");

    /// <summary>Categorical hues for series and slices, in fixed order (validated for the dark surface).</summary>
    public static readonly Brush[] Series = new[] { "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181" }.Select(Frozen).ToArray();
    public static readonly Brush Other = Frozen("#5B636C");

    internal static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? UiBold : Ui, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>0 = left, 0.5 = centred, 1 = right of x.</summary>
    protected void DrawText(DrawingContext dc, string s, double x, double y, double size, Brush brush, double align = 0, bool bold = false)
    {
        var t = Text(s, size, brush, bold);
        dc.DrawText(t, new Point(x - t.Width * align, y));
    }

    /// <summary>The hover label: a small box above (x, y), kept inside the control.</summary>
    protected void DrawTip(DrawingContext dc, string text, double x, double y)
    {
        var lines = text.Split('\n');
        var texts = lines.Select((l, i) => Text(l, 11.5, i == 0 ? Strong : Muted, i == 0)).ToList();
        var w = texts.Max(t => t.Width) + 16;
        var h = texts.Sum(t => t.Height) + 10;
        var left = Math.Clamp(x - w / 2, 0, Math.Max(0, ActualWidth - w));
        var top = Math.Max(0, y - h - 8);
        dc.DrawRoundedRectangle(TipBg, new Pen(TipBorder, 1), new Rect(left, top, w, h), 3, 3);
        var ty = top + 5;
        foreach (var t in texts) { dc.DrawText(t, new Point(left + 8, ty)); ty += t.Height; }
    }

    protected int Hover { get; private set; } = -1;

    /// <summary>Which mark is under the mouse, or -1.</summary>
    protected abstract int HitIndex(Point p);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = HitIndex(e.GetPosition(this));
        if (i != Hover) { Hover = i; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (Hover != -1) { Hover = -1; InvalidateVisual(); }
    }

    protected DashChart()
    {
        // Hit testing needs a background; the charts draw their own transparent rect.
        SnapsToDevicePixels = true;
    }

    protected static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DashChart)d).InvalidateVisual();

    /// <summary>Gridline values: 0, a rounded middle and a rounded top that covers max.</summary>
    protected static double NiceTop(double max)
    {
        if (max <= 4) return 4;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 2, 2.5, 4, 5, 10 })
            if (m * mag >= max) return m * mag;
        return 10 * mag;
    }
}

/// <summary>Donut with the total in the middle; hover a slice for its share.</summary>
public sealed class DonutChart : DashChart
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(nameof(Slices), typeof(IReadOnlyList<ChartSlice>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public IReadOnlyList<ChartSlice>? Slices { get => (IReadOnlyList<ChartSlice>?)GetValue(SlicesProperty); set => SetValue(SlicesProperty, value); }

    public static readonly DependencyProperty CenterLabelProperty = DependencyProperty.Register(nameof(CenterLabel), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("entries", FrameworkPropertyMetadataOptions.AffectsRender));
    public string CenterLabel { get => (string)GetValue(CenterLabelProperty); set => SetValue(CenterLabelProperty, value); }

    private Point C => new(ActualWidth / 2, ActualHeight / 2);
    private double R => Math.Min(ActualWidth, ActualHeight) / 2 - 2;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var slices = Slices ?? [];
        var total = slices.Sum(s => s.Count);
        if (total == 0 || R <= 10) return;
        var c = C;
        double r = R, ri = R * 0.68, a0 = -Math.PI / 2, gap = slices.Count(s => s.Count > 0) > 1 ? 0.03 : 0;
        for (var i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            if (s.Count == 0) continue;
            var sweep = s.Count / (double)total * Math.PI * 2;
            var grow = i == Hover ? 3 : 0;
            dc.DrawGeometry(s.Brush, null, Arc(c, r + grow, ri, a0 + gap / 2, a0 + sweep - gap / 2));
            a0 += sweep;
        }
        if (Hover >= 0 && Hover < slices.Count)
        {
            var s = slices[Hover];
            DrawText(dc, s.Count.ToString("N0"), c.X, c.Y - 18, 22, Strong, 0.5, true);
            DrawText(dc, $"{s.Name}, {Math.Round(s.Count * 100.0 / total)}%", c.X, c.Y + 9, 10.5, Faint, 0.5);
        }
        else
        {
            DrawText(dc, total.ToString("N0"), c.X, c.Y - 18, 22, Strong, 0.5, true);
            DrawText(dc, CenterLabel, c.X, c.Y + 9, 10.5, Faint, 0.5);
        }
    }

    private static Geometry Arc(Point c, double r, double ri, double a0, double a1)
    {
        if (a1 - a0 >= Math.PI * 2 - 0.001) a1 = a0 + Math.PI * 2 - 0.001;
        Point P(double rr, double a) => new(c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a));
        var large = a1 - a0 > Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(r, a0), true, true);
            ctx.ArcTo(P(r, a1), new Size(r, r), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(ri, a1), true, false);
            ctx.ArcTo(P(ri, a0), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    protected override int HitIndex(Point p)
    {
        var slices = Slices ?? [];
        var total = slices.Sum(s => s.Count);
        var d = (p - C).Length;
        if (total == 0 || d > R + 3 || d < R * 0.68) return -1;
        var a = Math.Atan2(p.Y - C.Y, p.X - C.X) + Math.PI / 2;
        if (a < 0) a += Math.PI * 2;
        var acc = 0.0;
        for (var i = 0; i < slices.Count; i++)
        {
            acc += slices[i].Count / (double)total * Math.PI * 2;
            if (a <= acc) return i;
        }
        return -1;
    }
}

/// <summary>Columns with rounded tops, a dashed average line and hover text.</summary>
public sealed class ColumnChart : DashChart
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(IReadOnlyList<ChartColumn>), typeof(ColumnChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public IReadOnlyList<ChartColumn>? Columns { get => (IReadOnlyList<ChartColumn>?)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    private const double Pl = 24, Pr = 4, Pt = 8, Pb = 18;
    private static readonly Brush Highlight = Frozen("#3FB8A4");

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var cols = Columns ?? [];
        if (cols.Count == 0 || ActualWidth < 60) return;
        var top = NiceTop(cols.Max(c => c.Value));
        double w = ActualWidth - Pl - Pr, h = ActualHeight - Pt - Pb, slot = w / cols.Count, bw = Math.Min(26, slot - 6);
        double Y(double v) => Pt + h * (1 - v / top);
        foreach (var v in new[] { 0, top / 2, top })
        {
            dc.DrawLine(new Pen(v == 0 ? Axis : Grid, 1), new Point(Pl, Math.Round(Y(v)) + 0.5), new Point(ActualWidth - Pr, Math.Round(Y(v)) + 0.5));
            DrawText(dc, v.ToString("0.#"), Pl - 5, Y(v) - 7, 10, Faint, 1);
        }
        for (var i = 0; i < cols.Count; i++)
        {
            var c = cols[i];
            var x = Pl + i * slot + (slot - bw) / 2;
            if (c.Value > 0)
            {
                var y = Y(c.Value);
                var r = Math.Min(4, (Pt + h - y) / 2);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(x, Pt + h), true, true);
                    ctx.LineTo(new Point(x, y + r), true, false);
                    ctx.QuadraticBezierTo(new Point(x, y), new Point(x + r, y), true, false);
                    ctx.LineTo(new Point(x + bw - r, y), true, false);
                    ctx.QuadraticBezierTo(new Point(x + bw, y), new Point(x + bw, y + r), true, false);
                    ctx.LineTo(new Point(x + bw, Pt + h), true, false);
                }
                g.Freeze();
                dc.PushOpacity(Hover >= 0 && Hover != i ? 0.6 : 1);
                dc.DrawGeometry(c.Highlight ? Highlight : Series[0], null, g);
                dc.Pop();
            }
            if (c.Label is { } l) DrawText(dc, l, x + bw / 2, ActualHeight - 14, 10, Faint, 0.5);
        }
        var avg = cols.Average(c => c.Value);
        if (avg > 0)
        {
            dc.DrawLine(new Pen(Muted, 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(Pl, Y(avg)), new Point(ActualWidth - Pr, Y(avg)));
            DrawText(dc, $"avg {avg:0.#}/day", Pl + 4, Y(avg) - 15, 10, Muted);
        }
        if (Hover >= 0 && Hover < cols.Count)
            DrawTip(dc, cols[Hover].Hover, Pl + Hover * slot + slot / 2, Y(cols[Hover].Value));
    }

    protected override int HitIndex(Point p)
    {
        var cols = Columns ?? [];
        if (cols.Count == 0 || p.X < Pl || p.X > ActualWidth - Pr) return -1;
        return Math.Clamp((int)((p.X - Pl) / ((ActualWidth - Pl - Pr) / cols.Count)), 0, cols.Count - 1);
    }
}

/// <summary>Stacked areas (bottom series first) with 2px lines, a crosshair and hover text.</summary>
public sealed class AreaChart : DashChart
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(AreaChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public IReadOnlyList<ChartPoint>? Points { get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    /// <summary>One brush per series, bottom first.</summary>
    public static readonly DependencyProperty BrushesProperty = DependencyProperty.Register(nameof(Brushes), typeof(IReadOnlyList<Brush>), typeof(AreaChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<Brush>? Brushes { get => (IReadOnlyList<Brush>?)GetValue(BrushesProperty); set => SetValue(BrushesProperty, value); }

    /// <summary>Start the y axis at a rounded value under the minimum instead of 0 (growth chart).</summary>
    public bool FromMinimum { get; set; }

    private const double Pl = 28, Pr = 8, Pt = 8, Pb = 18;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(RenderSize));
        var pts = Points ?? [];
        if (pts.Count < 2 || ActualWidth < 60) return;
        var series = pts[0].Values.Length;
        var tops = pts.Select(p => p.Values.Sum()).ToList();
        double bottom = 0, top;
        if (FromMinimum)
        {
            var lo = tops.Min();
            var step = NiceTop(Math.Max(1, (tops.Max() - lo) / 2));
            bottom = Math.Max(0, Math.Floor(lo / step) * step);
            top = bottom + 2 * step;
            while (top < tops.Max()) top += step;
        }
        else top = NiceTop(tops.Max());
        double w = ActualWidth - Pl - Pr, h = ActualHeight - Pt - Pb;
        double X(int i) => Pl + i * w / (pts.Count - 1);
        double Y(double v) => Pt + h * (1 - (v - bottom) / (top - bottom));
        foreach (var v in new[] { bottom, (bottom + top) / 2, top })
        {
            dc.DrawLine(new Pen(v == bottom ? Axis : Grid, 1), new Point(Pl, Math.Round(Y(v)) + 0.5), new Point(ActualWidth - Pr, Math.Round(Y(v)) + 0.5));
            DrawText(dc, v.ToString("0.#"), Pl - 5, Y(v) - 7, 10, Faint, 1);
        }
        var brushes = Brushes ?? Series;
        // Stack from 0; the first fill's lower edge is the axis bottom.
        var cum = new double[pts.Count];
        var lower = pts.Select(_ => bottom).ToArray();
        for (var s = 0; s < series; s++)
        {
            for (var i = 0; i < pts.Count; i++) cum[i] += pts[i].Values[s];
            var upper = cum.ToArray();
            var brush = brushes[s % brushes.Count];
            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(upper[0])), true, true);
                for (var i = 1; i < pts.Count; i++) ctx.LineTo(new Point(X(i), Y(upper[i])), true, true);
                for (var i = pts.Count - 1; i >= 0; i--) ctx.LineTo(new Point(X(i), Y(lower[i])), true, true);
            }
            fill.Freeze();
            dc.PushOpacity(FromMinimum ? 0.15 : 0.35);
            dc.DrawGeometry(brush, null, fill);
            dc.Pop();
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(upper[0])), false, false);
                for (var i = 1; i < pts.Count; i++) ctx.LineTo(new Point(X(i), Y(upper[i])), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(brush, 2) { LineJoin = PenLineJoin.Round }, line);
            lower = upper;
        }
        for (var i = 0; i < pts.Count; i++)
            if (pts[i].Label is { } l)
                DrawText(dc, l, X(i), ActualHeight - 14, 10, Faint, i == 0 ? 0 : i == pts.Count - 1 ? 1 : 0.5);
        if (FromMinimum)
            dc.DrawEllipse(brushes[0], new Pen(Panel, 2), new Point(X(pts.Count - 1), Y(tops[^1])), 4, 4);
        if (Hover >= 0 && Hover < pts.Count)
        {
            dc.DrawLine(new Pen(Cross, 1) { DashStyle = new DashStyle([2, 2], 0) }, new Point(X(Hover), Pt), new Point(X(Hover), Pt + h));
            DrawTip(dc, pts[Hover].Hover, X(Hover), Y(tops[Hover]));
        }
    }

    protected override int HitIndex(Point p)
    {
        var pts = Points ?? [];
        if (pts.Count < 2 || p.X < Pl - 4 || p.X > ActualWidth - Pr + 4) return -1;
        return Math.Clamp((int)Math.Round((p.X - Pl) / ((ActualWidth - Pl - Pr) / (pts.Count - 1))), 0, pts.Count - 1);
    }
}

/// <summary>Weekday by hour grid (Monday first), one blue hue from faint to bright.</summary>
public sealed class HeatmapChart : DashChart
{
    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(nameof(Cells), typeof(int[,]), typeof(HeatmapChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public int[,]? Cells { get => (int[,]?)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }

    public static readonly Brush[] Ramp = new[] { "#1A2027", "#1B3350", "#1F4A7A", "#2C66A8", "#3987e5", "#7DB3F2" }.Select(Frozen).ToArray();
    private static readonly string[] Days = [.. Enumerable.Range(0, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[(i + 1) % 7])];
    private const double Pl = 32, Pb = 16;

    private double Cw => (ActualWidth - Pl) / 24;
    private double Ch => Math.Min(18, (ActualHeight - Pb) / 7);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var cells = Cells;
        if (cells is null || ActualWidth < 100) return;
        var max = 0;
        foreach (var v in cells) max = Math.Max(max, v);
        for (var d = 0; d < 7; d++)
        {
            DrawText(dc, Days[d], Pl - 6, d * Ch + Ch / 2 - 7, 10, Faint, 1);
            for (var hr = 0; hr < 24; hr++)
            {
                var v = cells[d, hr];
                var step = v == 0 || max == 0 ? 0 : 1 + (int)Math.Round((Ramp.Length - 2) * (v / (double)max));
                var rect = new Rect(Pl + hr * Cw + 1, d * Ch + 1, Math.Max(1, Cw - 2), Math.Max(1, Ch - 2));
                dc.DrawRoundedRectangle(Ramp[step], Hover == d * 24 + hr ? new Pen(Strong, 1) : null, rect, 2, 2);
            }
        }
        foreach (var hr in new[] { 0, 6, 12, 18, 23 }) DrawText(dc, hr.ToString("00"), Pl + hr * Cw + Cw / 2, 7 * Ch + 2, 10, Faint, 0.5);
        if (Hover >= 0)
        {
            int d = Hover / 24, hr = Hover % 24, v = cells[d, hr];
            DrawTip(dc, $"{Days[d]} {hr:00}:00\n{v} session{(v == 1 ? "" : "s")}", Pl + hr * Cw + Cw / 2, d * Ch);
        }
    }

    protected override int HitIndex(Point p)
    {
        if (Cells is null || p.X < Pl || p.Y < 0 || p.Y >= 7 * Ch) return -1;
        var hr = (int)((p.X - Pl) / Cw);
        var d = (int)(p.Y / Ch);
        return hr is >= 0 and < 24 && d is >= 0 and < 7 ? d * 24 + hr : -1;
    }
}
