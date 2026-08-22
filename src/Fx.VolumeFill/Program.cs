using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using FXViewer.Storage;

const int HeaderSize = 64;
const int RecordSize = 16;
const uint Magic = 0x314D5846;
const uint FlagFilled = 1u << 0;
const uint FlagVolume = 1u << 3;
const int VolumeShift = 16;
const uint VolumeMask = 0xFFFFu << VolumeShift;
const int VolumeMax = 65535;
const int WindowDays = 6;
const int MaxWindows = 6;

var futures = new (string Pair, string Ticker)[]
{
    ("EURUSD", "6E=F"),
    ("GBPUSD", "6B=F"),
    ("USDCHF", "6S=F"),
    ("USDJPY", "6J=F"),
    ("AUDUSD", "6A=F"),
    ("NZDUSD", "6N=F"),
    ("USDCAD", "6C=F"),
};

string dataRoot = @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
string? csvPath = null;
string? scidPath = null;
string? scidPrefix = null;
string? csvPair = null;
string? csvZone = null;
var pairFilter = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--data" && i + 1 < args.Length) dataRoot = args[++i];
    else if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[++i];
    else if (args[i] == "--scid" && i + 1 < args.Length) scidPath = args[++i];
    else if (args[i] == "--prefix" && i + 1 < args.Length) scidPrefix = args[++i];
    else if (args[i] == "--pair" && i + 1 < args.Length) csvPair = args[++i].ToUpperInvariant();
    else if (args[i] == "--tz" && i + 1 < args.Length) csvZone = args[++i];
    else pairFilter.Add(args[i].ToUpperInvariant());
}

if (!Directory.Exists(dataRoot))
{
    Console.Error.WriteLine("Data folder not found: " + dataRoot);
    return 1;
}

var sw = Stopwatch.StartNew();

if (scidPath != null)
{
    if (csvPair == null)
    {
        Console.Error.WriteLine("--scid needs --pair <SYMBOL>");
        return 1;
    }
    var files = new List<string>();
    if (File.Exists(scidPath)) files.Add(scidPath);
    else if (Directory.Exists(scidPath))
        files.AddRange(Directory.GetFiles(scidPath, (scidPrefix ?? "") + "*.scid"));
    else
    {
        Console.Error.WriteLine("Not found: " + scidPath);
        return 1;
    }
    if (files.Count == 0)
    {
        Console.Error.WriteLine($"No .scid files in {scidPath}" +
            (scidPrefix == null ? "" : $" starting with {scidPrefix}"));
        return 1;
    }
    bool reciprocal = VolumeProfileStore.IsReciprocal(csvPair);
    if (reciprocal)
        Console.WriteLine($"{csvPair}: {(scidPrefix ?? "the future")} is quoted upside down, " +
            "tick prices are inverted and the aggressor side is swapped");
    var perContract = new List<(string Name, Dictionary<long, long> Minutes, Dictionary<long, TickMinute> Profiles)>();
    foreach (var file in files.OrderBy(f => f))
    {
        var (minutes, profiles) = ReadScid(file, reciprocal, out string error);
        if (error.Length > 0)
        {
            Console.Error.WriteLine($"{Path.GetFileName(file)}: {error}");
            continue;
        }
        if (minutes.Count == 0) continue;
        long total = minutes.Values.Sum();
        Console.WriteLine($"{Path.GetFileName(file),-24} {minutes.Count,9:N0} minutes  " +
            $"{FormatUtc(minutes.Keys.Min())} .. {FormatUtc(minutes.Keys.Max())}  " +
            $"volume {total:N0}" +
            (profiles.Count > 0 ? $"  ticks in {profiles.Count:N0} minutes" : ""));
        perContract.Add((Path.GetFileNameWithoutExtension(file), minutes, profiles));
    }
    if (perContract.Count == 0)
    {
        Console.Error.WriteLine("No usable records");
        return 1;
    }
    var (mergedVolumes, mergedProfiles) = MergeByActiveContract(perContract);
    Console.WriteLine($"{perContract.Count} contract file(s) -> {mergedVolumes.Count:N0} minutes, " +
        $"{FormatUtc(mergedVolumes.Keys.Min())} .. {FormatUtc(mergedVolumes.Keys.Max())} UTC");
    WritePair(dataRoot, csvPair, mergedVolumes);
    WriteProfiles(dataRoot, csvPair, mergedProfiles);
    Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F0} s");
    return 0;
}

if (csvPath != null)
{
    if (csvPair == null)
    {
        Console.Error.WriteLine("--csv needs --pair <SYMBOL>");
        return 1;
    }
    if (!File.Exists(csvPath))
    {
        Console.Error.WriteLine("CSV not found: " + csvPath);
        return 1;
    }
    TimeZoneInfo? zone = null;
    if (csvZone != null)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(csvZone);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unknown time zone '{csvZone}': {ex.Message}");
            return 1;
        }
        Console.WriteLine($"Text timestamps read as {zone.Id} and converted to UTC");
    }
    var parsed = ReadCsv(csvPath, zone);
    if (parsed.Volumes.Count == 0)
    {
        Console.Error.WriteLine($"{csvPath}: no usable rows (need a time column and a volume column)");
        return 1;
    }
    Console.WriteLine($"{csvPath}: {parsed.Volumes.Count:N0} minutes, " +
        $"{parsed.Rows:N0} rows, {parsed.Skipped:N0} skipped, " +
        $"{FormatUtc(parsed.FirstUnix)} .. {FormatUtc(parsed.LastUnix)} UTC");
    WritePair(dataRoot, csvPair, parsed.Volumes);
    Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F0} s");
    return 0;
}

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
http.Timeout = TimeSpan.FromSeconds(30);

long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
foreach (var (pair, ticker) in futures)
{
    if (pairFilter.Count > 0 && !pairFilter.Contains(pair)) continue;
    var volumes = await FetchMinuteVolumes(http, ticker, nowUnix);
    if (volumes.Count == 0)
    {
        Console.WriteLine($"{pair}: {ticker} returned no 1m bars, skipped");
        continue;
    }
    Console.Write($"{pair}: {ticker} {volumes.Count:N0} minutes fetched, ");
    WritePair(dataRoot, pair, volumes);
}

Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F0} s");
return 0;

void WritePair(string root, string pair, Dictionary<long, int> volumes)
{
    var dir = Path.Combine(root, pair);
    if (!Directory.Exists(dir))
    {
        Console.WriteLine($"{pair}: no symbol folder in {root}, skipped");
        return;
    }
    int written = 0;
    int noCandle = 0;
    foreach (var yearGroup in volumes.GroupBy(v => DateTimeOffset.FromUnixTimeSeconds(v.Key).UtcDateTime.Year))
    {
        var path = Path.Combine(dir, yearGroup.Key + ".m1");
        if (!File.Exists(path))
        {
            noCandle += yearGroup.Count();
            continue;
        }
        long yearStart = new DateTimeOffset(yearGroup.Key, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var header = new byte[HeaderSize];
            fs.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0)) != Magic
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != RecordSize
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != yearGroup.Key)
            {
                Console.Error.WriteLine($"{pair}: bad header in {path}, skipped");
                continue;
            }
            int minutesInYear = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            var buf = new byte[4];
            foreach (var (minuteUnix, volume) in yearGroup.OrderBy(v => v.Key))
            {
                int moy = (int)((minuteUnix - yearStart) / 60);
                if (moy < 0 || moy >= minutesInYear) continue;
                long pos = HeaderSize + (long)moy * RecordSize + 12;
                fs.Position = pos;
                fs.ReadExactly(buf);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf);
                if ((flags & FlagFilled) == 0)
                {
                    noCandle++;
                    continue;
                }
                flags = (flags & ~VolumeMask) | FlagVolume
                    | ((uint)Math.Min(volume, VolumeMax) << VolumeShift);
                BinaryPrimitives.WriteUInt32LittleEndian(buf, flags);
                fs.Position = pos;
                fs.Write(buf);
                written++;
            }
            fs.Flush(true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"{pair}: cannot update {path}: {ex.Message} (close FXViewer first)");
        }
    }
    long total = volumes.Values.Sum(v => (long)v);
    Console.WriteLine($"{written:N0} written, {noCandle:N0} without a candle, total volume {total:N0}");
}

void WriteProfiles(string root, string pair, Dictionary<long, (TickMinute Accum, int Contract)> profiles)
{
    if (profiles.Count == 0) return;
    if (!VolumeProfileStore.SupportedPairs.Contains(pair))
    {
        Console.WriteLine($"{pair}: ticks in {profiles.Count:N0} minutes, but per-pip profiles " +
            $"support {string.Join(", ", VolumeProfileStore.SupportedPairs)} only, " +
            "totals written without profiles");
        return;
    }
    const int PipPoints = 10;
    const int PriceScale = 100000;
    var dir = Path.Combine(root, pair);
    if (!Directory.Exists(dir)) return;
    try
    {
        WriteProfilesCore(dir, pair, profiles, PipPoints, PriceScale);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"{pair}: profiles not written: {ex.Message} (close FXViewer first)");
    }
}

void WriteProfilesCore(string dir, string pair,
    Dictionary<long, (TickMinute Accum, int Contract)> profiles, int PipPoints, int PriceScale)
{

    var yearBytes = new Dictionary<int, byte[]?>();
    int SpotAvg(long minuteUnix)
    {
        int year = DateTimeOffset.FromUnixTimeSeconds(minuteUnix).UtcDateTime.Year;
        if (!yearBytes.TryGetValue(year, out var bytes))
        {
            var m1 = Path.Combine(dir, year + ".m1");
            bytes = null;
            if (File.Exists(m1))
            {
                using var fs = new FileStream(m1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
                if (bytes.Length < HeaderSize
                    || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0)) != Magic)
                    bytes = null;
            }
            yearBytes[year] = bytes;
        }
        if (bytes == null) return -1;
        long yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        int minutesInYear = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        int moy = (int)((minuteUnix - yearStart) / 60);
        if (moy < 0 || moy >= minutesInYear) return -1;
        int off = HeaderSize + moy * RecordSize;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 12));
        if ((flags & FlagFilled) == 0) return -1;
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 8));
    }

    var estimator = new ShiftEstimator();
    var fresh = new List<ProfileRecord>();
    int noCandle = 0;
    int overflow = 0;
    int prevContract = -1;
    foreach (var (minute, entry) in profiles.OrderBy(p => p.Key))
    {
        var accum = entry.Accum;
        if (!accum.HasTrades) continue;
        if (entry.Contract != prevContract)
        {
            if (prevContract >= 0) estimator.Reset();
            prevContract = entry.Contract;
        }
        int avg = SpotAvg(minute);
        if (avg < 0) { noCandle++; continue; }
        int shift = estimator.Push(minute, avg, accum.VwapPoints);
        var record = VolumeProfileStore.Build(minute, accum, shift, PipPoints);
        if (record == null) { overflow++; continue; }
        fresh.Add(record.Value);
    }
    if (fresh.Count == 0)
    {
        Console.WriteLine($"{pair}: no profile minutes with a spot candle");
        return;
    }

    var existing = VolumeProfileStore.ReadAll(dir, pair, PipPoints, PriceScale, out _,
        s => Console.WriteLine("  " + s));
    var byMinute = existing.ToDictionary(r => r.MinuteUnix);
    var touchedYears = new HashSet<int>();
    foreach (var record in fresh)
    {
        byMinute[record.MinuteUnix] = record;
        touchedYears.Add(DateTimeOffset.FromUnixTimeSeconds(record.MinuteUnix).UtcDateTime.Year);
    }
    foreach (var year in touchedYears.OrderBy(y => y))
    {
        long yearStart = VolumeProfileStore.YearStartUnix(year);
        long yearEnd = VolumeProfileStore.YearStartUnix(year + 1);
        var yearRecords = byMinute.Values
            .Where(r => r.MinuteUnix >= yearStart && r.MinuteUnix < yearEnd)
            .OrderBy(r => r.MinuteUnix).ToList();
        var path = VolumeProfileStore.YearPath(dir, year);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var header = new byte[VolumeProfileStore.HeaderSize];
            VolumeProfileStore.WriteHeader(header, pair, year, PipPoints, PriceScale);
            fs.Write(header);
            foreach (var r in yearRecords)
            {
                var buf = new byte[VolumeProfileStore.EncodedLength(r)];
                VolumeProfileStore.Encode(r, buf);
                fs.Write(buf);
            }
            fs.Flush(true);
        }
        if (File.Exists(path))
        {
            bool headerOk;
            using (var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[VolumeProfileStore.HeaderSize];
                int got = check.Read(head, 0, head.Length);
                headerOk = got == head.Length
                    && VolumeProfileStore.ValidateHeader(head, pair, year, PipPoints, PriceScale, out _);
            }
            if (!headerOk)
            {
                File.Move(path, path + ".bad", true);
                Console.WriteLine($"{pair}: {year}.vap had a bad header, moved to {year}.vap.bad");
            }
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
        else File.Move(tmp, path);
        Console.WriteLine($"{pair}: {year}.vap {yearRecords.Count:N0} minutes");
    }
    Console.WriteLine($"{pair}: profiles {fresh.Count:N0} minutes converted" +
        (noCandle > 0 ? $", {noCandle:N0} without a candle" : "") +
        (overflow > 0 ? $", {overflow:N0} wider than {VolumeProfileStore.MaxSpan} pips dropped" : ""));
}

static string FormatUtc(long unix) =>
    DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString(
        "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

static (Dictionary<long, int> Volumes, int Rows, int Skipped, long FirstUnix, long LastUnix)
    ReadCsv(string path, TimeZoneInfo? zone)
{
    var volumes = new Dictionary<long, int>();
    int rows = 0;
    int skipped = 0;
    long first = long.MaxValue;
    long last = long.MinValue;
    using var reader = new StreamReader(path);
    var header = reader.ReadLine();
    if (header == null) return (volumes, 0, 0, 0, 0);
    char sep = header.Contains(';') && !header.Contains(',') ? ';' : ',';
    var names = header.Split(sep);
    int timeCol = FindColumn(names, "ts_event", "unix", "time_utc", "timestamp", "time", "date", "datetime");
    int volCol = FindColumn(names, "volume", "vol", "size", "qty");
    if (timeCol < 0 || volCol < 0) return (volumes, 0, 0, 0, 0);
    int needed = Math.Max(timeCol, volCol) + 1;
    string? line;
    while ((line = reader.ReadLine()) != null)
    {
        if (line.Length == 0) continue;
        rows++;
        var f = line.Split(sep);
        if (f.Length < needed) { skipped++; continue; }
        if (!TryParseUnix(f[timeCol], zone, out long unix)) { skipped++; continue; }
        if (!decimal.TryParse(f[volCol], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
                | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var vol) || vol < 0)
        {
            skipped++;
            continue;
        }
        long minute = unix - Mod(unix, 60);
        int add = (int)Math.Min(Math.Round(vol, MidpointRounding.AwayFromZero), int.MaxValue / 2);
        volumes[minute] = volumes.TryGetValue(minute, out var have)
            ? (int)Math.Min((long)have + add, int.MaxValue / 2)
            : add;
        if (minute < first) first = minute;
        if (minute > last) last = minute;
    }
    if (volumes.Count == 0) return (volumes, rows, skipped, 0, 0);
    return (volumes, rows, skipped, first, last);
}

static long Mod(long value, long m)
{
    long r = value % m;
    return r < 0 ? r + m : r;
}

static (Dictionary<long, long> Minutes, Dictionary<long, TickMinute> Profiles) ReadScid(
    string path, bool reciprocal, out string error)
{
    const long ScidEpochOffset = 2209161600L;
    const int PriceScale = 100000;
    var minutes = new Dictionary<long, long>();
    var profiles = new Dictionary<long, TickMinute>();
    error = "";
    try
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[56];
        if (fs.Length < head.Length) { error = "file shorter than the header"; return (minutes, profiles); }
        fs.ReadExactly(head);
        if (head[0] != (byte)'S' || head[1] != (byte)'C' || head[2] != (byte)'I' || head[3] != (byte)'D')
        {
            error = "not a .scid file (bad magic)";
            return (minutes, profiles);
        }
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        int recordSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
        if (headerSize < 56 || recordSize < 40)
        {
            error = $"unexpected header {headerSize} / record {recordSize}";
            return (minutes, profiles);
        }
        fs.Position = headerSize;
        var buf = new byte[recordSize * 4096];
        while (true)
        {
            int read = fs.Read(buf, 0, buf.Length);
            if (read < recordSize) break;
            int usable = read - read % recordSize;
            for (int off = 0; off < usable; off += recordSize)
            {
                long micros = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off));
                if (micros <= 0) continue;
                uint volume = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 28));
                if (volume == 0) continue;
                long unix = micros / 1_000_000 - ScidEpochOffset;
                long minute = unix - Mod(unix, 60);
                minutes[minute] = minutes.TryGetValue(minute, out var have) ? have + volume : volume;
                float open = BitConverter.ToSingle(buf, off + 8);
                if (open != 0f && open >= -1e30f) continue;
                float close = BitConverter.ToSingle(buf, off + 20);
                if (close <= 0f) continue;
                uint bid = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 32));
                uint ask = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 36));
                if (!profiles.TryGetValue(minute, out var accum))
                {
                    accum = new TickMinute();
                    profiles[minute] = accum;
                }
                accum.Add(VolumeProfileStore.SpotPoints(close, reciprocal, PriceScale), volume,
                    reciprocal ? ask : bid, reciprocal ? bid : ask);
            }
            if (read != usable) fs.Position -= read - usable;
        }
    }
    catch (Exception ex)
    {
        error = ex.Message;
    }
    return (minutes, profiles);
}

static (Dictionary<long, int> Volumes, Dictionary<long, (TickMinute Accum, int Contract)> Profiles)
    MergeByActiveContract(
    List<(string Name, Dictionary<long, long> Minutes, Dictionary<long, TickMinute> Profiles)> contracts)
{
    var merged = new Dictionary<long, int>();
    var mergedProfiles = new Dictionary<long, (TickMinute Accum, int Contract)>();
    if (contracts.Count == 1)
    {
        foreach (var (minute, volume) in contracts[0].Minutes)
            merged[minute] = (int)Math.Min(volume, int.MaxValue);
        foreach (var (minute, accum) in contracts[0].Profiles)
            mergedProfiles[minute] = (accum, 0);
        return (merged, mergedProfiles);
    }
    var dayWinner = new Dictionary<long, (int Index, long Volume)>();
    for (int i = 0; i < contracts.Count; i++)
    {
        var perDay = new Dictionary<long, long>();
        foreach (var (minute, volume) in contracts[i].Minutes)
        {
            long day = minute - Mod(minute, 86400);
            perDay[day] = perDay.TryGetValue(day, out var have) ? have + volume : volume;
        }
        foreach (var (day, volume) in perDay)
            if (!dayWinner.TryGetValue(day, out var best) || volume > best.Volume)
                dayWinner[day] = (i, volume);
    }
    for (int i = 0; i < contracts.Count; i++)
    {
        foreach (var (minute, volume) in contracts[i].Minutes)
        {
            long day = minute - Mod(minute, 86400);
            if (dayWinner.TryGetValue(day, out var best) && best.Index == i)
                merged[minute] = (int)Math.Min(volume, int.MaxValue);
        }
        foreach (var (minute, accum) in contracts[i].Profiles)
        {
            long day = minute - Mod(minute, 86400);
            if (dayWinner.TryGetValue(day, out var best) && best.Index == i)
                mergedProfiles[minute] = (accum, i);
        }
    }
    return (merged, mergedProfiles);
}

static bool TryParseUnix(string text, TimeZoneInfo? zone, out long unix)
{
    unix = 0;
    var s = text.Trim().Trim('"');
    if (s.Length == 0) return false;
    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
    {
        unix = raw switch
        {
            > 100_000_000_000_000_000 => raw / 1_000_000_000,
            > 100_000_000_000_000 => raw / 1_000_000,
            > 100_000_000_000 => raw / 1_000,
            _ => raw,
        };
        return true;
    }
    if (zone != null)
    {
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) return false;
        unix = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeSeconds();
        return true;
    }
    if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
    {
        unix = dto.ToUnixTimeSeconds();
        return true;
    }
    return false;
}

static int FindColumn(string[] names, params string[] wanted)
{
    foreach (var want in wanted)
        for (int i = 0; i < names.Length; i++)
            if (names[i].Trim().Trim('"').Equals(want, StringComparison.OrdinalIgnoreCase))
                return i;
    return -1;
}

static async Task<Dictionary<long, int>> FetchMinuteVolumes(HttpClient http, string ticker, long nowUnix)
{
    var volumes = new Dictionary<long, int>();
    for (int w = 0; w < MaxWindows; w++)
    {
        long to = nowUnix - (long)w * WindowDays * 86400;
        long from = to - WindowDays * 86400;
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ticker)}" +
            $"?interval=1m&period1={from}&period2={to}";
        string body;
        try
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) break;
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ticker} window {w}: {ex.Message}");
            break;
        }
        using var doc = JsonDocument.Parse(body);
        var chart = doc.RootElement.GetProperty("chart");
        if (chart.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null) break;
        var result = chart.GetProperty("result")[0];
        if (!result.TryGetProperty("timestamp", out var stamps)) continue;
        var quote = result.GetProperty("indicators").GetProperty("quote")[0];
        var vols = quote.GetProperty("volume");
        int n = Math.Min(stamps.GetArrayLength(), vols.GetArrayLength());
        for (int i = 0; i < n; i++)
        {
            if (vols[i].ValueKind == JsonValueKind.Null) continue;
            long t = stamps[i].GetInt64();
            int v = vols[i].GetInt32();
            if (v < 0) continue;
            volumes[t - t % 60] = v;
        }
        await Task.Delay(300);
    }
    return volumes;
}
