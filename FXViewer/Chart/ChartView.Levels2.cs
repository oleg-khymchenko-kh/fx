using System.Windows;
using System.Windows.Media;

namespace FXViewer.Chart;

public sealed partial class ChartView
{
    private const int Levels2AreaGapPx = 4;
    private const int Levels2SplitLineArgb = unchecked((int)0xFFEEEEEE);

    private readonly Levels2Finder _levels2Finder = new();
    private int _levels2StripPx;
    private readonly List<Level2> _levels2 = new();

    private bool Levels2Shown(SymbolSeries s) =>
        s.Levels2Panel && s.SourceSymbol != null && GetSeries(s.SourceSymbol) != null;

    private int Levels2StripPx(IReadOnlyList<SymbolSeries> visibleSeries)
    {
        int strip = 0;
        foreach (var s in visibleSeries)
            if (Levels2Shown(s)) strip += Levels2AreaWidth(s) + Levels2AreaGapPx;
        return strip;
    }

    private (List<int> Dividers, List<int> Separators) Levels2StripColumns(
        IReadOnlyList<SymbolSeries> visibleSeries, int width)
    {
        var dividers = new List<int>();
        var separators = new List<int>();
        int areaRight = width;
        foreach (var s in visibleSeries)
        {
            if (!Levels2Shown(s)) continue;
            if (areaRight > 0 && s.Level2Right is { IsEmpty: false } right)
                dividers.Add(Levels2SplitX(width, areaRight, right));
            areaRight -= Levels2AreaWidth(s) + Levels2AreaGapPx;
            separators.Add(areaRight + Levels2AreaGapPx / 2);
        }
        return (dividers, separators);
    }

    private static int Levels2SplitX(int width, int areaRight, LevelSizeTable rightTable) =>
        Math.Min(width, areaRight) - (int)Math.Ceiling(rightTable.MaxPixels);

    private void ClipOverlaysToLevels2Strip(int pw, int ph, DpiScale dpi)
    {
        Geometry? clip = null;
        if (_levels2StripPx > 0)
        {
            clip = new RectangleGeometry(new Rect(0, 0,
                Math.Max(0, pw - _levels2StripPx) / dpi.DpiScaleX, ph / dpi.DpiScaleY));
            clip.Freeze();
        }
        _markerCanvas.Clip = clip;
        _editCanvas.Clip = clip;
    }

    private static int Levels2AreaWidth(SymbolSeries s) =>
        (int)Math.Ceiling(s.Level2Left?.MaxPixels ?? 0)
        + (int)Math.Ceiling(s.Level2Right?.MaxPixels ?? 0);

    private int DrawLevels2(int[] buffer, int width, int height, int areaRight, SymbolSeries s,
        long fromUnix, long anchorUnix)
    {
        if (areaRight <= 0) return 0;
        var leftTable = s.Level2Left;
        var rightTable = s.Level2Right;
        if (s.SourceSymbol == null || leftTable == null || rightTable == null
            || leftTable.IsEmpty || rightTable.IsEmpty) return 0;
        var source = GetSeries(s.SourceSymbol);
        if (source == null) return 0;
        var minutes = MinuteSequence.Of(source.History);
        if (minutes.Length == 0) return 0;
        _levels2Finder.Find(minutes, fromUnix, anchorUnix,
            leftTable.MinPoints, rightTable.MinPoints,
            leftTable.MaxPoints, _levels2);
        if (_levels2.Count == 0) return 0;
        int split = Levels2SplitX(width, areaRight, rightTable);
        double offset = SeriesOffset(s.SourceSymbol);
        int riseColor = s.ColorArgb | unchecked((int)0xFF000000);
        int fallColor = (s.SellColorArgb == 0 ? s.ColorArgb : s.SellColorArgb)
            | unchecked((int)0xFF000000);
        int leftmost = width;
        foreach (var level in _levels2)
        {
            int y = (int)Math.Floor(DisplayToY(level.Value, offset, anchorUnix));
            if (y < 0 || y >= height) continue;
            double leftPx = leftTable.PixelsAt(level.ApproachPoints);
            double rightPx = rightTable.PixelsAt(level.ReversalPoints);
            int row = y * width;
            int leftStart = DrawLevel2Span(buffer, row, width, split, -leftPx,
                level.Top ? riseColor : fallColor);
            int rightStart = DrawLevel2Span(buffer, row, width, split, rightPx,
                level.Top ? fallColor : riseColor);
            leftmost = Math.Min(leftmost, Math.Min(leftStart, rightStart));
        }
        return leftmost < width ? width - leftmost : 0;
    }

    private static int DrawLevel2Span(int[] buffer, int row, int width, int split, double lengthPx,
        int color)
    {
        double length = Math.Abs(lengthPx);
        if (length <= 0) return width;
        int full = (int)Math.Floor(length);
        int partialAlpha = (int)Math.Round((length - full) * 255);
        int direction = lengthPx < 0 ? -1 : 1;
        int from = direction < 0 ? split - full : split;
        int to = direction < 0 ? split : split + full;
        int leftmost = width;
        for (int x = Math.Max(0, from); x < Math.Min(width, to); x++)
        {
            buffer[row + x] = color;
            if (x < leftmost) leftmost = x;
        }
        if (partialAlpha > 0)
        {
            int x = direction < 0 ? from - 1 : to;
            if (x >= 0 && x < width)
            {
                buffer[row + x] = BlendOverPremultiplied(buffer[row + x],
                    PremultiplyArgb(color, partialAlpha));
                if (x < leftmost) leftmost = x;
            }
        }
        return leftmost;
    }
}
