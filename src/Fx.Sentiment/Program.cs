using System.Globalization;
using System.Text;
using Fx.Sentiment;
using FXViewer.Storage;

const string DefaultDataRoot = @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
const string DefaultSymbols = "EURUSD,GBPUSD,USDCHF";
const string LogFileName = "sentiment-myfxbook.log";
const int DefaultPauseSeconds = 20;
const int ExitOk = 0;
const int ExitError = 1;
const int ExitRateLimited = 3;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    PrintUsage();
    return ExitError;
}

var options = ReadOptions(args.Skip(1).ToArray());
string dataRoot = options.GetValueOrDefault("data", DefaultDataRoot);
string[] symbols = options.GetValueOrDefault("symbols", DefaultSymbols)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => s.ToUpperInvariant())
    .ToArray();
int pauseSeconds = options.TryGetValue("pause", out var pauseText)
    && int.TryParse(pauseText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPause)
    && parsedPause >= 0
        ? parsedPause
        : DefaultPauseSeconds;
bool force = options.ContainsKey("force");
string logPath = Path.Combine(dataRoot, LogFileName);

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "fetch":
            return await FetchAsync();
        case "backfill":
            return await BackfillAsync();
        case "collect":
            return await CollectAsync();
        case "status":
            return Status();
        case "parse":
            return ParseFile();
        case "reimport":
            return Reimport();
        default:
            PrintUsage();
            return ExitError;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    Log("error " + ex.Message);
    return ExitError;
}

async Task<int> FetchAsync()
{
    if (!CheckDataRoot() || !CheckSymbols()) return ExitError;
    var timeframes = options.GetValueOrDefault("tf", "m1")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(SentimentTimeframes.Normalize)
        .ToArray();
    var unknown = timeframes.Where(tf => !SentimentTimeframes.IsKnown(tf)).ToArray();
    if (unknown.Length > 0)
    {
        Console.Error.WriteLine("ERROR: unknown timeframe(s): " + string.Join(", ", unknown)
            + ". Known: " + string.Join(", ", SentimentTimeframes.All));
        return ExitError;
    }
    var jobs = symbols.SelectMany(symbol => timeframes.Select(tf => (symbol, tf))).ToList();
    return await RunJobsAsync(jobs);
}

async Task<int> BackfillAsync()
{
    if (!CheckDataRoot() || !CheckSymbols()) return ExitError;
    var jobs = new List<(string Symbol, string Timeframe)>();
    foreach (var symbol in symbols) jobs.Add((symbol, "m1"));
    foreach (var tf in SentimentTimeframes.BackfillOrder)
        foreach (var symbol in symbols)
            if (force || !File.Exists(SentimentStore.SeriesPath(SymbolDirectory(symbol), tf)))
                jobs.Add((symbol, tf));
    if (jobs.Count == 0)
    {
        Console.WriteLine("Nothing to backfill, every timeframe already has a file (use --force to refetch)");
        return ExitOk;
    }
    return await RunJobsAsync(jobs);
}

async Task<int> CollectAsync()
{
    if (!CheckDataRoot() || !CheckSymbols()) return ExitError;
    var jobs = new List<(string Symbol, string Timeframe)>();
    foreach (var symbol in symbols) jobs.Add((symbol, "m1"));
    var missing = SentimentTimeframes.BackfillOrder
        .SelectMany(tf => symbols.Select(symbol => (Symbol: symbol, Timeframe: tf)))
        .FirstOrDefault(job => !File.Exists(SentimentStore.SeriesPath(SymbolDirectory(job.Symbol), job.Timeframe)));
    if (missing.Symbol != null) jobs.Add(missing);
    Log("collect start");
    int code = await RunJobsAsync(jobs);
    Log("collect end exit=" + code);
    return code;
}

async Task<int> RunJobsAsync(List<(string Symbol, string Timeframe)> jobs)
{
    using var client = new MyfxbookClient();
    long fetchedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    bool first = true;
    for (int i = 0; i < jobs.Count; i++)
    {
        var (symbol, tf) = jobs[i];
        if (!first && pauseSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(pauseSeconds));
        first = false;
        var result = await client.FetchAsync(symbol, tf, CancellationToken.None);
        if (result.RateLimited)
        {
            var left = jobs.Skip(i).Select(j => j.Symbol + " " + j.Timeframe);
            string message = $"rate limited at {symbol} {tf}, {jobs.Count - i} job(s) not done: {string.Join(", ", left)}";
            Console.WriteLine("RATE LIMITED: " + message);
            Log(message);
            return ExitRateLimited;
        }
        string symbolDirectory = SymbolDirectory(symbol);
        SentimentStore.SaveRaw(SentimentStore.RawPath(symbolDirectory, tf, fetchedUnix), result.Json);
        string seriesPath = SentimentStore.SeriesPath(symbolDirectory, tf);
        int before = SentimentStore.Load(seriesPath).Count;
        int changed = SentimentStore.Merge(seriesPath, result.Points);
        var all = SentimentStore.Load(seriesPath);
        string firstTime = all.Count > 0 ? Stamp(all[0].TimeUnix) : "-";
        string lastTime = all.Count > 0 ? Stamp(all[^1].TimeUnix) : "-";
        string line = $"{symbol} {tf}: got {result.Points.Count} rows ({result.SkippedRows} empty), "
            + $"{changed} new or changed, file {before} -> {all.Count} rows, {firstTime} .. {lastTime}";
        Console.WriteLine(line);
        Log(line);
    }
    return ExitOk;
}

int Status()
{
    if (!CheckDataRoot()) return ExitError;
    foreach (var symbol in symbols)
    {
        Console.WriteLine(symbol);
        string symbolDirectory = SymbolDirectory(symbol);
        foreach (var tf in SentimentTimeframes.All)
        {
            string path = SentimentStore.SeriesPath(symbolDirectory, tf);
            var points = SentimentStore.Load(path);
            if (points.Count == 0)
            {
                Console.WriteLine($"  {tf,-4} no data");
                continue;
            }
            long step = SentimentTimeframes.Minutes(tf) * 60L;
            long largestGap = 0;
            long gapStart = 0;
            int gaps = 0;
            for (int i = 1; i < points.Count; i++)
            {
                long gap = points[i].TimeUnix - points[i - 1].TimeUnix;
                if (gap > step * 3 / 2)
                {
                    gaps++;
                    if (gap > largestGap) { largestGap = gap; gapStart = points[i - 1].TimeUnix; }
                }
            }
            var last = points[^1];
            Console.WriteLine($"  {tf,-4} {points.Count,6} rows  {Stamp(points[0].TimeUnix)} .. {Stamp(last.TimeUnix)}"
                + $"  last long {last.LongShareByLots:P0} by lots, {last.LongShareByPositions:P0} by positions"
                + (gaps > 0 ? $"  gaps {gaps}, largest {TimeSpan.FromSeconds(largestGap)} after {Stamp(gapStart)}" : ""));
        }
    }
    return ExitOk;
}

int ParseFile()
{
    if (!options.TryGetValue("file", out var file) || !File.Exists(file))
    {
        Console.Error.WriteLine("ERROR: --file <path> is required and must exist");
        return ExitError;
    }
    string json = ReadMaybeGzip(file);
    var (points, skipped) = MyfxbookClient.Parse(json);
    Console.WriteLine($"{points.Count} rows, {skipped} empty");
    if (points.Count > 0)
    {
        Console.WriteLine("first: " + Stamp(points[0].TimeUnix) + " " + SentimentStore.FormatLine(points[0]));
        Console.WriteLine("last:  " + Stamp(points[^1].TimeUnix) + " " + SentimentStore.FormatLine(points[^1]));
    }
    return ExitOk;
}

int Reimport()
{
    if (!CheckDataRoot()) return ExitError;
    foreach (var symbol in symbols)
    {
        string rawDirectory = SentimentStore.RawDirectory(SymbolDirectory(symbol));
        if (!Directory.Exists(rawDirectory))
        {
            Console.WriteLine($"{symbol}: no raw folder");
            continue;
        }
        var files = Directory.GetFiles(rawDirectory, "*.json.gz").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var byTimeframe = files
            .Select(f => (Path: f, Timeframe: Path.GetFileName(f).Split('-')[0]))
            .Where(f => SentimentTimeframes.IsKnown(f.Timeframe))
            .GroupBy(f => f.Timeframe);
        foreach (var group in byTimeframe)
        {
            var merged = new SortedDictionary<long, SentimentPoint>();
            int bad = 0;
            foreach (var (path, _) in group)
            {
                try
                {
                    var (points, _) = MyfxbookClient.Parse(ReadMaybeGzip(path));
                    foreach (var point in points) merged[point.TimeUnix] = point;
                }
                catch (Exception ex)
                {
                    bad++;
                    Console.Error.WriteLine($"  skipped {Path.GetFileName(path)}: {ex.Message}");
                }
            }
            string seriesPath = SentimentStore.SeriesPath(SymbolDirectory(symbol), group.Key);
            int changed = SentimentStore.Merge(seriesPath, merged.Values);
            Console.WriteLine($"{symbol} {group.Key}: {group.Count()} raw file(s), {bad} unreadable, {merged.Count} rows, {changed} new or changed");
        }
    }
    return ExitOk;
}

static string ReadMaybeGzip(string path)
{
    if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return File.ReadAllText(path);
    using var file = File.OpenRead(path);
    using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
    using var reader = new StreamReader(gzip, Encoding.UTF8);
    return reader.ReadToEnd();
}

bool CheckDataRoot()
{
    if (Directory.Exists(dataRoot)) return true;
    Console.Error.WriteLine("ERROR: data folder not found: " + dataRoot);
    return false;
}

bool CheckSymbols()
{
    var unknown = symbols.Where(s => !MyfxbookClient.IsKnownSymbol(s)).ToArray();
    if (unknown.Length == 0) return true;
    Console.Error.WriteLine("ERROR: no Myfxbook id for: " + string.Join(", ", unknown)
        + ". Known: " + string.Join(", ", MyfxbookClient.SymbolOids.Keys));
    return false;
}

string SymbolDirectory(string symbol) => Path.Combine(dataRoot, symbol);

void Log(string message)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
        File.AppendAllText(logPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}{Environment.NewLine}");
    }
    catch
    {
    }
}

static string Stamp(long unix) =>
    DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

static Dictionary<string, string> ReadOptions(string[] rest)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--", StringComparison.Ordinal)) continue;
        string key = rest[i][2..];
        string value = i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? rest[++i]
            : "";
        result[key] = value;
    }
    return result;
}

static void PrintUsage()
{
    Console.WriteLine("Fx.Sentiment fetch [--tf m1,h1,...] [--symbols EURUSD,GBPUSD] [--data <folder>] [--pause <sec>]");
    Console.WriteLine("Fx.Sentiment backfill [--symbols ...] [--force] [--data <folder>] [--pause <sec>]");
    Console.WriteLine("Fx.Sentiment collect [--symbols ...] [--data <folder>] [--pause <sec>]");
    Console.WriteLine("Fx.Sentiment status [--symbols ...] [--data <folder>]");
    Console.WriteLine("Fx.Sentiment parse --file <answer.json or .json.gz>");
    Console.WriteLine("Fx.Sentiment reimport [--symbols ...] [--data <folder>]   (rebuild the series from raw/)");
    Console.WriteLine("Timeframes: " + string.Join(", ", SentimentTimeframes.All));
    Console.WriteLine("Exit codes: 0 ok, 1 error, 3 rate limited (some requests not done)");
}
