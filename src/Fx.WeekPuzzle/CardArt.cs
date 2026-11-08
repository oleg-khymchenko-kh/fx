using System.Windows;
using System.Windows.Media;

namespace Fx.WeekPuzzle;

internal enum CardState { Normal, Dragging, Hint }

internal static class CardArt
{
    private static readonly Brush Paper = Brushes.White;
    private static readonly Brush Frame = Hex("#B0B4B9");
    private static readonly Color BarColor = (Color)ColorConverter.ConvertFromString("#E53935");
    private const double BarAlpha = 0.3;
    private const double BarAlphaZoomedOut = 0.65;
    private static readonly Brush Zig = Hex("#1A56C4");
    private static readonly Brush Faint = Hex("#9AA0A6");
    private static readonly Brush Grid = Hex("#D5D9DF");
    private static readonly Brush Edge = Hex("#80868B");
    private static readonly Brush HintFrame = Hex("#F09300");
    private static readonly Brush HintFill = Hex("#40F09300");
    public static readonly Brush Good = Hex("#A02E7D32");
    public static readonly Brush Bad = Hex("#B0D32F2F");
    private static readonly Brush DayFill = Hex("#D9F2D9");
    private static readonly Brush Dot = Hex("#E00000");
    private const double DotRadius = 5;
    private const double DotScreenRadius = 2;

    public static StreamGeometry BarGeometry(CardShape shape) =>
        BarGeometry(shape.BarTop, shape.BarBottom, WeekCards.Margin, 1);

    public static StreamGeometry BarGeometry(double[] top, double[] bottom, double left, double column)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            int c = 0;
            while (c < top.Length)
            {
                if (double.IsNaN(top[c]))
                {
                    c++;
                    continue;
                }
                int start = c;
                c++;
                while (c < top.Length && !double.IsNaN(top[c]) && top[c] < bottom[c - 1] && bottom[c] > top[c - 1]) c++;
                ctx.BeginFigure(new Point(left + start * column, top[start]), true, true);
                for (int k = start; k < c; k++)
                {
                    ctx.LineTo(new Point(left + k * column, top[k]), false, false);
                    ctx.LineTo(new Point(left + (k + 1) * column, top[k]), false, false);
                }
                for (int k = c - 1; k >= start; k--)
                {
                    ctx.LineTo(new Point(left + (k + 1) * column, bottom[k]), false, false);
                    ctx.LineTo(new Point(left + k * column, bottom[k]), false, false);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    public static StreamGeometry LineGeometry(CardShape shape) => LineGeometry(shape.Lines);

    public static StreamGeometry LineGeometry(IReadOnlyList<IReadOnlyList<Point>> lines)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
            foreach (var line in lines)
            {
                ctx.BeginFigure(line[0], false, false);
                for (int i = 1; i < line.Count; i++) ctx.LineTo(line[i], true, true);
            }
        geometry.Freeze();
        return geometry;
    }

    public static void Draw(DrawingContext dc, CardShape shape, StreamGeometry bars, StreamGeometry line, double zoom, CardState state,
        IReadOnlySet<DateTime> filledDays)
    {
        double px = 1 / zoom;
        double margin = WeekCards.Margin;
        dc.DrawRectangle(Paper, null, new Rect(0, 0, shape.Width, shape.Height));
        foreach (var day in shape.Days)
            if (filledDays.Contains(day.Date))
                dc.DrawRectangle(DayFill, null, new Rect(day.X0, 0, day.X1 - day.X0, shape.Height));
        var grid = Frozen(Pen(Grid, Math.Max(1, px)));
        foreach (double y in shape.GridLines)
            dc.DrawLine(grid, new Point(0, y), new Point(shape.Width, y));
        dc.DrawGeometry(BarBrush(zoom), null, bars);
        var dash = Pen(Faint, Math.Max(1, px));
        dash.DashStyle = new DashStyle(new double[] { 4, 4 }, 0);
        dash.Freeze();
        foreach (double x in shape.DayLines)
            dc.DrawLine(dash, new Point(x, margin), new Point(x, shape.Height - margin));
        dc.DrawLine(Frozen(Pen(Edge, Math.Max(1.5, 1.5 * px))),
            new Point(shape.WeekLine, margin), new Point(shape.WeekLine, shape.Height - margin));
        var tick = Frozen(Pen(Edge, Math.Max(3, 2.5 * px)));
        dc.DrawLine(tick, new Point(0, shape.LeftTick), new Point(margin - 4, shape.LeftTick));
        dc.DrawLine(tick, new Point(shape.Width - margin + 4, shape.RightTick), new Point(shape.Width, shape.RightTick));
        dc.DrawGeometry(null, LinePen(Math.Max(1.3, px)), line);
        DrawState(dc, shape.Width, shape.Height, zoom, state);
        if (filledDays.Count == 0) return;
        double radius = Math.Max(DotRadius, DotScreenRadius * px);
        double dotY = Math.Max(margin / 2, radius);
        foreach (var day in shape.Days)
            if (filledDays.Contains(day.Date))
                dc.DrawEllipse(Dot, null, new Point((day.X0 + day.X1) / 2, dotY), radius, radius);
    }

    public static void DrawState(DrawingContext dc, double width, double height, double zoom, CardState state)
    {
        double px = 1 / zoom;
        if (state == CardState.Hint) dc.DrawRectangle(HintFill, null, new Rect(0, 0, width, height));
        double frame = state switch
        {
            CardState.Dragging => Math.Max(3, 3 * px),
            CardState.Hint => Math.Max(6, 6 * px),
            _ => Math.Max(1, px),
        };
        var frameBrush = state switch
        {
            CardState.Dragging => Zig,
            CardState.Hint => HintFrame,
            _ => Frame,
        };
        dc.DrawRectangle(null, Frozen(Pen(frameBrush, frame)),
            new Rect(frame / 2, frame / 2, width - frame, height - frame));
    }

    public static Pen LinePen(double thickness) => LinePen(thickness, Zig);

    public static Pen LinePen(double thickness, Brush brush)
    {
        var zig = Pen(brush, thickness);
        zig.LineJoin = PenLineJoin.Round;
        zig.StartLineCap = PenLineCap.Round;
        zig.EndLineCap = PenLineCap.Round;
        zig.Freeze();
        return zig;
    }

    public static Brush BarBrush(double zoom)
    {
        double alpha = Math.Clamp(BarAlpha * Math.Sqrt(0.5 / zoom), BarAlpha, BarAlphaZoomedOut);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), BarColor.R, BarColor.G, BarColor.B));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Brush brush, double thickness) => new(brush, thickness);

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static Brush Hex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
