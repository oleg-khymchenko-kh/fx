using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FXViewer.Chart;

namespace Fx.WeekPuzzle;

internal static class DayArt
{
    private const double MinGridSpacingPx = 6;
    private const double LabelSize = 11 * DayCards.Scale;
    private const double LineScreenPx = 1.8;
    private const double LineWorld = 1.5 * DayCards.Scale;
    private static readonly Brush Paper = Brushes.White;
    private static readonly Brush LabelBrush = Hex("#5F6368");
    private static readonly Brush HeaderLine = Hex("#DADCE0");
    private static readonly Brush GridDark = Hex("#C8C8C8");
    private static readonly Brush GridLight = Hex("#E0E0E0");
    private static readonly Brush AsiaEurope = Hex("#E2EDF9");
    private static readonly Brush Europe = Hex("#F2F7FD");
    private static readonly Brush Overlap = Hex("#F9EBD2");
    private static readonly Brush America = Hex("#FCF3E5");
    private static readonly Brush Closed = Hex("#F9F9F9");
    private static readonly Brush Mirror = Hex("#8E24AA");
    private static readonly Brush MirrorLine = Hex("#1E8E3E");
    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Typeface MirrorFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static StreamGeometry BarGeometry(DayShape shape) =>
        CardArt.BarGeometry(shape.BarTop, shape.BarBottom, 0, DayCards.BarWidth);

    public static StreamGeometry LineGeometry(DayShape shape) => CardArt.LineGeometry(shape.Lines);

    public static FormattedText Label(DayShape shape, bool mirrored, double pixelsPerDip) => mirrored
        ? new($"{shape.Label}   ↕ MIRRORED", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MirrorFont, LabelSize,
            Brushes.White, pixelsPerDip)
        : new(shape.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, LabelSize, LabelBrush, pixelsPerDip);

    public static void Draw(DrawingContext dc, DayShape shape, StreamGeometry bars, StreamGeometry line, FormattedText label,
        double zoom, CardState state, bool mirrored)
    {
        double px = 1 / zoom;
        double top = DayCards.Header;
        dc.DrawRectangle(Paper, null, new Rect(0, 0, shape.Width, shape.Height));
        foreach (var band in shape.Bands)
            dc.DrawRectangle(SessionBrush(band.Session), null, new Rect(band.X0, top, band.X1 - band.X0, shape.Height - top));
        if (mirrored) dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, top + shape.Height));

        bool show10 = 10 * DayCards.PxPerPip * zoom >= MinGridSpacingPx;
        bool show50 = 50 * DayCards.PxPerPip * zoom >= MinGridSpacingPx;
        var solid100 = Frozen(new Pen(GridDark, px));
        var dotted50 = Dotted(GridDark, px);
        var dotted10 = Dotted(GridLight, px);
        foreach (var grid in shape.GridLines)
        {
            var pen = grid.Pips switch
            {
                100 => solid100,
                50 => show50 ? dotted50 : null,
                _ => show10 ? dotted10 : null,
            };
            if (pen is not null) dc.DrawLine(pen, new Point(0, grid.Y), new Point(shape.Width, grid.Y));
        }
        bool showHours = DayCards.PxPerHour * zoom >= MinGridSpacingPx;
        var midnight = Frozen(new Pen(GridLight, px));
        var hour = Dotted(GridLight, px);
        foreach (var h in shape.HourLines)
        {
            if (!h.Midnight && !showHours) continue;
            dc.DrawLine(h.Midnight ? midnight : hour, new Point(h.X, top), new Point(h.X, shape.Height));
        }

        dc.DrawGeometry(CardArt.BarBrush(zoom * DayCards.Scale), null, bars);
        double lineWidth = Math.Max(px, Math.Min(LineScreenPx * px, LineWorld));
        dc.DrawGeometry(null, mirrored ? CardArt.LinePen(lineWidth, MirrorLine) : CardArt.LinePen(lineWidth), line);
        if (mirrored) dc.Pop();

        dc.DrawRectangle(mirrored ? Mirror : Paper, null, new Rect(0, 0, shape.Width, top));
        dc.DrawLine(Frozen(new Pen(mirrored ? Mirror : HeaderLine, px)), new Point(0, top), new Point(shape.Width, top));
        dc.DrawText(label, new Point(4 * DayCards.Scale, (top - label.Height) / 2));
        CardArt.DrawState(dc, shape.Width, shape.Height, zoom, state);
    }

    private static Brush SessionBrush(ChartSession session) => session switch
    {
        ChartSession.AsiaEurope => AsiaEurope,
        ChartSession.Europe => Europe,
        ChartSession.Overlap => Overlap,
        ChartSession.America => America,
        ChartSession.Closed => Closed,
        _ => Paper,
    };

    private static Pen Dotted(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { DashStyle = new DashStyle(new double[] { 1, 1 }, 0), DashCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

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
