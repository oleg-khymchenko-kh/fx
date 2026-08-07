using System.Buffers.Binary;
using System.Globalization;
using System.Text;

const int HeaderSize = 64;
const int RecordSize = 16;
const uint FlagFilled = 1u << 0;

const int WeekMinutes = 5 * 1440;
const int SmaHalfWindowMinutes = 120;
const int MinSmaSamples = 121;
const int BucketMinutes = 15;
const int WeekPoints = WeekMinutes / BucketMinutes;
const int DayPoints = 1440 / BucketMinutes;
const int PointsPerHour = 60 / BucketMinutes;
const int WindowDays = 3;
const int WindowPoints = WindowDays * DayPoints;
const int MaxShiftHours = 12;
const int CtxPad = MaxShiftHours * PointsPerHour;
const int MinOverlapPoints = WindowPoints * 6 / 10;
const double MinZoom = 0.6;
const double MaxZoom = 1.5;

string dataRoot = args.Length > 0
    ? args[0]
    : @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
string symbol = args.Length > 1 ? args[1] : "EURUSD";
DateOnly targetStart = args.Length > 2
    ? DateOnly.ParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture)
    : new DateOnly(2026, 7, 20);
int topCount = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 20;

string symbolDir = Path.Combine(dataRoot, symbol);
if (!Directory.Exists(symbolDir))
{
    Console.Error.WriteLine($"Symbol directory not found: {symbolDir}");
    return 1;
}

var rawWeeks = new SortedDictionary<DateOnly, List<(long Unix, int Avg)>>();

foreach (var (path, year) in Directory.GetFiles(symbolDir, "*.m1")
             .Select(p => (Path: p, Year: int.Parse(Path.GetFileNameWithoutExtension(p), CultureInfo.InvariantCulture)))
             .OrderBy(t => t.Year))
{
    byte[] bytes;
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
    }
    long yearStartUnix = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    int records = (bytes.Length - HeaderSize) / RecordSize;
    for (int i = 0; i < records; i++)
    {
        int off = HeaderSize + i * RecordSize;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 12));
        if ((flags & FlagFilled) == 0) continue;
        long unix = yearStartUnix + (long)i * 60;
        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime);
        var monday = TradingWeekMonday(date);
        long mondayUnix = new DateTimeOffset(monday.Year, monday.Month, monday.Day, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        if (unix < mondayUnix - 4 * 3600 || unix >= mondayUnix + 5 * 86400) continue;
        int avg = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 8));
        if (!rawWeeks.TryGetValue(monday, out var list))
            rawWeeks[monday] = list = new List<(long, int)>(WeekMinutes);
        list.Add((unix, avg));
    }
}

var weeks = new List<WeekSeries>();
long lastOpenOffset = -2 * 3600;
foreach (var (monday, list) in rawWeeks)
{
    long mondayUnix = new DateTimeOffset(monday.Year, monday.Month, monday.Day, 0, 0, 0, TimeSpan.Zero)
        .ToUnixTimeSeconds();
    long firstFilled = list[0].Unix;
    long open;
    if (firstFilled <= mondayUnix - 2 * 3600 + 300)
    {
        open = firstFilled;
        lastOpenOffset = firstFilled - mondayUnix;
    }
    else
        open = mondayUnix + lastOpenOffset;
    var minute = new double[WeekMinutes];
    Array.Fill(minute, double.NaN);
    foreach (var (unix, avg) in list)
    {
        int idx = (int)((unix - open) / 60);
        if (idx >= 0 && idx < WeekMinutes) minute[idx] = avg;
    }
    weeks.Add(new WeekSeries(monday, Downsample(Smooth(minute))));
}

var runs = new List<Run>();
Run? current = null;
foreach (var week in weeks)
{
    if (current is null || week.Monday != current.Weeks[^1].Monday.AddDays(7))
    {
        current = new Run();
        runs.Add(current);
    }
    current.Weeks.Add(week);
}
foreach (var run in runs)
{
    run.Points = new double[run.Weeks.Count * WeekPoints];
    for (int w = 0; w < run.Weeks.Count; w++)
    {
        Array.Copy(run.Weeks[w].Points, 0, run.Points, w * WeekPoints, WeekPoints);
        for (int d = 0; d < 5; d++)
            run.Days.Add((run.Weeks[w].Monday.AddDays(d), w * WeekPoints + d * DayPoints));
    }
}

Run? targetRun = null;
int targetDayIndex = -1;
foreach (var run in runs)
{
    int di = run.Days.FindIndex(d => d.Date == targetStart);
    if (di >= 0)
    {
        targetRun = run;
        targetDayIndex = di;
        break;
    }
}
if (targetRun is null || targetDayIndex + WindowDays > targetRun.Days.Count)
{
    Console.Error.WriteLine($"Target day {targetStart:yyyy-MM-dd} not found or not enough following days.");
    return 1;
}

int targetOffset = targetRun.Days[targetDayIndex].Offset;
var targetDates = Enumerable.Range(0, WindowDays).Select(d => targetRun.Days[targetDayIndex + d].Date).ToArray();
var a = new double[WindowPoints];
Array.Copy(targetRun.Points, targetOffset, a, 0, WindowPoints);
int targetCoverage = a.Count(v => !double.IsNaN(v));
if (targetCoverage < MinOverlapPoints)
{
    Console.Error.WriteLine($"Target window has too little data: {targetCoverage}/{WindowPoints} points.");
    return 1;
}

Console.WriteLine($"Symbol:        {symbol}");
Console.WriteLine($"Target days:   {targetDates[0]:yyyy-MM-dd} .. {targetDates[^1]:yyyy-MM-dd} ({targetCoverage}/{WindowPoints} points)");

var selfCtx = SliceCtx(targetRun, targetOffset, int.MaxValue);
var self = BestMatch(a, selfCtx);
if (self is not { ShiftHours: 0, Mirrored: false } s0 || Math.Abs(s0.Zoom - 1) > 1e-6 || Math.Abs(s0.Score - 1) > 1e-6)
{
    Console.Error.WriteLine($"Self-test FAILED: {self}");
    return 1;
}
Console.WriteLine($"Self-test:     ok (score={self.Value.Score:F4}, shift={self.Value.ShiftHours}, zoom={self.Value.Zoom:F2})");

var results = new List<Result>();
foreach (var run in runs)
{
    for (int di = 0; di + WindowDays <= run.Days.Count; di++)
    {
        if (run.Days[di + WindowDays - 1].Date >= targetStart) break;
        int clipAt = run == targetRun ? targetOffset : int.MaxValue;
        var m = BestMatch(a, SliceCtx(run, run.Days[di].Offset, clipAt));
        if (m is not null)
            results.Add(new Result(run, di, run.Days[di].Date, run.Days[di + WindowDays - 1].Date, m.Value));
    }
}
results.Sort((x, y) => y.M.Sim.CompareTo(x.M.Sim));
Console.WriteLine($"Candidates:    {results.Count} windows of {WindowDays} consecutive trading days");
Console.WriteLine();

var top = new List<Result>();
foreach (var r in results)
{
    if (top.Count >= topCount) break;
    if (top.Any(t => t.Run == r.Run && Math.Abs(t.DayIndex - r.DayIndex) < WindowDays)) continue;
    top.Add(r);
}

Console.WriteLine($"rank  days                      sim     score   rho     zoom  shift  mir  overlap");
for (int i = 0; i < top.Count; i++)
{
    var r = top[i];
    Console.WriteLine(
        $"{i + 1,4}  {r.Start:yyyy-MM-dd} .. {r.End:yyyy-MM-dd}  {r.M.Sim,6:F4}  {r.M.Score,6:F4}  {r.M.Rho,6:F4}  {r.M.Zoom,4:F2}  {r.M.ShiftHours,4:+0;-0;0}h  {(r.M.Mirrored ? "M" : " "),3}  {r.M.Overlap,4}/{WindowPoints}");
}

string reportsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
Directory.CreateDirectory(reportsDir);
string baseName = $"{symbol.ToLowerInvariant()}-similar-days-{targetStart:yyyy-MM-dd}";

string csvPath = Path.Combine(reportsDir, baseName + ".csv");
using (var writer = new StreamWriter(csvPath))
{
    writer.WriteLine("startDay;endDay;sim;score;rho;zoom;shiftHours;mirror;overlapPoints");
    foreach (var r in results)
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{r.Start:yyyy-MM-dd};{r.End:yyyy-MM-dd};{r.M.Sim:F4};{r.M.Score:F4};{r.M.Rho:F4};{r.M.Zoom:F2};{r.M.ShiftHours};{(r.M.Mirrored ? 1 : 0)};{r.M.Overlap}"));
}
Console.WriteLine();
Console.WriteLine($"CSV:  {csvPath} ({results.Count} windows)");

string htmlPath = Path.Combine(reportsDir, baseName + ".html");
File.WriteAllText(htmlPath, BuildHtml(top.Take(5).ToList()));
Console.WriteLine($"HTML: {htmlPath} (top 5 overlays, same-episode duplicates collapsed)");
return 0;

static DateOnly TradingWeekMonday(DateOnly d) => d.DayOfWeek switch
{
    DayOfWeek.Sunday => d.AddDays(1),
    DayOfWeek.Saturday => d.AddDays(-5),
    _ => d.AddDays(-((int)d.DayOfWeek - 1)),
};

static double[] Smooth(double[] minute)
{
    int n = minute.Length;
    var sum = new double[n + 1];
    var cnt = new int[n + 1];
    for (int i = 0; i < n; i++)
    {
        bool has = !double.IsNaN(minute[i]);
        sum[i + 1] = sum[i] + (has ? minute[i] : 0);
        cnt[i + 1] = cnt[i] + (has ? 1 : 0);
    }
    var smoothed = new double[n];
    for (int i = 0; i < n; i++)
    {
        int lo = Math.Max(0, i - SmaHalfWindowMinutes);
        int hi = Math.Min(n - 1, i + SmaHalfWindowMinutes);
        int c = cnt[hi + 1] - cnt[lo];
        smoothed[i] = c >= MinSmaSamples ? (sum[hi + 1] - sum[lo]) / c : double.NaN;
    }
    return smoothed;
}

static double[] Downsample(double[] smoothed)
{
    var points = new double[WeekPoints];
    for (int p = 0; p < WeekPoints; p++)
    {
        double acc = 0;
        int c = 0;
        for (int i = p * BucketMinutes; i < (p + 1) * BucketMinutes; i++)
        {
            if (double.IsNaN(smoothed[i])) continue;
            acc += smoothed[i];
            c++;
        }
        points[p] = c > 0 ? acc / c : double.NaN;
    }
    return points;
}

static double[] SliceCtx(Run run, int offset, int clipAt)
{
    var ctx = new double[WindowPoints + 2 * CtxPad];
    for (int i = 0; i < ctx.Length; i++)
    {
        int idx = offset - CtxPad + i;
        ctx[i] = idx >= 0 && idx < run.Points.Length && idx < clipAt ? run.Points[idx] : double.NaN;
    }
    return ctx;
}

static MatchResult? BestMatch(double[] a, double[] bctx)
{
    MatchResult? best = null;
    for (int shift = -MaxShiftHours; shift <= MaxShiftHours; shift++)
    {
        int s = shift * PointsPerHour;
        int n = 0;
        double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
        for (int i = 0; i < WindowPoints; i++)
        {
            double va = a[i], vb = bctx[i - s + CtxPad];
            if (double.IsNaN(va) || double.IsNaN(vb)) continue;
            n++;
            sumA += va;
            sumB += vb;
            sumAA += va * va;
            sumBB += vb * vb;
            sumAB += va * vb;
        }
        if (n < MinOverlapPoints) continue;
        double meanA = sumA / n, meanB = sumB / n;
        double sxx = sumAA - n * meanA * meanA;
        double syy = sumBB - n * meanB * meanB;
        double sxy = sumAB - n * meanA * meanB;
        if (sxx <= 0 || syy <= 0) continue;
        double rho = sxy / Math.Sqrt(sxx * syy);
        for (int mirror = 0; mirror < 2; mirror++)
        {
            double sxyM = mirror == 0 ? sxy : -sxy;
            double zoom = Math.Clamp(sxyM / syy, MinZoom, MaxZoom);
            double residual = sxx - 2 * zoom * sxyM + zoom * zoom * syy;
            double score = 1 - residual / sxx;
            double sim = score * Math.Sqrt((double)n / WindowPoints);
            if (best is null || sim > best.Value.Sim)
                best = new MatchResult(shift, n, mirror == 0 ? rho : -rho, zoom, score, sim, meanA, meanB,
                    mirror == 1);
        }
    }
    return best;
}

string BuildHtml(List<Result> top)
{
    const int W = 960, H = 300, Pad = 40;
    var sb = new StringBuilder();
    sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
    sb.AppendLine($"<title>{symbol} similar days - {targetStart:yyyy-MM-dd}</title>");
    sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;background:#fafafa;color:#222;margin:24px}" +
                  "h1{font-size:20px}h2{font-size:15px;margin:28px 0 4px}" +
                  "svg{background:#fff;border:1px solid #ddd}.meta{color:#666;font-size:13px}</style></head><body>");
    sb.AppendLine($"<h1>{symbol}: {WindowDays} trading days similar to {targetDates[0]:yyyy-MM-dd} .. {targetDates[^1]:yyyy-MM-dd}</h1>");
    sb.AppendLine($"<p class=\"meta\">Blue = target days. " +
                  "Orange = matched days, shifted, re-centered, zoomed and (if marked MIRROR) flipped vertically. " +
                  "Y axis: pips relative to the overlap mean. X axis: target trading days from session open.</p>");
    for (int rank = 0; rank < top.Count; rank++)
    {
        var r = top[rank];
        var m = r.M;
        var ctx = SliceCtx(r.Run, r.Run.Days[r.DayIndex].Offset, r.Run == targetRun ? targetOffset : int.MaxValue);
        var aPlot = new double[WindowPoints];
        var bPlot = new double[WindowPoints];
        Array.Fill(aPlot, double.NaN);
        Array.Fill(bPlot, double.NaN);
        int s = m.ShiftHours * PointsPerHour;
        for (int i = 0; i < WindowPoints; i++)
        {
            if (!double.IsNaN(a[i])) aPlot[i] = (a[i] - m.MeanA) / 10;
            double vb = ctx[i - s + CtxPad];
            if (!double.IsNaN(vb)) bPlot[i] = m.Zoom * (vb - m.MeanB) * (m.Mirrored ? -1 : 1) / 10;
        }
        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in aPlot.Concat(bPlot))
        {
            if (double.IsNaN(v)) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        double span = Math.Max(1, max - min);
        double X(int i) => Pad + (double)i / (WindowPoints - 1) * (W - 2 * Pad);
        double Y(double v) => Pad + (max - v) / span * (H - 2 * Pad);
        sb.AppendLine($"<h2>#{rank + 1} &nbsp; {r.Start:yyyy-MM-dd} .. {r.End:yyyy-MM-dd} &nbsp; sim {m.Sim:F4} &nbsp; " +
                      $"rho {m.Rho:F3} &nbsp; zoom {m.Zoom:F2}x &nbsp; shift {m.ShiftHours:+0;-0;0}h &nbsp; " +
                      $"{(m.Mirrored ? "MIRROR &nbsp; " : "")}overlap {m.Overlap}/{WindowPoints}</h2>");
        sb.AppendLine($"<svg width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\">");
        for (int d = 0; d <= WindowDays; d++)
        {
            double x = X(Math.Min(WindowPoints - 1, d * DayPoints));
            sb.AppendLine(FormattableString.Invariant(
                $"<line x1=\"{x:F1}\" y1=\"{Pad}\" x2=\"{x:F1}\" y2=\"{H - Pad}\" stroke=\"#eee\"/>"));
            if (d < WindowDays)
                sb.AppendLine(FormattableString.Invariant(
                    $"<text x=\"{x + 4:F1}\" y=\"{H - Pad + 16}\" font-size=\"11\" fill=\"#999\">{targetDates[d]:ddd dd MMM}</text>"));
        }
        double y0 = Y(0);
        sb.AppendLine(FormattableString.Invariant(
            $"<line x1=\"{Pad}\" y1=\"{y0:F1}\" x2=\"{W - Pad}\" y2=\"{y0:F1}\" stroke=\"#ccc\" stroke-dasharray=\"4 3\"/>"));
        sb.AppendLine(FormattableString.Invariant(
            $"<text x=\"4\" y=\"{Pad + 4}\" font-size=\"11\" fill=\"#999\">{max:F0} pips</text>"));
        sb.AppendLine(FormattableString.Invariant(
            $"<text x=\"4\" y=\"{H - Pad}\" font-size=\"11\" fill=\"#999\">{min:F0}</text>"));
        AppendPolylines(sb, aPlot, X, Y, "#2b6cb0");
        AppendPolylines(sb, bPlot, X, Y, "#dd6b20");
        sb.AppendLine("</svg>");
    }
    sb.AppendLine("</body></html>");
    return sb.ToString();
}

static void AppendPolylines(StringBuilder sb, double[] vals, Func<int, double> x, Func<double, double> y, string color)
{
    var seg = new StringBuilder();
    for (int i = 0; i <= vals.Length; i++)
    {
        if (i < vals.Length && !double.IsNaN(vals[i]))
        {
            seg.Append(FormattableString.Invariant($"{x(i):F1},{y(vals[i]):F1} "));
            continue;
        }
        if (seg.Length > 0)
            sb.AppendLine($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1.6\" points=\"{seg}\"/>");
        seg.Clear();
    }
}

sealed record WeekSeries(DateOnly Monday, double[] Points);

sealed class Run
{
    public List<WeekSeries> Weeks { get; } = new();
    public double[] Points { get; set; } = Array.Empty<double>();
    public List<(DateOnly Date, int Offset)> Days { get; } = new();
}

sealed record Result(Run Run, int DayIndex, DateOnly Start, DateOnly End, MatchResult M);

readonly record struct MatchResult(
    int ShiftHours, int Overlap, double Rho, double Zoom, double Score, double Sim, double MeanA, double MeanB,
    bool Mirrored);
