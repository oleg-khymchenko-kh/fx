using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using FXViewer.OrderBook;

const int StoreVersion = 1;
const int Levels = 100;
const int StoreHeaderBytes = 16;
const int StoreRecordBytes = 8 + 4 + Levels * 2 * 2;
const long ScidEpochOffset = 2209161600L;

const int DepthHeaderMin = 64;
const int DepthRecordSize = 24;
const byte CommandClearBook = 1;
const byte CommandAddBid = 2;
const byte CommandAddAsk = 3;
const byte CommandModifyBid = 4;
const byte CommandModifyAsk = 5;
const byte CommandDeleteBid = 6;
const byte CommandDeleteAsk = 7;
const byte FlagEndOfBatch = 0x01;

string depthRoot = @"C:\SierraChart\Data\MarketDepthData";
string dataRoot = @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
string? pair = null;
string prefix = "";
int priceScale = 100000;
int pipPoints = 10;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--depth" && i + 1 < args.Length) depthRoot = args[++i];
    else if (args[i] == "--data" && i + 1 < args.Length) dataRoot = args[++i];
    else if (args[i] == "--pair" && i + 1 < args.Length) pair = args[++i].ToUpperInvariant();
    else if (args[i] == "--prefix" && i + 1 < args.Length) prefix = args[++i];
    else if (args[i] == "--scale" && i + 1 < args.Length) priceScale = int.Parse(args[++i]);
    else if (args[i] == "--pip" && i + 1 < args.Length) pipPoints = int.Parse(args[++i]);
}

if (pair == null)
{
    Console.Error.WriteLine("--pair <SYMBOL> is required");
    return 1;
}
if (!Directory.Exists(depthRoot))
{
    Console.Error.WriteLine("Depth folder not found: " + depthRoot);
    return 1;
}
if (!Directory.Exists(dataRoot))
{
    Console.Error.WriteLine("Data folder not found: " + dataRoot);
    return 1;
}

var byDate = new SortedDictionary<DateOnly, (string Path, long Size)>();
foreach (var path in Directory.GetFiles(depthRoot, prefix + "*.depth"))
{
    var name = Path.GetFileNameWithoutExtension(path);
    int dot = name.LastIndexOf('.');
    if (dot < 0) continue;
    if (!DateOnly.TryParseExact(name[(dot + 1)..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)) continue;
    long size = new FileInfo(path).Length;
    if (!byDate.TryGetValue(date, out var have) || size > have.Size)
        byDate[date] = (path, size);
}

if (byDate.Count == 0)
{
    Console.Error.WriteLine($"No .depth files in {depthRoot}" +
        (prefix.Length == 0 ? "" : $" starting with {prefix}"));
    return 1;
}

Console.WriteLine($"{byDate.Count} day file(s), {byDate.Values.Sum(v => v.Size) / 1048576.0:F0} MB");
Console.WriteLine($"{pair}: scale {priceScale}, {pipPoints} points per level, " +
    $"{Levels} levels per side ({Levels * pipPoints / (double)pipPoints:F0} pips)");

var symbolDir = Path.Combine(dataRoot, pair);
if (!Directory.Exists(symbolDir))
{
    Console.Error.WriteLine("No symbol folder: " + symbolDir);
    return 1;
}
var storeDir = Path.Combine(symbolDir, "depth");
Directory.CreateDirectory(storeDir);

long lastStored = 0;
foreach (var path in Directory.GetFiles(storeDir, "*.dpt"))
{
    var name = Path.GetFileNameWithoutExtension(path);
    if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) continue;
    long last = DepthStore.LastMinute(symbolDir, y);
    if (last > lastStored) lastStored = last;
}
if (lastStored > 0)
    Console.WriteLine("Store already ends at " +
        $"{DateTimeOffset.FromUnixTimeSeconds(lastStored).UtcDateTime:yyyy-MM-dd HH:mm} UTC, " +
        "earlier minutes are skipped");

var sw = Stopwatch.StartNew();
var writers = new Dictionary<int, FileStream>();
var recordBuf = new byte[StoreRecordBytes];
var bidOut = new ushort[Levels];
var askOut = new ushort[Levels];
long totalMinutes = 0;
long totalRecords = 0;
int skipped = 0;

foreach (var (date, file) in byDate)
{
    var (minutes, records, error) = ConvertDay(file.Path);
    if (error.Length > 0)
    {
        Console.Error.WriteLine($"{Path.GetFileName(file.Path)}: {error}");
        skipped++;
        continue;
    }
    totalMinutes += minutes;
    totalRecords += records;
    Console.WriteLine($"{date:yyyy-MM-dd}  {records,10:N0} records -> {minutes,5:N0} minutes");
}

foreach (var w in writers.Values) w.Dispose();
Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F0} s: {totalMinutes:N0} minutes from " +
    $"{totalRecords:N0} records, {skipped} file(s) skipped");
long stored = Directory.GetFiles(storeDir, "*.dpt").Sum(p => new FileInfo(p).Length);
Console.WriteLine($"Store: {stored / 1048576.0:F1} MB in {storeDir}");
return 0;

(long Minutes, long Records, string Error) ConvertDay(string path)
{
    long minutes = 0;
    var book = new DepthBook();
    var res = DepthReader.ReadTail(path, 0, priceScale, book, (minute, b) =>
    {
        if (minute <= lastStored) return;
        if (!b.TryBuild(minute, pipPoints, out var snapshot)) return;
        Emit(snapshot);
        minutes++;
    });
    if (res.Ok && book.Started && book.CurrentMinute > lastStored
        && book.TryBuild(book.CurrentMinute, pipPoints, out var tail))
    {
        Emit(tail);
        minutes++;
    }
    return (minutes, res.Records, res.Error);

    void Emit(DepthSnapshot snapshot)
    {
        int year = DateTimeOffset.FromUnixTimeSeconds(snapshot.MinuteUnix).UtcDateTime.Year;
        var stream = WriterFor(year);
        DepthStore.WriteRecord(stream, recordBuf, snapshot.MinuteUnix, snapshot.MidPoints,
            snapshot.Bid, snapshot.Ask);
        if (snapshot.MinuteUnix > lastStored) lastStored = snapshot.MinuteUnix;
    }
}

FileStream WriterFor(int year)
{
    if (writers.TryGetValue(year, out var have)) return have;
    var path = Path.Combine(storeDir, year.ToString(CultureInfo.InvariantCulture) + ".dpt");
    bool fresh = !File.Exists(path) || new FileInfo(path).Length < StoreHeaderBytes;
    var stream = new FileStream(path, fresh ? FileMode.Create : FileMode.Open,
        FileAccess.Write, FileShare.Read, 1 << 20);
    if (fresh)
    {
        var header = new byte[StoreHeaderBytes];
        header[0] = (byte)'F';
        header[1] = (byte)'X';
        header[2] = (byte)'D';
        header[3] = (byte)'P';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), StoreVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Levels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), pipPoints);
        stream.Write(header);
    }
    else
    {
        stream.Position = stream.Length;
    }
    writers[year] = stream;
    return stream;
}

static long Mod(long value, long m)
{
    long r = value % m;
    return r < 0 ? r + m : r;
}
