using System.Windows;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Storage;

namespace Fx.WeekPuzzle;

internal enum PointKind { High, Low, Close, Edge }

internal sealed record WeekPoint(long Unix, double Value, PointKind Kind);

internal sealed record CardShape(
    DateTime Monday, double Width, double Height, double LeftTick, double RightTick, double PrevClose, WeekCandle Candle,
    IReadOnlyList<IReadOnlyList<Point>> Lines, double[] BarTop, double[] BarBottom, double WeekLine,
    IReadOnlyList<double> DayLines, IReadOnlyList<double> GridLines)
{
    public double Close => Candle.Close;
}

internal static class WeekCards
{
    public const double Width = 2000;
    public const double Margin = 30;
    public const double PxPerPip = 10.0;
    public const int PipPoints = 10;
    public const int GridPips = 100;
    public static readonly ZigZagLimits Limits = new(50 * PipPoints, 20 * PipPoints, 90);
    private const int WarmUpDays = 60;
    private const int MinWeekMinutes = 600;

    public static List<CardShape> Load(PuzzleOptions options)
    {
        var monday = MondayOf(options.FirstMonday);
        var minutes = MinuteFile.Read(options.DataRoot, options.Symbol, Unix(monday.AddDays(-WarmUpDays)));
        if (minutes.Count == 0)
            throw new InvalidOperationException($"no {options.Symbol} minutes around {monday:yyyy-MM-dd}");
        var drawing = options.Drawing.Length > 0 ? DrawingFile.Read(options.DataRoot, options.Drawing) : null;
        var zigzag = drawing is null ? ZigZagSymbol.BuildPoints(minutes, Limits) : new List<PivotPoint>();
        return Build(minutes, zigzag, drawing, monday, options.Weeks > 0 ? options.Weeks : int.MaxValue);
    }

    public static DateTime MondayOf(DateTime date)
    {
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(-1);
        return day;
    }

    public static List<CardShape> Build(List<Candle> all, List<PivotPoint> zigzag, PivotPoint[][]? drawing,
        DateTime firstMonday, int maxWeeks)
    {
        var kinds = Classify(zigzag);
        long dataEnd = all[^1].MinuteUnixSeconds + 60;
        var cards = new List<CardShape>();
        for (var monday = firstMonday; cards.Count < maxWeeks && DayEnd(monday.AddDays(4)) <= dataEnd; monday = monday.AddDays(7))
        {
            var card = TryBuild(all, zigzag, kinds, drawing, monday);
            if (card is not null) cards.Add(card);
        }
        return cards;
    }

    private static CardShape? TryBuild(List<Candle> all, List<PivotPoint> zigzag, PointKind[] kinds, PivotPoint[][]? drawing,
        DateTime monday)
    {
        long weekStart = DayEnd(monday.AddDays(-1));
        long weekEnd = DayEnd(monday.AddDays(4));
        int ws = LowerBound(all, weekStart);
        int we = LowerBound(all, weekEnd);
        if (we - ws < MinWeekMinutes || ws == 0) return null;

        var prevClose = DayClose(all, monday.AddDays(-3))
            ?? new WeekPoint(all[ws - 1].MinuteUnixSeconds, all[ws - 1].Avg, PointKind.Close);
        var closes = Enumerable.Range(0, 5).Select(d => DayClose(all, monday.AddDays(d))).OfType<WeekPoint>().ToList();
        if (closes.Count == 0) return null;

        var comp = WeekendCompressor.Instance;
        double vFrom = Math.Min(comp.ToVirtual(prevClose.Unix), comp.ToVirtual(weekStart));
        double vTo = Math.Max(comp.ToVirtual(closes[^1].Unix), comp.ToVirtual(weekEnd - 60));
        double left = Margin, right = Width - Margin;
        double Xv(double v) => left + (v - vFrom) / (vTo - vFrom) * (right - left);
        double X(long unix) => Xv(comp.ToVirtual(unix));

        var lines = drawing is null
            ? new List<List<(double V, double Value)>>
            {
                ZigZagLine(all, zigzag, kinds, monday, prevClose, weekEnd).Select(p => ((double)comp.ToVirtual(p.Unix), p.Value)).ToList(),
            }
            : ClipLines(drawing, comp, vFrom, vTo);

        double weekHigh = double.MinValue, weekLow = double.MaxValue;
        for (int i = ws; i < we; i++)
        {
            weekLow = Math.Min(weekLow, all[i].Min);
            weekHigh = Math.Max(weekHigh, all[i].Max);
        }
        double lo = Math.Min(weekLow, Math.Min(prevClose.Value, closes.Min(c => c.Value)));
        double hi = Math.Max(weekHigh, Math.Max(prevClose.Value, closes.Max(c => c.Value)));
        foreach (var line in lines)
            foreach (var (_, value) in line)
            {
                lo = Math.Min(lo, value);
                hi = Math.Max(hi, value);
            }
        double height = Math.Ceiling((hi - lo) / PipPoints * PxPerPip + 2 * Margin);
        double Y(double value) => Margin + (hi - value) / PipPoints * PxPerPip;

        int columns = (int)Math.Ceiling(right - left) + 1;
        var colMin = new int[columns];
        var colMax = new int[columns];
        Array.Fill(colMin, int.MaxValue);
        Array.Fill(colMax, int.MinValue);
        for (int i = ws; i < we; i++)
        {
            var c = all[i];
            int col = Math.Clamp((int)Math.Floor(X(c.MinuteUnixSeconds) - left), 0, columns - 1);
            colMin[col] = Math.Min(colMin[col], c.Min);
            colMax[col] = Math.Max(colMax[col], c.Max);
        }
        var barTop = new double[columns];
        var barBottom = new double[columns];
        for (int c = 0; c < columns; c++)
        {
            if (colMax[c] == int.MinValue)
            {
                barTop[c] = double.NaN;
                barBottom[c] = double.NaN;
                continue;
            }
            barTop[c] = Math.Floor(Y(colMax[c]));
            barBottom[c] = Math.Max(barTop[c] + 1, Math.Ceiling(Y(colMin[c])));
        }

        var shapes = lines
            .Select(line => (IReadOnlyList<Point>)line.Select(p => new Point(Xv(p.V), Y(p.Value))).ToList())
            .ToList();
        var dayLines = Enumerable.Range(1, 4).Select(d => Math.Round(X(DayEnd(monday.AddDays(d - 1)))) + 0.5).ToList();
        double weekLine = Math.Round(X(weekStart)) + 0.5;
        var gridLines = new List<double>();
        long step = GridPips * PipPoints;
        double topPrice = hi + Margin / PxPerPip * PipPoints;
        double bottomPrice = topPrice - height / PxPerPip * PipPoints;
        for (long g = (long)Math.Ceiling(bottomPrice / step) * step; g <= topPrice; g += step)
            gridLines.Add(Math.Round(Y(g)) + 0.5);
        var candle = new WeekCandle(all[ws].Avg, weekHigh, weekLow, closes[^1].Value);
        return new CardShape(monday, Width, height, Y(prevClose.Value), Y(candle.Close), prevClose.Value, candle,
            shapes, barTop, barBottom, weekLine, dayLines, gridLines);
    }

    private static List<List<(double V, double Value)>> ClipLines(PivotPoint[][] drawing, WeekendCompressor comp,
        double vFrom, double vTo)
    {
        var result = new List<List<(double V, double Value)>>();
        foreach (var line in drawing)
        {
            var current = new List<(double V, double Value)>();
            void Flush()
            {
                if (current.Count >= 2) result.Add(current);
                current = new List<(double V, double Value)>();
            }
            for (int i = 1; i < line.Length; i++)
            {
                double va = comp.ToVirtual(line[i - 1].UnixSeconds);
                double vb = comp.ToVirtual(line[i].UnixSeconds);
                double ya = line[i - 1].Value;
                double yb = line[i].Value;
                double t0 = 0, t1 = 1;
                if (va == vb)
                {
                    if (va < vFrom || va > vTo)
                    {
                        Flush();
                        continue;
                    }
                }
                else
                {
                    double ta = (vFrom - va) / (vb - va);
                    double tb = (vTo - va) / (vb - va);
                    t0 = Math.Max(0, Math.Min(ta, tb));
                    t1 = Math.Min(1, Math.Max(ta, tb));
                    if (t0 > t1)
                    {
                        Flush();
                        continue;
                    }
                }
                if (current.Count == 0) current.Add((va + t0 * (vb - va), ya + t0 * (yb - ya)));
                current.Add((va + t1 * (vb - va), ya + t1 * (yb - ya)));
                if (t1 < 1) Flush();
            }
            Flush();
        }
        return result;
    }

    private static List<WeekPoint> ZigZagLine(List<Candle> all, List<PivotPoint> zigzag, PointKind[] kinds, DateTime monday,
        WeekPoint prevClose, long weekEnd)
    {
        var points = new List<WeekPoint> { prevClose };
        for (int i = ZigZagSymbol.FirstAtOrAfter(zigzag, prevClose.Unix + 1);
             i < zigzag.Count && zigzag[i].UnixSeconds < weekEnd; i++)
            points.Add(new WeekPoint(zigzag[i].UnixSeconds, zigzag[i].Value, kinds[i]));
        for (int d = 0; d < 5; d++)
            if (DayClose(all, monday.AddDays(d)) is not null)
                points.AddRange(DayPoints(all, monday.AddDays(d)));
        return Merge(all, points);
    }

    private static WeekPoint? DayClose(List<Candle> all, DateTime day)
    {
        int a = LowerBound(all, DayEnd(day.AddDays(-1)));
        int b = LowerBound(all, DayEnd(day));
        if (b <= a) return null;
        return new WeekPoint(all[b - 1].MinuteUnixSeconds, all[b - 1].Avg, PointKind.Close);
    }

    private static List<WeekPoint> DayPoints(List<Candle> all, DateTime day)
    {
        int a = LowerBound(all, DayEnd(day.AddDays(-1)));
        int b = LowerBound(all, DayEnd(day));
        int hi = a, lo = a;
        for (int i = a + 1; i < b; i++)
        {
            if (all[i].Max > all[hi].Max) hi = i;
            if (all[i].Min < all[lo].Min) lo = i;
        }
        int close = b - 1;
        var result = new List<WeekPoint>
        {
            new(all[hi].MinuteUnixSeconds, all[hi].Max, PointKind.High),
            new(all[lo].MinuteUnixSeconds, all[lo].Min, PointKind.Low),
        };
        bool highLater = hi > lo || (hi == lo && !MaxFirst(all, hi));
        int best = -1;
        for (int i = (highLater ? hi : lo) + 1; i < close; i++)
            if (best < 0 || (highLater ? all[i].Min < all[best].Min : all[i].Max > all[best].Max)) best = i;
        if (best >= 0 && highLater && all[best].Min < all[close].Avg)
            result.Add(new WeekPoint(all[best].MinuteUnixSeconds, all[best].Min, PointKind.Low));
        if (best >= 0 && !highLater && all[best].Max > all[close].Avg)
            result.Add(new WeekPoint(all[best].MinuteUnixSeconds, all[best].Max, PointKind.High));
        result.Add(new WeekPoint(all[close].MinuteUnixSeconds, all[close].Avg, PointKind.Close));
        return result;
    }

    private static List<WeekPoint> Merge(List<Candle> all, List<WeekPoint> points)
    {
        var merged = new List<WeekPoint>();
        foreach (var group in points.GroupBy(p => p.Unix).OrderBy(g => g.Key))
        {
            int index = LowerBound(all, group.Key);
            bool maxFirst = index < all.Count && all[index].MinuteUnixSeconds == group.Key && MaxFirst(all, index);
            var ordered = group.Where(p => p.Kind != PointKind.Close)
                .OrderBy(p => maxFirst ? -p.Value : p.Value)
                .Concat(group.Where(p => p.Kind == PointKind.Close));
            foreach (var p in ordered)
            {
                if (merged.Count > 0 && merged[^1].Unix == p.Unix && merged[^1].Value == p.Value)
                {
                    if (merged[^1].Kind == PointKind.Edge) merged[^1] = p;
                    continue;
                }
                merged.Add(p);
            }
        }
        return merged;
    }

    private static bool MaxFirst(List<Candle> all, int index)
    {
        var c = all[index];
        if (index > 0)
        {
            long d = 2L * all[index - 1].Avg - c.Min - c.Max;
            if (d != 0) return d > 0;
        }
        return 2L * c.Avg < (long)c.Min + c.Max;
    }

    private static PointKind[] Classify(List<PivotPoint> zigzag)
    {
        var kinds = new PointKind[zigzag.Count];
        for (int i = 0; i < zigzag.Count; i++)
        {
            if (i == 0 || i == zigzag.Count - 1)
            {
                kinds[i] = PointKind.Edge;
                continue;
            }
            kinds[i] = zigzag[i].Value > zigzag[i + 1].Value ? PointKind.High : PointKind.Low;
        }
        return kinds;
    }

    public static long DayEnd(DateTime date)
    {
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        return Unix(day.AddHours(SessionClock.AmericaCloseHourUtc(day.AddHours(12))));
    }

    public static int LowerBound(List<Candle> all, long unix)
    {
        int lo = 0, hi = all.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (all[mid].MinuteUnixSeconds < unix) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();
}
