namespace FXViewer.Chart;

public static class TradingCentralRenderer
{
    public const int PivotAlpha = 255;
    public const int TargetAlpha = 210;
    public const int AlternativeAlpha = 170;
    public const int DashOnPx = 4;
    public const int DashPeriodPx = 8;
    public const int PivotMarkerHalfPx = 3;
    public const int TargetMarkerRadiusPx = 2;
    public const int EndCapHalfPx = 3;

    private const int OutlineArgb = unchecked((int)0xFFFFFFFF);

    public static void Draw(int[] buffer, int pw, int ph, SymbolSeries s, FlattenMap? flatten,
        WeekendCompressor? map, long columnSeconds, long startBucket, double topPrice,
        double pointsPerRow, double offset, List<TradingCentralMarker> markers, long cut = long.MaxValue)
    {
        var marks = s.TradingCentralMarks;
        var transform = s.Transform;
        if (marks == null || transform == null || pointsPerRow <= 0) return;
        double bucketSec = columnSeconds;
        int reach = PivotMarkerHalfPx + 1;
        foreach (var mark in marks)
        {
            if (mark.FromUnix >= cut) continue;
            long va = map?.ToVirtual(mark.FromUnix) ?? mark.FromUnix;
            long vb = map?.ToVirtual(mark.ToUnix) ?? mark.ToUnix;
            if (vb <= va) continue;
            double xa = va / bucketSec - startBucket;
            double xb = vb / bucketSec - startBucket;
            if (xb < -reach || xa >= pw + reach) continue;
            double display = transform.ToDisplay(mark.Value);
            int alpha = AlphaOf(mark.Kind);
            bool dashed = mark.Kind == TradingCentralLevelKind.Alternative;
            int xs = Math.Max(0, (int)Math.Floor(xa + 0.5));
            int xe = Math.Min(pw - 1, (int)Math.Floor(xb + 0.5));
            for (int px = xs; px <= xe; px++)
            {
                if (dashed && Mod(px + startBucket, DashPeriodPx) >= DashOnPx) continue;
                long v = (long)((px + startBucket) * bucketSec);
                ChartRasterizer.BlendPixel(buffer, pw, ph, px,
                    Row(topPrice, offset, display, flatten, v, pointsPerRow), s.ColorArgb, alpha);
            }
            if (xb >= 0 && xb < pw)
            {
                int xEnd = (int)Math.Round(xb);
                int yEnd = Row(topPrice, offset, display, flatten, vb, pointsPerRow);
                for (int dy = -EndCapHalfPx; dy <= EndCapHalfPx; dy++)
                    ChartRasterizer.BlendPixel(buffer, pw, ph, xEnd, yEnd + dy, s.ColorArgb, alpha);
            }
            if (xa < -reach || xa >= pw + reach) continue;
            int cx = (int)Math.Round(xa);
            int cy = Row(topPrice, offset, display, flatten, va, pointsPerRow);
            if (cy < -reach || cy >= ph + reach) continue;
            if (mark.Kind == TradingCentralLevelKind.Pivot)
            {
                bool pointsUp = mark.Up != transform.Mirror;
                ChartRasterizer.FillTriangle(buffer, pw, ph, cx, cy, PivotMarkerHalfPx + 1,
                    pointsUp, OutlineArgb);
                ChartRasterizer.FillTriangle(buffer, pw, ph, cx, cy, PivotMarkerHalfPx,
                    pointsUp, s.ColorArgb);
                markers.Add(new TradingCentralMarker(cx, cy, PivotMarkerHalfPx + 1, s.ColorArgb,
                    transform.Mirror, mark));
            }
            else if (mark.Kind == TradingCentralLevelKind.Target)
            {
                ChartRasterizer.StrokeDisc(buffer, pw, ph, cx, cy, TargetMarkerRadiusPx + 1,
                    OutlineArgb);
                ChartRasterizer.FillDisc(buffer, pw, ph, cx, cy, TargetMarkerRadiusPx, s.ColorArgb);
                markers.Add(new TradingCentralMarker(cx, cy, TargetMarkerRadiusPx + 1, s.ColorArgb,
                    transform.Mirror, mark));
            }
        }
    }

    private static int AlphaOf(TradingCentralLevelKind kind) =>
        kind == TradingCentralLevelKind.Pivot ? PivotAlpha
        : kind == TradingCentralLevelKind.Target ? TargetAlpha
        : AlternativeAlpha;

    private static int Row(double topPrice, double offset, double display, FlattenMap? flatten,
        long virtualSeconds, double pointsPerRow) =>
        (int)Math.Floor(
            (topPrice - offset - display - (flatten?.ShiftAt(virtualSeconds) ?? 0)) / pointsPerRow
            + 0.5);

    private static long Mod(long value, long m)
    {
        long r = value % m;
        return r < 0 ? r + m : r;
    }
}
