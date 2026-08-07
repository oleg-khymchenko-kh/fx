using System.Buffers.Binary;
using System.Globalization;

const int HeaderSize = 64;
const int RecordSize = 16;
const uint FlagFilled = 1u << 0;
const int PriceScale = 100000;
const int PipPoints = 10;
const int BaseWindowMinutes = 15;
const int MinBaseSamples = 5;
const int SpikeSearchMinutes = 90;

string repoRoot = @"C:\Users\Oleg\Documents\Oleg\fx";
string dataRoot = args.Length > 0 ? args[0] : Path.Combine(repoRoot, @"FXViewer\bin\Debug\net10.0-windows\data");
string symbol = args.Length > 1 ? args[1] : "EURUSD";
string reportsDir = Path.Combine(repoRoot, "reports");

var series = LoadSeries(Path.Combine(dataRoot, symbol));
if (series.Count == 0)
{
    Console.Error.WriteLine($"No {symbol} data under {dataRoot}");
    return 1;
}
Console.WriteLine($"{symbol}: years {series.Keys.Min()}..{series.Keys.Max()}");

var calendarTimes = ReadCalendarTimes(Path.Combine(dataRoot, "calendar"));
var meetings = ReadScheduledDates(Path.Combine(reportsDir, "events-fomc.csv"));
Console.WriteLine($"Meetings in schedule: {meetings.Count}");

var rows = new List<Row>();
var skipped = new List<string>();
foreach (var date in meetings)
{
    long unix = ReleaseUnix(date, calendarTimes);
    var utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

    double sum = 0;
    int samples = 0;
    for (int m = BaseWindowMinutes; m >= 1; m--)
        if (TryGet(series, unix - m * 60, out var c)) { sum += c.Avg; samples++; }
    if (samples < MinBaseSamples)
    {
        skipped.Add($"{date:yyyy-MM-dd} {utc:HH:mm} - no quotes before the release ({samples}/{BaseWindowMinutes} minutes)");
        continue;
    }
    double basePoints = sum / samples;

    long dayEnd = new DateTimeOffset(date.Year, date.Month, date.Day, 23, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    int hi = int.MinValue, lo = int.MaxValue, after = 0;
    for (long t = unix; t <= dayEnd; t += 60)
    {
        if (!TryGet(series, t, out var c)) continue;
        if (c.Max > hi) hi = c.Max;
        if (c.Min < lo) lo = c.Min;
        after++;
    }
    if (after == 0)
    {
        skipped.Add($"{date:yyyy-MM-dd} {utc:HH:mm} - no quotes after the release");
        continue;
    }

    int spikeOffset = 0, spikeRange = -1;
    for (int m = -SpikeSearchMinutes; m <= SpikeSearchMinutes; m++)
        if (TryGet(series, unix + m * 60, out var c) && c.Max - c.Min > spikeRange)
        { spikeRange = c.Max - c.Min; spikeOffset = m; }

    double up = Math.Max(0, (hi - basePoints) / PipPoints);
    double down = Math.Max(0, (basePoints - lo) / PipPoints);
    rows.Add(new Row(utc, basePoints / PriceScale, hi / (double)PriceScale, lo / (double)PriceScale,
        samples, after, up, down, Math.Max(up, down), spikeOffset, spikeRange / (double)PipPoints));
}

string csvPath = Path.Combine(reportsDir, $"{symbol.ToLowerInvariant()}-fomc-impact.csv");
using (var w = new StreamWriter(csvPath))
{
    w.WriteLine("date;releaseUtc;basePrice;dayHigh;dayLow;baseMinutes;afterMinutes;upPips;downPips;maxPips;spikeOffsetMin;spikeRangePips");
    foreach (var r in rows)
        w.WriteLine(string.Join(';',
            r.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.Utc.ToString("HH:mm", CultureInfo.InvariantCulture),
            F5(r.Base), F5(r.High), F5(r.Low), r.BaseMinutes, r.AfterMinutes,
            F1(r.Up), F1(r.Down), F1(r.Max), r.SpikeOffset, F1(r.SpikeRange)));
}

Console.WriteLine();
Console.WriteLine("date        rel     base      high      low          up    down     max   spike");
foreach (var r in rows)
    Console.WriteLine($"{r.Utc:yyyy-MM-dd} {r.Utc:HH:mm}  {r.Base:F5}  {r.High:F5}  {r.Low:F5}  " +
        $"{r.Up,6:F1}  {r.Down,6:F1}  {r.Max,6:F1}   {r.SpikeOffset,+4}m/{r.SpikeRange:F0}p");

Console.WriteLine();
Console.WriteLine($"Events with data: {rows.Count}");
Report("up", rows.Select(r => r.Up).ToArray());
Report("down", rows.Select(r => r.Down).ToArray());
Report("max", rows.Select(r => r.Max).ToArray());
Console.WriteLine();
foreach (var s in skipped) Console.WriteLine($"skipped: {s}");

Console.WriteLine();
Console.WriteLine("year   n   avgUp  avgDown   avgMax   minMax   maxMax");
var byYear = rows.GroupBy(r => r.Utc.Year).OrderBy(g => g.Key).ToList();
foreach (var g in byYear)
    Console.WriteLine($"{g.Key}  {g.Count(),2}  {g.Average(r => r.Up),6:F1}  {g.Average(r => r.Down),7:F1}  " +
        $"{g.Average(r => r.Max),7:F1}  {g.Min(r => r.Max),7:F1}  {g.Max(r => r.Max),7:F1}");

string yearCsv = Path.Combine(reportsDir, $"{symbol.ToLowerInvariant()}-fomc-impact-by-year.csv");
using (var w = new StreamWriter(yearCsv))
{
    w.WriteLine("year;events;avgUpPips;avgDownPips;avgMaxPips;minMaxPips;maxMaxPips");
    foreach (var g in byYear)
        w.WriteLine(string.Join(';', g.Key, g.Count(), F1(g.Average(r => r.Up)), F1(g.Average(r => r.Down)),
            F1(g.Average(r => r.Max)), F1(g.Min(r => r.Max)), F1(g.Max(r => r.Max))));
}

Console.WriteLine();
Console.WriteLine("maxPips distribution");
int[] edges = { 20, 40, 60, 80, 100, 150, int.MaxValue };
int prev = 0;
foreach (int e in edges)
{
    int n = rows.Count(r => r.Max >= prev && r.Max < e);
    string label = e == int.MaxValue ? $"{prev}+" : $"{prev}-{e}";
    Console.WriteLine($"{label,8}  {n,3}  {(double)n / rows.Count,6:P1}");
    prev = e;
}
Console.WriteLine();
Console.WriteLine($"CSV: {csvPath}");
Console.WriteLine($"CSV: {yearCsv}");
return 0;

static string F5(double v) => v.ToString("F5", CultureInfo.InvariantCulture);
static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

static void Report(string name, double[] values)
{
    if (values.Length == 0) return;
    var sorted = (double[])values.Clone();
    Array.Sort(sorted);
    Console.WriteLine($"{name,-5} avg={values.Average(),6:F1}  median={Pct(sorted, 0.5),6:F1}  " +
        $"p25={Pct(sorted, 0.25),6:F1}  p75={Pct(sorted, 0.75),6:F1}  p90={Pct(sorted, 0.90),6:F1}  " +
        $"min={sorted[0],6:F1}  max={sorted[^1],6:F1}");
}

static double Pct(double[] sorted, double q)
{
    double pos = q * (sorted.Length - 1);
    int i = (int)pos;
    return i + 1 < sorted.Length ? sorted[i] + (pos - i) * (sorted[i + 1] - sorted[i]) : sorted[i];
}

static long ReleaseUnix(DateOnly date, Dictionary<DateOnly, long> calendarTimes)
{
    if (date == new DateOnly(2020, 3, 3)) return Utc(date, 15, 0);
    if (date == new DateOnly(2020, 3, 15)) return Utc(date, 21, 0);
    if (date < new DateOnly(2013, 3, 20) && calendarTimes.TryGetValue(date, out long unix)) return unix;
    return new DateTimeOffset(date.Year, date.Month, date.Day, 14, 0, 0,
        TimeSpan.FromHours(IsUsDst(date) ? -4 : -5)).ToUnixTimeSeconds();
}

static long Utc(DateOnly d, int hour, int minute) =>
    new DateTimeOffset(d.Year, d.Month, d.Day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds();

static bool IsUsDst(DateOnly date) =>
    date >= NthWeekday(date.Year, 3, DayOfWeek.Sunday, 2) && date < NthWeekday(date.Year, 11, DayOfWeek.Sunday, 1);

static DateOnly NthWeekday(int year, int month, DayOfWeek day, int n)
{
    var d = new DateOnly(year, month, 1);
    while (d.DayOfWeek != day) d = d.AddDays(1);
    return d.AddDays(7 * (n - 1));
}

static bool TryGet(Dictionary<int, Year> series, long unix, out Candle candle)
{
    candle = default;
    var utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
    if (!series.TryGetValue(utc.Year, out var year)) return false;
    long start = new DateTimeOffset(utc.Year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    long index = (unix - start) / 60;
    if (index < 0 || index >= year.Filled.Length || !year.Filled[index]) return false;
    candle = new Candle(year.Min[index], year.Max[index], year.Avg[index]);
    return true;
}

static Dictionary<int, Year> LoadSeries(string symbolDir)
{
    var result = new Dictionary<int, Year>();
    if (!Directory.Exists(symbolDir)) return result;
    foreach (string path in Directory.GetFiles(symbolDir, "*.m1"))
    {
        if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int year)) continue;
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        int records = (bytes.Length - HeaderSize) / RecordSize;
        var y = new Year(new int[records], new int[records], new int[records], new bool[records]);
        for (int i = 0; i < records; i++)
        {
            int off = HeaderSize + i * RecordSize;
            if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 12)) & FlagFilled) == 0) continue;
            y.Min[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 0));
            y.Max[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 4));
            y.Avg[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 8));
            y.Filled[i] = true;
        }
        result[year] = y;
    }
    return result;
}

static Dictionary<DateOnly, long> ReadCalendarTimes(string calendarDir)
{
    var result = new Dictionary<DateOnly, long>();
    if (!Directory.Exists(calendarDir)) return result;
    foreach (string title in new[] { "Federal Funds Rate", "FOMC Statement" })
    {
        string needle = $"\"E\":\"{title}\"";
        foreach (string path in Directory.GetFiles(calendarDir, "*.jsonl").OrderBy(p => p))
        foreach (string line in File.ReadLines(path))
        {
            if (!line.Contains(needle, StringComparison.Ordinal)) continue;
            if (!line.Contains("\"C\":\"USD\"", StringComparison.Ordinal)) continue;
            int start = line.IndexOf("\"T\":", StringComparison.Ordinal);
            if (start < 0) continue;
            start += 4;
            int end = line.IndexOf(',', start);
            if (end < 0 || !long.TryParse(line.AsSpan(start, end - start), out long unix)) continue;
            var utc = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            result[DateOnly.FromDateTime(utc)] = unix / 60 * 60;
        }
    }
    return result;
}

static List<DateOnly> ReadScheduledDates(string csvPath)
{
    var result = new List<DateOnly>();
    if (!File.Exists(csvPath)) return result;
    foreach (string line in File.ReadLines(csvPath).Skip(1))
    {
        string[] parts = line.Split(';');
        if (parts.Length > 0 && DateOnly.TryParse(parts[0], CultureInfo.InvariantCulture, out var date))
            result.Add(date);
    }
    result.Sort();
    return result;
}

record struct Candle(int Min, int Max, int Avg);
record Year(int[] Min, int[] Max, int[] Avg, bool[] Filled);
record Row(DateTime Utc, double Base, double High, double Low, int BaseMinutes, int AfterMinutes,
    double Up, double Down, double Max, int SpikeOffset, double SpikeRange);
