using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

const int HeaderSize = 64;
const int RecordSize = 16;
const uint FlagFilled = 1u << 0;

const int StopLoss = 200;
const int TakeProfit = 500;

string dataRoot = args.Length > 0
    ? args[0]
    : @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
string symbol = args.Length > 1 ? args[1] : "EURUSD";

string symbolDir = Path.Combine(dataRoot, symbol);
if (!Directory.Exists(symbolDir))
{
    Console.Error.WriteLine($"Symbol directory not found: {symbolDir}");
    return 1;
}

var yearFiles = Directory.GetFiles(symbolDir, "*.m1")
    .Select(p => (Path: p, Year: int.Parse(Path.GetFileNameWithoutExtension(p), CultureInfo.InvariantCulture)))
    .OrderBy(t => t.Year)
    .ToList();

var min = new List<int>();
var max = new List<int>();
var avg = new List<int>();
var time = new List<long>();

foreach (var (path, year) in yearFiles)
{
    byte[] bytes;
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
    }
    long yearStartUnix = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    int records = (bytes.Length - HeaderSize) / RecordSize;
    int filledThisYear = 0;
    for (int i = 0; i < records; i++)
    {
        int off = HeaderSize + i * RecordSize;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 12));
        if ((flags & FlagFilled) == 0) continue;
        min.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 0)));
        max.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 4)));
        avg.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 8)));
        time.Add(yearStartUnix + (long)i * 60);
        filledThisYear++;
    }
    Console.WriteLine($"{year}: {filledThisYear:N0} minutes");
}

int[] lo = min.ToArray();
int[] hi = max.ToArray();
int[] mid = avg.ToArray();
long[] ts = time.ToArray();
int n = mid.Length;

Console.WriteLine($"Total filled minutes: {n:N0}");
Console.WriteLine("Running backtest...");

var sw = Stopwatch.StartNew();
long confirmed = 0;
long unresolved = 0;
var weeks = new SortedDictionary<DateOnly, (long Minutes, long Confirmed)>();
var days = new SortedDictionary<DateOnly, DayAgg>();

static DateOnly TradingWeekMonday(DateOnly d) => d.DayOfWeek switch
{
    DayOfWeek.Sunday => d.AddDays(1),
    DayOfWeek.Saturday => d.AddDays(-5),
    _ => d.AddDays(-((int)d.DayOfWeek - 1)),
};

for (int i = 0; i < n; i++)
{
    int entry = mid[i];

    int longProfit = entry + TakeProfit;
    int longStop = entry - StopLoss;
    int longOutcome = 0;
    for (int j = i + 1; j < n; j++)
    {
        bool tp = hi[j] >= longProfit;
        bool sl = lo[j] <= longStop;
        if (tp || sl)
        {
            longOutcome = (tp && !sl) ? 1 : -1;
            break;
        }
    }

    bool isConfirmed = longOutcome == 1;
    if (!isConfirmed)
    {
        int shortProfit = entry - TakeProfit;
        int shortStop = entry + StopLoss;
        int shortOutcome = 0;
        for (int j = i + 1; j < n; j++)
        {
            bool tp = lo[j] <= shortProfit;
            bool sl = hi[j] >= shortStop;
            if (tp || sl)
            {
                shortOutcome = (tp && !sl) ? 1 : -1;
                break;
            }
        }

        isConfirmed = shortOutcome == 1;
        if (!isConfirmed && longOutcome == 0 && shortOutcome == 0)
            unresolved++;
    }

    if (isConfirmed) confirmed++;
    var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ts[i]).UtcDateTime);
    var week = TradingWeekMonday(date);
    var agg = weeks.TryGetValue(week, out var w) ? w : (0, 0);
    weeks[week] = (agg.Item1 + 1, agg.Item2 + (isConfirmed ? 1 : 0));

    if (!days.TryGetValue(date, out var day))
    {
        day = new DayAgg { FirstAvg = mid[i], Lo = lo[i], Hi = hi[i] };
        days[date] = day;
    }
    day.Minutes++;
    if (isConfirmed) day.Confirmed++;
    day.LastAvg = mid[i];
    if (lo[i] < day.Lo) day.Lo = lo[i];
    if (hi[i] > day.Hi) day.Hi = hi[i];
    day.SumRange += hi[i] - lo[i];
    if (day.HasPrev) day.SumAbsDelta += Math.Abs(mid[i] - day.PrevAvg);
    day.PrevAvg = mid[i];
    day.HasPrev = true;

    if ((i & 0x7FFFF) == 0 && i > 0)
        Console.WriteLine($"  {i:N0}/{n:N0}  confirmed={confirmed:N0}  elapsed={sw.Elapsed.TotalSeconds:F1}s");
}

sw.Stop();

Console.WriteLine();
Console.WriteLine("==================== RESULT ====================");
Console.WriteLine($"Symbol:              {symbol}");
Console.WriteLine($"Total minutes:       {n:N0}");
Console.WriteLine($"Confirmed entries:   {confirmed:N0}");
Console.WriteLine($"Not confirmed:       {n - confirmed:N0}");
Console.WriteLine($"  (of which tail unresolved, ran out of data: {unresolved:N0})");
Console.WriteLine($"Confirmed share:     {(double)confirmed / n:P2}");
Console.WriteLine($"Elapsed:             {sw.Elapsed.TotalSeconds:F1}s");

string reportsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports");
reportsDir = Path.GetFullPath(reportsDir);
Directory.CreateDirectory(reportsDir);
string csvPath = Path.Combine(reportsDir, $"{symbol.ToLowerInvariant()}-weekly-confirmation.csv");
using (var writer = new StreamWriter(csvPath))
{
    writer.WriteLine("weekMonday;minutes;confirmed");
    foreach (var (week, w) in weeks)
        writer.WriteLine($"{week:yyyy-MM-dd};{w.Minutes};{w.Confirmed}");
}
Console.WriteLine($"Weekly CSV:          {csvPath} ({weeks.Count} weeks)");

string dailyPath = Path.Combine(reportsDir, $"{symbol.ToLowerInvariant()}-daily.csv");
using (var writer = new StreamWriter(dailyPath))
{
    writer.WriteLine("date;minutes;confirmed;firstAvg;lastAvg;lo;hi;sumRange;sumAbsDelta");
    foreach (var (date, d) in days)
        writer.WriteLine($"{date:yyyy-MM-dd};{d.Minutes};{d.Confirmed};{d.FirstAvg};{d.LastAvg};{d.Lo};{d.Hi};{d.SumRange};{d.SumAbsDelta}");
}
Console.WriteLine($"Daily CSV:           {dailyPath} ({days.Count} days)");
return 0;

sealed class DayAgg
{
    public long Minutes;
    public long Confirmed;
    public int FirstAvg;
    public int LastAvg;
    public int Lo;
    public int Hi;
    public long SumRange;
    public long SumAbsDelta;
    public int PrevAvg;
    public bool HasPrev;
}
