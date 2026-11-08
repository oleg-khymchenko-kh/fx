using System.Globalization;
using System.Windows;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Storage;

namespace Fx.WeekPuzzle;

internal sealed record DayBand(double X0, double X1, ChartSession Session);

internal sealed record DayGridLine(double Y, int Pips);

internal sealed record DayHourLine(double X, bool Midnight);

internal sealed record DayHour(long Unix, int Open, int High, int Low, int Close);

internal sealed record DayShape(
    string Id, DateTime Date, long Start, long End, double Width, double Height,
    WeekCandle Candle, long HighUnix, long LowUnix,
    IReadOnlyList<IReadOnlyList<Point>> Lines, IReadOnlyList<IReadOnlyList<(double Unix, double Value)>> RawLines,
    double[] BarTop, double[] BarBottom, IReadOnlyList<DayBand> Bands, IReadOnlyList<DayHourLine> HourLines,
    IReadOnlyList<DayGridLine> GridLines, IReadOnlyList<DayHour> Hours)
{
    public string Label => Date.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal static class DayCards
{
    public const double Scale = 4;
    public const double PxPerHour = 10 * Scale;
    public const double PxPerPip = 2.5 * Scale;
    public const double Header = 16 * Scale;
    public const double Margin = 5 * Scale;
    public const int BarMinutes = 1;
    public const double BarWidth = PxPerHour * BarMinutes / 60;
    private const int PipPoints = WeekCards.PipPoints;
    private const long HourSeconds = 3600;
    private const long DaySeconds = 86400;
    private const int MinDayMinutes = 300;
    private const int LookBackDays = 3;
    private const int ZigZagWarmUpDays = 60;

    public static List<DayShape> Load(PuzzleOptions options, DateTime firstDay, int maxDays)
    {
        var first = DateTime.SpecifyKind(firstDay.Date, DateTimeKind.Utc);
        bool zigzag = options.Drawing.Length == 0;
        var minutes = MinuteFile.Read(options.DataRoot, options.Symbol,
            WeekCards.Unix(first.AddDays(-(zigzag ? ZigZagWarmUpDays : LookBackDays))));
        if (minutes.Count == 0)
            throw new InvalidOperationException($"no {options.Symbol} minutes after {first:yyyy-MM-dd}");
        var drawing = zigzag
            ? new[] { ZigZagSymbol.BuildPoints(minutes, WeekCards.Limits).ToArray() }
            : DrawingFile.Read(options.DataRoot, options.Drawing);
        return Build(minutes, drawing, first, maxDays > 0 ? maxDays : int.MaxValue);
    }

    public static List<DayShape> Build(List<Candle> all, PivotPoint[][] drawing, DateTime firstDay, int maxDays)
    {
        long dataEnd = all[^1].MinuteUnixSeconds + 60;
        var cards = new List<DayShape>();
        for (var day = firstDay; cards.Count < maxDays && WeekCards.DayEnd(day) <= dataEnd; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var card = TryBuild(all, drawing, day);
            if (card is not null) cards.Add(card);
        }
        return cards;
    }

    private static DayShape? TryBuild(List<Candle> all, PivotPoint[][] drawing, DateTime day)
    {
        long start = WeekCards.DayEnd(day.AddDays(-1));
        long end = WeekCards.DayEnd(day);
        int a = WeekCards.LowerBound(all, start);
        int b = WeekCards.LowerBound(all, end);
        if (b - a < MinDayMinutes) return null;

        var raw = WeekCards.ClipLines(drawing, unix => unix, start, end);
        int high = a, low = a;
        for (int i = a + 1; i < b; i++)
        {
            if (all[i].Max > all[high].Max) high = i;
            if (all[i].Min < all[low].Min) low = i;
        }
        double hi = all[high].Max, lo = all[low].Min;
        foreach (var line in raw)
            foreach (var (_, value) in line)
            {
                lo = Math.Min(lo, value);
                hi = Math.Max(hi, value);
            }

        double width = (end - start) / (double)HourSeconds * PxPerHour;
        double height = Math.Ceiling(Header + 2 * Margin + (hi - lo) / PipPoints * PxPerPip);
        double X(double unix) => (unix - start) / HourSeconds * PxPerHour;
        double Y(double value) => Header + Margin + (hi - value) / PipPoints * PxPerPip;

        int columns = (int)Math.Ceiling((end - start) / 60.0 / BarMinutes);
        var colMin = new int[columns];
        var colMax = new int[columns];
        Array.Fill(colMin, int.MaxValue);
        Array.Fill(colMax, int.MinValue);
        for (int i = a; i < b; i++)
        {
            int col = Math.Clamp((int)((all[i].MinuteUnixSeconds - start) / 60 / BarMinutes), 0, columns - 1);
            colMin[col] = Math.Min(colMin[col], all[i].Min);
            colMax[col] = Math.Max(colMax[col], all[i].Max);
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
            barTop[c] = Y(colMax[c]);
            barBottom[c] = Math.Max(barTop[c] + 1, Y(colMin[c]));
        }

        long midnight = WeekCards.Unix(day);
        var bands = new List<DayBand>();
        for (long h = start; h < end; h += HourSeconds)
        {
            long mid = h + HourSeconds / 2;
            var session = mid < midnight ? ChartSession.Closed : SessionClock.At(mid);
            if (session == ChartSession.None) continue;
            double x0 = X(h), x1 = X(Math.Min(h + HourSeconds, end));
            if (bands.Count > 0 && bands[^1].Session == session && bands[^1].X1 == x0) bands[^1] = bands[^1] with { X1 = x1 };
            else bands.Add(new DayBand(x0, x1, session));
        }

        var hourLines = new List<DayHourLine>();
        for (long h = start + HourSeconds; h < end; h += HourSeconds)
            hourLines.Add(new DayHourLine(X(h), h % DaySeconds == 0));

        var gridLines = new List<DayGridLine>();
        long step = 10 * PipPoints;
        double topPrice = hi + Margin / PxPerPip * PipPoints;
        double bottomPrice = topPrice - (height - Header) / PxPerPip * PipPoints;
        for (long g = (long)Math.Ceiling(bottomPrice / step) * step; g <= topPrice; g += step)
            gridLines.Add(new DayGridLine(Y(g), g % (100 * PipPoints) == 0 ? 100 : g % (50 * PipPoints) == 0 ? 50 : 10));

        var hours = new List<DayHour>();
        for (int i = a; i < b;)
        {
            long hour = start + (all[i].MinuteUnixSeconds - start) / HourSeconds * HourSeconds;
            int j = i, max = int.MinValue, min = int.MaxValue;
            for (; j < b && all[j].MinuteUnixSeconds < hour + HourSeconds; j++)
            {
                max = Math.Max(max, all[j].Max);
                min = Math.Min(min, all[j].Min);
            }
            hours.Add(new DayHour(hour, all[i].Avg, max, min, all[j - 1].Avg));
            i = j;
        }

        var lines = raw
            .Select(line => (IReadOnlyList<Point>)line.Select(p => new Point(X(p.V), Y(p.Value))).ToList())
            .ToList();
        var rawLines = raw
            .Select(line => (IReadOnlyList<(double Unix, double Value)>)line.Select(p => (p.V, p.Value)).ToList())
            .ToList();
        var candle = new WeekCandle(all[a].Avg, all[high].Max, all[low].Min, all[b - 1].Avg);
        return new DayShape(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day, start, end, width, height,
            candle, all[high].MinuteUnixSeconds, all[low].MinuteUnixSeconds, lines, rawLines, barTop, barBottom,
            bands, hourLines, gridLines, hours);
    }
}
