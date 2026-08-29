namespace FXViewer.Chart;

public readonly record struct ChartPalette(int Background, int Weekend, int GridDay, int GridMonth, int GridYear,
    int GridPrice100, int GridPrice50, int GridPrice10, int GridTilted = 0, int GridTiltedNear = 0,
    int SessionEurope = 0, int SessionOverlap = 0, int SessionAmerica = 0, int WeekendSession = 0);

public readonly record struct RenderLine(ChartSeries Series, int[] Chosen, int Color, int LastPrice, long LastBucket, double OffsetPoints, double[]? ColumnShift = null, int Width = 1, bool[]? FullRange = null);

public readonly record struct TiltedFamilySettings(
    bool Visible, double AnchorSeconds, double AnchorPoints, double Slope,
    bool Nearest = false, long NearestLine = 0);

public readonly record struct TiltedGridSettings(
    TiltedFamilySettings Up, TiltedFamilySettings Down)
{
    public bool Visible => Up.Visible || Down.Visible;
}

public static class ChartRasterizer
{
    public const int GridPriceStepPoints = 1000;
    public const int GridPrice50StepPoints = 500;
    public const int GridPrice10StepPoints = 100;
    public const long DaySeconds = 86400;
    public const long HourSeconds = 3600;
    public const long MinuteSeconds = 60;
    public const int MinDayGridSpacingPixels = 10;
    public const int MinHourGridSpacingPixels = 10;
    public const int MinMinuteGridSpacingPixels = 10;
    public const int MinPriceGridSpacingPixels = 20;

    public static bool HourGridVisible(long columnSeconds) =>
        HourSeconds >= MinHourGridSpacingPixels * columnSeconds;

    public static bool MinuteGridVisible(long columnSeconds) =>
        columnSeconds > 0 && MinuteSeconds >= MinMinuteGridSpacingPixels * columnSeconds;

    public static bool SessionBandsVisible(long columnSeconds) =>
        columnSeconds > 0 && columnSeconds <= HourSeconds;

    public static void Render(int[] buffer, int width, int height,
        IReadOnlyList<RenderLine> lines, ChartPalette palette,
        long columnSeconds, long startBucket, long[] columnEdges,
        double topPrice, double pointsPerRow, TiltedGridSettings tiltedGrid = default,
        bool sessionBands = false)
    {
        Array.Fill(buffer, palette.Background, 0, width * height);
        if (pointsPerRow <= 0) return;
        DrawGrid(buffer, width, height, columnSeconds, startBucket, columnEdges,
            topPrice, pointsPerRow, palette, tiltedGrid, sessionBands);
        for (int i = 0; i < lines.Count; i++)
            DrawLine(buffer, width, height, lines[i], startBucket, topPrice, pointsPerRow);
        for (int i = 0; i < lines.Count; i++)
            DrawLastPrice(buffer, width, height, lines[i], startBucket, topPrice, pointsPerRow);
    }

    public static void DrawSeries(int[] buffer, int width, int height,
        RenderLine line, long startBucket, double topPrice, double pointsPerRow)
    {
        DrawLine(buffer, width, height, line, startBucket, topPrice, pointsPerRow);
        DrawLastPrice(buffer, width, height, line, startBucket, topPrice, pointsPerRow);
    }

    private static void DrawLine(int[] buffer, int width, int height,
        RenderLine line, long startBucket, double topPrice, double pointsPerRow)
    {
        double priceOffsetPoints = line.OffsetPoints;
        int extra = Math.Max(0, line.Width - 1);
        var columns = line.Series.Columns;
        var chosen = line.Chosen;
        var fullRange = line.FullRange;
        var shift = line.ColumnShift;
        int color = line.Color;
        int startColumn = (int)(startBucket - line.Series.FirstBucket);
        int visEnd = Math.Min(columns.Length, startColumn + width);
        bool prevHasData = false;
        int prevY = 0;
        int prevLo = 0;
        int prevHi = 0;
        for (int i = 0; i < visEnd; i++)
        {
            if (!columns[i].HasData)
            {
                prevHasData = false;
                continue;
            }
            double columnShift = shift == null || i >= shift.Length ? 0 : shift[i];
            int y = MapY(chosen[i] + columnShift, topPrice, priceOffsetPoints, pointsPerRow, height);
            int barLo = y;
            int barHi = y;
            if (fullRange != null && i < fullRange.Length && fullRange[i])
            {
                int yTop = MapY(columns[i].Max + columnShift, topPrice, priceOffsetPoints, pointsPerRow, height);
                int yBot = MapY(columns[i].Min + columnShift, topPrice, priceOffsetPoints, pointsPerRow, height);
                if (yTop < barLo) barLo = yTop;
                if (yBot > barHi) barHi = yBot;
            }
            int runLo = barLo;
            int runHi = barHi;
            if (prevHasData)
            {
                int start = prevY + Math.Sign(y - prevY);
                runLo = Math.Min(Math.Min(start, y), barLo);
                runHi = Math.Max(Math.Max(start, y), barHi);
                if (runLo >= prevLo && runHi <= prevHi)
                {
                    runLo = y;
                    runHi = y;
                }
                else if (barLo == barHi)
                {
                    if (y > prevHi) runLo = Math.Max(runLo, prevHi + 1);
                    else if (y < prevLo) runHi = Math.Min(runHi, prevLo - 1);
                }
            }
            int x = i - startColumn;
            if (x >= 0)
            {
                int fillHi = Math.Min(height - 1, runHi + extra);
                for (int row = runLo; row <= fillHi; row++)
                    buffer[row * width + x] = color;
            }
            prevY = y;
            prevLo = runLo;
            prevHi = runHi;
            prevHasData = true;
        }
    }

    private static int MapY(double value, double topPrice, double priceOffsetPoints, double pointsPerRow, int height)
    {
        int y = (int)Math.Round((topPrice - priceOffsetPoints - value) / pointsPerRow);
        if (y < 0) return 0;
        if (y >= height) return height - 1;
        return y;
    }

    public const int EntryRowHeightPx = 3;
    public const int EntryRowCount = 3;
    public const int EntryPanelHeightPx = EntryRowHeightPx * EntryRowCount;
    public const int EntryPanelGapPx = 2;
    public const int EntryBuyArgb = unchecked((int)0xFF2E7D32);
    public const int EntryBothLostArgb = unchecked((int)0xFF000000);
    public const int EntrySellArgb = unchecked((int)0xFFE65100);

    public static void DrawEntryPanel(int[] buffer, int width, int height, byte[] states,
        int bottomRow, int emptyArgb)
    {
        int top = bottomRow - EntryPanelHeightPx + 1;
        if (top < 0 || bottomRow >= height) return;
        for (int x = 0; x < width; x++)
        {
            byte s = x < states.Length ? states[x] : (byte)0;
            DrawEntryBar(buffer, width, x, top,
                (s & EntryPointsColumns.Buy) != 0 ? EntryBuyArgb : emptyArgb);
            DrawEntryBar(buffer, width, x, top + EntryRowHeightPx,
                (s & EntryPointsColumns.BothLost) != 0 ? EntryBothLostArgb : emptyArgb);
            DrawEntryBar(buffer, width, x, top + 2 * EntryRowHeightPx,
                (s & EntryPointsColumns.Sell) != 0 ? EntrySellArgb : emptyArgb);
        }
    }

    private static void DrawEntryBar(int[] buffer, int width, int x, int rowTop, int color)
    {
        for (int row = rowTop; row < rowTop + EntryRowHeightPx; row++)
            buffer[row * width + x] = color;
    }

    public const int AgeBarMaxPx = 50;
    public const int AgePanelHeightPx = 2 * AgeBarMaxPx + 1;
    public const int AgeZeroLineArgb = unchecked((int)0xFFBDBDBD);
    private const int AgeDayMinutes = 1440;

    private static readonly (int Minutes, int Px)[] AgeAnchors =
    {
        (AgeDayMinutes, 5),
        (2 * AgeDayMinutes, 10),
        (5 * AgeDayMinutes, 15),
        (10 * AgeDayMinutes, 20),
        (20 * AgeDayMinutes, 30),
        (40 * AgeDayMinutes, 40),
        (80 * AgeDayMinutes, 50),
    };

    public static int AgeBarHeightPx(int ageMinutes)
    {
        long a = Math.Abs((long)ageMinutes);
        if (a == 0) return 0;
        if (a >= AgeAnchors[^1].Minutes) return AgeBarMaxPx;
        var (firstMinutes, firstPx) = AgeAnchors[0];
        if (a <= firstMinutes)
            return Math.Max(1, (int)Math.Round(
                firstPx * Math.Log(1 + (double)a) / Math.Log(1 + (double)firstMinutes)));
        for (int i = 1; i < AgeAnchors.Length; i++)
        {
            var (hiMinutes, hiPx) = AgeAnchors[i];
            if (a > hiMinutes) continue;
            var (loMinutes, loPx) = AgeAnchors[i - 1];
            double t = Math.Log((double)a / loMinutes) / Math.Log((double)hiMinutes / loMinutes);
            return (int)Math.Round(loPx + (hiPx - loPx) * t);
        }
        return AgeBarMaxPx;
    }

    public static void DrawAgePanel(int[] buffer, int width, int height, AgeColumn[] columns,
        int bottomRow, int colorArgb)
    {
        int top = bottomRow - AgePanelHeightPx + 1;
        if (top < 0 || bottomRow >= height) return;
        int zeroRow = bottomRow - AgeBarMaxPx;
        for (int x = 0; x < width; x++)
        {
            buffer[zeroRow * width + x] = AgeZeroLineArgb;
            var col = x < columns.Length ? columns[x] : default;
            int upH = AgeBarHeightPx(col.Up);
            for (int row = zeroRow - upH; row < zeroRow; row++)
                buffer[row * width + x] = colorArgb;
            int downH = AgeBarHeightPx(col.Down);
            for (int row = zeroRow + 1; row <= zeroRow + downH; row++)
                buffer[row * width + x] = colorArgb;
        }
    }

    public const int SpreadBarMaxPx = 40;
    public const int SpreadPanelHeightPx = SpreadBarMaxPx + 1;
    public const int SpreadBaseLineArgb = unchecked((int)0xFFBDBDBD);

    public static int SpreadBarHeightPx(int tenths, double pointsPerRow)
    {
        if (tenths < 0) return 0;
        int px = pointsPerRow > 0
            ? (int)Math.Round(tenths / pointsPerRow)
            : (tenths + 5) / 10;
        return px < 1 ? 1 : px;
    }

    public static void DrawSpreadPanel(int[] buffer, int width, int height, int[] columns,
        int bottomRow, int colorArgb, double pointsPerRow)
    {
        int top = bottomRow - SpreadPanelHeightPx + 1;
        if (top < 0 || bottomRow >= height) return;
        for (int x = 0; x < width; x++)
        {
            buffer[bottomRow * width + x] = SpreadBaseLineArgb;
            int tenths = x < columns.Length ? columns[x] : -1;
            int h = Math.Min(SpreadBarHeightPx(tenths, pointsPerRow), bottomRow);
            for (int row = bottomRow - h; row < bottomRow; row++)
                buffer[row * width + x] = colorArgb;
        }
    }

    public const int VolumeBarMaxPx = 40;
    public const int VolumePanelHeightPx = VolumeBarMaxPx;

    public static double DrawVolumePanel(int[] buffer, int width, int height, VolumeColumnSet columns,
        int bottomRow, int colorArgb, double scale, int bidColorArgb, double unit)
    {
        int top = bottomRow - VolumePanelHeightPx + 1;
        if (top < 0 || bottomRow >= height) return 0;
        var total = columns.Total;
        if (!(unit > 0))
        {
            long max = 0;
            for (int x = 0; x < width && x < total.Length; x++)
                if (total[x] > max) max = total[x];
            if (max <= 0) return 0;
            unit = max;
        }
        for (int x = 0; x < width; x++)
        {
            long v = x < total.Length ? total[x] : -1;
            if (v < 0) continue;
            double px = v * VolumeBarMaxPx * scale / unit;
            int h = px >= bottomRow ? bottomRow : px < 1 ? 1 : (int)Math.Round(px);
            long bid = bidColorArgb != 0 && x < columns.Bid.Length ? columns.Bid[x] : 0;
            long sides = bid + (x < columns.Ask.Length ? columns.Ask[x] : 0);
            int bidRows = bid <= 0 || sides <= 0
                ? 0
                : Math.Min(h, (int)Math.Round((double)bid * h / sides));
            int barTop = bottomRow - h + 1;
            for (int row = barTop + bidRows; row <= bottomRow; row++)
                buffer[row * width + x] = colorArgb;
            for (int row = barTop; row < barTop + bidRows; row++)
                buffer[row * width + x] = bidColorArgb;
        }
        return unit;
    }

    public static void DrawVerticalDashed(int[] buffer, int width, int height,
        int x, int color, int on, int period)
    {
        if (x < 0 || x >= width) return;
        for (int y = 0; y < height; y++)
            if (period <= 1 || y % period < on)
                buffer[y * width + x] = color;
    }

    public static void DrawDownTriangle(int[] buffer, int width, int height,
        int x, int topY, int halfWidth, int rows, int color)
    {
        if (halfWidth < 0 || rows < 1 || x < 0 || x >= width) return;
        for (int row = 0; row < rows; row++)
        {
            int y = topY + row;
            if (y < 0) continue;
            if (y >= height) return;
            int span = rows == 1 ? 0 : (int)Math.Round(
                halfWidth * (double)(rows - 1 - row) / (rows - 1), MidpointRounding.AwayFromZero);
            int from = Math.Max(0, x - span);
            int to = Math.Min(width - 1, x + span);
            for (int px = from; px <= to; px++) buffer[y * width + px] = color;
        }
    }

    public static void DrawSegment(int[] buffer, int width, int height,
        double x0, double y0, double x1, double y1, int color)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double t0 = 0;
        double t1 = 1;
        if (!ClipT(-dx, x0 + 0.5, ref t0, ref t1)) return;
        if (!ClipT(dx, width - 0.5 - x0, ref t0, ref t1)) return;
        if (!ClipT(-dy, y0 + 0.5, ref t0, ref t1)) return;
        if (!ClipT(dy, height - 0.5 - y0, ref t0, ref t1)) return;
        double cx0 = x0 + t0 * dx;
        double cy0 = y0 + t0 * dy;
        double cx1 = x0 + t1 * dx;
        double cy1 = y0 + t1 * dy;
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(cx1 - cx0), Math.Abs(cy1 - cy0)));
        for (int i = 0; i <= steps; i++)
        {
            double f = steps == 0 ? 0 : (double)i / steps;
            int px = (int)Math.Floor(cx0 + (cx1 - cx0) * f + 0.5);
            int py = (int)Math.Floor(cy0 + (cy1 - cy0) * f + 0.5);
            if (px >= 0 && px < width && py >= 0 && py < height)
                buffer[py * width + px] = color;
        }
    }

    public const int DealWinArgb = unchecked((int)0xFF2E7D32);
    public const int DealLossArgb = unchecked((int)0xFFD32F2F);
    public const int DealConnectorAlpha = 140;
    public const int DealMarkerHalfPx = 3;

    public static void BlendSegment(int[] buffer, int width, int height,
        double x0, double y0, double x1, double y1, int color, int alpha)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double t0 = 0;
        double t1 = 1;
        if (!ClipT(-dx, x0 + 0.5, ref t0, ref t1)) return;
        if (!ClipT(dx, width - 0.5 - x0, ref t0, ref t1)) return;
        if (!ClipT(-dy, y0 + 0.5, ref t0, ref t1)) return;
        if (!ClipT(dy, height - 0.5 - y0, ref t0, ref t1)) return;
        double cx0 = x0 + t0 * dx;
        double cy0 = y0 + t0 * dy;
        double cx1 = x0 + t1 * dx;
        double cy1 = y0 + t1 * dy;
        int steps = (int)Math.Ceiling(Math.Max(Math.Abs(cx1 - cx0), Math.Abs(cy1 - cy0)));
        int lastPx = int.MinValue;
        int lastPy = int.MinValue;
        for (int i = 0; i <= steps; i++)
        {
            double f = steps == 0 ? 0 : (double)i / steps;
            int px = (int)Math.Floor(cx0 + (cx1 - cx0) * f + 0.5);
            int py = (int)Math.Floor(cy0 + (cy1 - cy0) * f + 0.5);
            if (px == lastPx && py == lastPy) continue;
            lastPx = px;
            lastPy = py;
            if (px >= 0 && px < width && py >= 0 && py < height)
            {
                int idx = py * width + px;
                buffer[idx] = Blend(buffer[idx], color, alpha);
            }
        }
    }

    public static void FillTriangle(int[] buffer, int width, int height,
        int cx, int cy, int half, bool pointsUp, int color)
    {
        for (int dy = -half; dy <= half; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= height) continue;
            int span = pointsUp ? (dy + half) / 2 : (half - dy) / 2;
            for (int dx = -span; dx <= span; dx++)
            {
                int x = cx + dx;
                if (x >= 0 && x < width) buffer[y * width + x] = color;
            }
        }
    }

    public static void FillRightTriangle(int[] buffer, int width, int height,
        int cx, int cy, int half, int color)
    {
        for (int dx = -half; dx <= half; dx++)
        {
            int x = cx + dx;
            if (x < 0 || x >= width) continue;
            int span = (half - dx) / 2;
            for (int dy = -span; dy <= span; dy++)
            {
                int y = cy + dy;
                if (y >= 0 && y < height) buffer[y * width + x] = color;
            }
        }
    }

    public static void FillDisc(int[] buffer, int width, int height,
        int cx, int cy, int radius, int color)
    {
        int r2 = radius * radius + radius / 2;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int x = cx + dx;
                if (x >= 0 && x < width) buffer[y * width + x] = color;
            }
        }
    }

    public const int ForecastBandAlpha = 38;
    public const int ForecastEdgeAlpha = 210;
    public const int ForecastMarkerHalfPx = 3;
    public const int ForecastMarkerHitPx = 4;
    public const int ForecastEndCapHalfPx = 3;
    public const int ForecastPointsPerPip = 10;
    public const int ForecastFillMaxPips = 40;
    public const int ForecastFillMaxPoints = ForecastFillMaxPips * ForecastPointsPerPip;

    public static void BlendPixel(int[] buffer, int width, int height,
        int x, int y, int color, int alpha)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        int idx = y * width + x;
        buffer[idx] = Blend(buffer[idx], color, alpha);
    }

    public static void BlendColumn(int[] buffer, int width, int height,
        int x, int yFrom, int yTo, int color, int alpha)
    {
        if (x < 0 || x >= width) return;
        int y0 = Math.Max(0, Math.Min(yFrom, yTo));
        int y1 = Math.Min(height - 1, Math.Max(yFrom, yTo));
        for (int y = y0; y <= y1; y++)
        {
            int idx = y * width + x;
            buffer[idx] = Blend(buffer[idx], color, alpha);
        }
    }

    public static void StrokeDisc(int[] buffer, int width, int height,
        int cx, int cy, int radius, int color)
    {
        int outer = radius * radius + radius / 2;
        int inner = (radius - 1) * (radius - 1);
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = cy + dy;
            if (y < 0 || y >= height) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int d = dx * dx + dy * dy;
                if (d > outer || d < inner) continue;
                int x = cx + dx;
                if (x >= 0 && x < width) buffer[y * width + x] = color;
            }
        }
    }

    private static int Blend(int dst, int src, int alpha)
    {
        int inv = 255 - alpha;
        int r = (((src >> 16) & 0xFF) * alpha + ((dst >> 16) & 0xFF) * inv) / 255;
        int g = (((src >> 8) & 0xFF) * alpha + ((dst >> 8) & 0xFF) * inv) / 255;
        int b = ((src & 0xFF) * alpha + (dst & 0xFF) * inv) / 255;
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    private static bool ClipT(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0) return q >= 0;
        double r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    private static void DrawLastPrice(int[] buffer, int width, int height,
        RenderLine line, long startBucket, double topPrice, double pointsPerRow)
    {
        long lastCol = line.LastBucket - startBucket;
        if (lastCol >= width) return;
        int y = (int)Math.Round((topPrice - line.OffsetPoints - line.LastPrice) / pointsPerRow);
        if (y < 0 || y >= height) return;
        int from = (int)Math.Max(0, lastCol + 1);
        int lastRow = Math.Min(height - 1, y + Math.Max(0, line.Width - 1));
        for (int row = y; row <= lastRow; row++)
        {
            int rowOff = row * width;
            for (int x = from; x < width; x++)
                buffer[rowOff + x] = line.Color;
        }
    }

    public const double WeekendGridShade = 0.85;

    private static int Shade(int argb, double factor)
    {
        int a = argb >> 24 & 0xFF;
        int r = (int)((argb >> 16 & 0xFF) * factor);
        int g = (int)((argb >> 8 & 0xFF) * factor);
        int b = (int)((argb & 0xFF) * factor);
        return a << 24 | r << 16 | g << 8 | b;
    }

    private static void DrawPriceLines(int[] buffer, int width, int height,
        double topPrice, double bottom, double pointsPerRow, int step, int color, bool dotted,
        bool[]? weekendMask)
    {
        int inc = dotted ? 2 : 1;
        int weekendColor = weekendMask == null ? color : Shade(color, WeekendGridShade);
        long firstLine = (long)Math.Ceiling(bottom / step) * step;
        for (long p = firstLine; p <= topPrice; p += step)
        {
            int y = (int)Math.Round((topPrice - p) / pointsPerRow);
            if (y < 0 || y >= height) continue;
            int rowOff = y * width;
            for (int x = 0; x < width; x += inc)
                buffer[rowOff + x] = weekendMask != null && weekendMask[x] ? weekendColor : color;
        }
    }

    private static void DrawGrid(int[] buffer, int width, int height,
        long columnSeconds, long startBucket, long[] columnEdges, double topPrice, double pointsPerRow,
        ChartPalette palette, TiltedGridSettings tiltedGrid, bool sessionBands)
    {
        long bucketSec = columnSeconds;
        bool bandsDrawn = sessionBands && SessionBandsVisible(columnSeconds);
        if (bandsDrawn)
        {
            for (int x = 0; x < width; x++)
            {
                long mid = columnEdges[x] + bucketSec / 2;
                int color = SessionClock.At(mid) switch
                {
                    ChartSession.Europe => palette.SessionEurope,
                    ChartSession.Overlap => palette.SessionOverlap,
                    ChartSession.America => palette.SessionAmerica,
                    _ => 0,
                };
                if (color == 0) continue;
                for (int row = 0; row < height; row++)
                    buffer[row * width + x] = color;
            }
        }
        bool[]? weekendMask = null;
        if (bucketSec < 2 * DaySeconds)
        {
            bool darkWeekend = bandsDrawn && palette.WeekendSession != 0;
            int weekendColor = darkWeekend ? palette.WeekendSession : palette.Weekend;
            if (darkWeekend) weekendMask = new bool[width];
            for (int x = 0; x < width; x++)
            {
                long mid = columnEdges[x] + bucketSec / 2;
                int dow = (int)((mid / DaySeconds + 4) % 7);
                if (dow != 0 && dow != 6) continue;
                if (weekendMask != null) weekendMask[x] = true;
                for (int row = 0; row < height; row++)
                    buffer[row * width + x] = weekendColor;
            }
        }
        int gridDayWeekend = Shade(palette.GridDay, WeekendGridShade);
        int gridMonthWeekend = Shade(palette.GridMonth, WeekendGridShade);
        int gridYearWeekend = Shade(palette.GridYear, WeekendGridShade);
        double bottom = topPrice - pointsPerRow * (height - 1);
        if (!tiltedGrid.Visible)
        {
            if (GridPrice10StepPoints / pointsPerRow >= MinPriceGridSpacingPixels)
                DrawPriceLines(buffer, width, height, topPrice, bottom, pointsPerRow, GridPrice10StepPoints, palette.GridPrice10, true, weekendMask);
            if (GridPrice50StepPoints / pointsPerRow >= MinPriceGridSpacingPixels)
                DrawPriceLines(buffer, width, height, topPrice, bottom, pointsPerRow, GridPrice50StepPoints, palette.GridPrice50, true, weekendMask);
        }
        DrawPriceLines(buffer, width, height, topPrice, bottom, pointsPerRow, GridPriceStepPoints, palette.GridPrice100, false, weekendMask);
        if (tiltedGrid.Visible)
            DrawTiltedGrid(buffer, width, height, columnSeconds, startBucket, topPrice, pointsPerRow,
                tiltedGrid, palette.GridTilted, palette.GridTiltedNear, weekendMask);
        if (MinuteGridVisible(columnSeconds))
        {
            for (int x = 0; x < width; x++)
            {
                long ts = columnEdges[x];
                long te = columnEdges[x + 1];
                if ((ts - 1) / MinuteSeconds == (te - 1) / MinuteSeconds) continue;
                int c = weekendMask != null && weekendMask[x] ? gridDayWeekend : palette.GridDay;
                for (int row = 0; row < height; row += 4)
                    buffer[row * width + x] = c;
            }
        }
        if (HourGridVisible(columnSeconds))
        {
            for (int x = 0; x < width; x++)
            {
                long ts = columnEdges[x];
                long te = columnEdges[x + 1];
                if ((ts - 1) / HourSeconds == (te - 1) / HourSeconds) continue;
                int c = weekendMask != null && weekendMask[x] ? gridDayWeekend : palette.GridDay;
                for (int row = 0; row < height; row += 2)
                    buffer[row * width + x] = c;
            }
        }
        bool drawDaily = DaySeconds >= MinDayGridSpacingPixels * bucketSec;
        for (int x = 0; x < width; x++)
        {
            long ts = columnEdges[x];
            long te = columnEdges[x + 1];
            if ((ts - 1) / DaySeconds == (te - 1) / DaySeconds) continue;
            var before = DateTimeOffset.FromUnixTimeSeconds(ts - 1).UtcDateTime;
            var after = DateTimeOffset.FromUnixTimeSeconds(te - 1).UtcDateTime;
            bool yearChanged = before.Year != after.Year;
            bool monthChanged = yearChanged || before.Month != after.Month;
            if (!drawDaily && !monthChanged) continue;
            bool onWeekend = weekendMask != null && weekendMask[x];
            int color = yearChanged
                ? (onWeekend ? gridYearWeekend : palette.GridYear)
                : monthChanged
                    ? (onWeekend ? gridMonthWeekend : palette.GridMonth)
                    : (onWeekend ? gridDayWeekend : palette.GridDay);
            for (int row = 0; row < height; row++)
                buffer[row * width + x] = color;
        }
    }

    public const int MaxTiltedLinesPerFamily = 2000;
    public const int TiltedSubLineMinSpacingPixels = 200;

    public static double TiltedLineSpacingPixels(double slope, double bucketSec, double pointsPerRow)
    {
        if (!(pointsPerRow > 0) || !double.IsFinite(slope)) return 0;
        double screenSlope = Math.Abs(slope) * bucketSec / pointsPerRow;
        return GridPriceStepPoints / pointsPerRow / Math.Sqrt(1 + screenSlope * screenSlope);
    }

    public static int TiltedStepPoints(double slope, double bucketSec, double pointsPerRow) =>
        TiltedLineSpacingPixels(slope, bucketSec, pointsPerRow) > TiltedSubLineMinSpacingPixels
            ? GridPrice50StepPoints
            : GridPriceStepPoints;

    private static void DrawTiltedGrid(int[] buffer, int width, int height,
        long columnSeconds, long startBucket, double topPrice, double pointsPerRow,
        TiltedGridSettings grid, int color, int nearColor, bool[]? weekendMask)
    {
        double bucketSec = columnSeconds;
        DrawTiltedFamily(buffer, width, height, startBucket, bucketSec, topPrice, pointsPerRow,
            grid.Up.Nearest ? grid.Down : grid.Up, color, nearColor, weekendMask);
        DrawTiltedFamily(buffer, width, height, startBucket, bucketSec, topPrice, pointsPerRow,
            grid.Up.Nearest ? grid.Up : grid.Down, color, nearColor, weekendMask);
    }

    private static void DrawTiltedFamily(int[] buffer, int width, int height,
        long startBucket, double bucketSec, double topPrice, double pointsPerRow,
        TiltedFamilySettings family, int color, int nearColor, bool[]? weekendMask)
    {
        if (!family.Visible) return;
        double anchorSeconds = family.AnchorSeconds;
        double anchorPoints = family.AnchorPoints;
        double slope = family.Slope;
        bool highlight = family.Nearest && nearColor != 0;
        if (!double.IsFinite(slope) || slope == 0) return;
        double BaseAt(double x) =>
            anchorPoints + slope * ((startBucket + x) * bucketSec - anchorSeconds);
        double left = BaseAt(-0.5);
        double right = BaseAt(width - 0.5);
        if (!double.IsFinite(left) || !double.IsFinite(right)) return;
        double lo = Math.Min(left, right);
        double hi = Math.Max(left, right);
        double bottom = topPrice - pointsPerRow * (height - 1);
        int step = TiltedStepPoints(slope, bucketSec, pointsPerRow);
        bool hasSubLines = step < GridPriceStepPoints;
        double first = Math.Floor((bottom - hi) / step) - 1;
        double last = Math.Ceiling((topPrice - lo) / step) + 1;
        if (!double.IsFinite(first) || !double.IsFinite(last)) return;
        if (last - first > MaxTiltedLinesPerFamily) return;
        for (long n = (long)first; n <= (long)last; n++)
            DrawTiltedLine(buffer, width, height, startBucket, bucketSec, topPrice, pointsPerRow,
                anchorSeconds, anchorPoints + n * (double)step, slope,
                highlight && n == family.NearestLine ? nearColor : color,
                hasSubLines && (n & 1) != 0, weekendMask);
    }

    private static void DrawTiltedLine(int[] buffer, int width, int height,
        long startBucket, double bucketSec, double topPrice, double pointsPerRow,
        double anchorSeconds, double linePoints, double slope, int color, bool dotted,
        bool[]? weekendMask)
    {
        int weekendColor = weekendMask == null ? color : Shade(color, WeekendGridShade);
        double RowAt(double x) =>
            (topPrice - (linePoints + slope * ((startBucket + x) * bucketSec - anchorSeconds)))
            / pointsPerRow;
        for (int x = 0; x < width; x++)
        {
            double yLo = RowAt(x - 0.5);
            double yHi = RowAt(x + 0.5);
            double top = Math.Min(yLo, yHi);
            double bot = Math.Max(yLo, yHi);
            if (!(bot >= 0) || !(top < height)) continue;
            int rowLo = (int)Math.Round(Math.Max(top, 0));
            int rowHi = (int)Math.Round(Math.Min(bot, height - 1));
            if (rowHi >= height) rowHi = height - 1;
            int c = weekendMask != null && weekendMask[x] ? weekendColor : color;
            for (int row = rowLo; row <= rowHi; row++)
                if (!dotted || ((x + row) & 1) == 0)
                    buffer[row * width + x] = c;
        }
    }
}
