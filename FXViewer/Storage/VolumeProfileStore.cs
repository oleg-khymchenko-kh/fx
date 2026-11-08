using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FXViewer.Storage;

public readonly record struct ProfileRecord(long MinuteUnix, int BasePip, short Shift, ushort[] Bid, ushort[] Ask)
{
    public int Span => Bid.Length;

    public long TotalVolume
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < Bid.Length; i++) sum += Bid[i] + Ask[i];
            return sum;
        }
    }
}

public sealed class TickMinute
{
    public readonly Dictionary<int, (long Bid, long Ask)> Cells = new();

    private double _vwapNum;
    private long _vwapDen;

    public void Add(int pricePoints, long total, long bid, long ask)
    {
        long extra = total - bid - ask;
        if (extra > 0)
        {
            bid += extra / 2;
            ask += extra - extra / 2;
        }
        var cur = Cells.TryGetValue(pricePoints, out var c) ? c : default;
        Cells[pricePoints] = (cur.Bid + bid, cur.Ask + ask);
        _vwapNum += (double)pricePoints * total;
        _vwapDen += total;
    }

    public bool HasTrades => _vwapDen > 0;

    public int VwapPoints => (int)Math.Round(_vwapNum / _vwapDen);
}

public sealed class ProfileSet
{
    private sealed class Tail
    {
        public int Records;
        public int Cells;
    }

    private const int SpareRecords = 4096;
    private const int SpareCells = 32768;

    public readonly long[] MinuteUnix;
    public readonly int[] BasePip;
    public readonly int[] Start;
    public readonly int[] Bid;
    public readonly int[] Ask;

    private readonly Tail _tail;

    private ProfileSet(long[] minuteUnix, int[] basePip, int[] start, int[] bid, int[] ask, int count, Tail tail)
    {
        MinuteUnix = minuteUnix;
        BasePip = basePip;
        Start = start;
        Bid = bid;
        Ask = ask;
        Count = count;
        _tail = tail;
    }

    public int Count { get; }

    public static readonly ProfileSet Empty = new(
        Array.Empty<long>(), Array.Empty<int>(), new int[1], Array.Empty<int>(), Array.Empty<int>(), 0,
        new Tail());

    private static ProfileSet Allocate(int records, int cells, int spareRecords, int spareCells) =>
        new(new long[records + spareRecords], new int[records + spareRecords], new int[records + spareRecords + 1],
            new int[cells + spareCells], new int[cells + spareCells], records,
            new Tail { Records = records, Cells = cells });

    public static ProfileSet FromRecords(IReadOnlyList<ProfileRecord> records)
    {
        int cells = 0;
        for (int i = 0; i < records.Count; i++) cells += records[i].Span;
        var set = Allocate(records.Count, cells, 0, 0);
        int offset = 0;
        for (int i = 0; i < records.Count; i++)
            offset = set.Put(i, offset, records[i]);
        return set;
    }

    public static ProfileSet Build(ProfileSet head, IReadOnlyList<ProfileYear> years)
    {
        int records = head.Count;
        int cells = head.Start[head.Count];
        foreach (var year in years)
        {
            records += year.Records;
            cells += year.Cells;
        }
        var set = Allocate(records, cells, SpareRecords, SpareCells);
        int outRecord = 0;
        int outCell = 0;
        set.CopyFrom(head, 0, head.Count, ref outRecord, ref outCell);
        foreach (var year in years)
        {
            if (year.Sorted != null)
            {
                foreach (var r in year.Sorted)
                    outCell = set.Put(outRecord++, outCell, r);
            }
            else if (year.Bytes != null)
            {
                int offset = VolumeProfileStore.HeaderSize;
                for (int k = 0; k < year.Records; k++)
                    outCell = set.PutEncoded(outRecord++, outCell, year.Bytes, ref offset);
            }
        }
        return set;
    }

    public ProfileSet Before(long minuteUnix) =>
        new(MinuteUnix, BasePip, Start, Bid, Ask, LowerBound(minuteUnix), new Tail { Records = -1 });

    public ProfileSet Merge(IReadOnlyList<ProfileRecord> fresh)
    {
        if (fresh.Count == 0) return this;
        var sorted = fresh.OrderBy(r => r.MinuteUnix).ToList();
        var deduped = new List<ProfileRecord>(sorted.Count);
        foreach (var r in sorted)
        {
            if (deduped.Count > 0 && deduped[^1].MinuteUnix == r.MinuteUnix) deduped[^1] = r;
            else deduped.Add(r);
        }
        return Count == 0 || deduped[0].MinuteUnix > MinuteUnix[Count - 1]
            ? Append(deduped)
            : Rebuild(deduped);
    }

    private ProfileSet Append(List<ProfileRecord> fresh)
    {
        int records = Count + fresh.Count;
        int cells = Start[Count];
        foreach (var r in fresh) cells += r.Span;
        bool inPlace;
        lock (_tail)
        {
            inPlace = _tail.Records == Count && _tail.Cells == Start[Count]
                && MinuteUnix.Length >= records && Bid.Length >= cells;
            if (inPlace)
            {
                _tail.Records = records;
                _tail.Cells = cells;
            }
        }
        var set = inPlace
            ? new ProfileSet(MinuteUnix, BasePip, Start, Bid, Ask, records, _tail)
            : Allocate(records, cells, records / 8 + SpareRecords, cells / 8 + SpareCells);
        int outRecord = 0;
        int outCell = 0;
        if (inPlace)
        {
            outRecord = Count;
            outCell = Start[Count];
        }
        else
        {
            set.CopyFrom(this, 0, Count, ref outRecord, ref outCell);
        }
        foreach (var r in fresh)
            outCell = set.Put(outRecord++, outCell, r);
        return set;
    }

    private ProfileSet Rebuild(List<ProfileRecord> fresh)
    {
        var at = new int[fresh.Count];
        var replaces = new bool[fresh.Count];
        int records = Count;
        int cells = Start[Count];
        for (int k = 0; k < fresh.Count; k++)
        {
            int i = LowerBound(fresh[k].MinuteUnix);
            at[k] = i;
            replaces[k] = i < Count && MinuteUnix[i] == fresh[k].MinuteUnix;
            cells += fresh[k].Span;
            if (replaces[k]) cells -= Start[i + 1] - Start[i];
            else records++;
        }
        var set = Allocate(records, cells, records / 8 + SpareRecords, cells / 8 + SpareCells);
        int outRecord = 0;
        int outCell = 0;
        int old = 0;
        for (int k = 0; k < fresh.Count; k++)
        {
            set.CopyFrom(this, old, at[k], ref outRecord, ref outCell);
            outCell = set.Put(outRecord++, outCell, fresh[k]);
            old = replaces[k] ? at[k] + 1 : at[k];
        }
        set.CopyFrom(this, old, Count, ref outRecord, ref outCell);
        return set;
    }

    private int Put(int index, int cell, in ProfileRecord r)
    {
        MinuteUnix[index] = r.MinuteUnix;
        BasePip[index] = r.BasePip;
        Start[index] = cell;
        for (int j = 0; j < r.Span; j++)
        {
            Bid[cell + j] = r.Bid[j];
            Ask[cell + j] = r.Ask[j];
        }
        Start[index + 1] = cell + r.Span;
        return cell + r.Span;
    }

    private int PutEncoded(int index, int cell, byte[] bytes, ref int offset)
    {
        var record = bytes.AsSpan(offset);
        MinuteUnix[index] = BinaryPrimitives.ReadInt64LittleEndian(record);
        BasePip[index] = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8));
        int span = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(14));
        Start[index] = cell;
        for (int j = 0; j < span; j++)
        {
            Bid[cell + j] = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(16 + j * 4));
            Ask[cell + j] = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(18 + j * 4));
        }
        Start[index + 1] = cell + span;
        offset += VolumeProfileStore.FixedRecordBytes + span * 4 + 4;
        return cell + span;
    }

    private void CopyFrom(ProfileSet source, int from, int to, ref int outRecord, ref int outCell)
    {
        if (to <= from) return;
        int records = to - from;
        Array.Copy(source.MinuteUnix, from, MinuteUnix, outRecord, records);
        Array.Copy(source.BasePip, from, BasePip, outRecord, records);
        int shift = outCell - source.Start[from];
        for (int i = 0; i <= records; i++) Start[outRecord + i] = source.Start[from + i] + shift;
        int cells = source.Start[to] - source.Start[from];
        Array.Copy(source.Bid, source.Start[from], Bid, outCell, cells);
        Array.Copy(source.Ask, source.Start[from], Ask, outCell, cells);
        outRecord += records;
        outCell += cells;
    }

    public int LowerBound(long minuteUnix)
    {
        int lo = 0;
        int hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (MinuteUnix[mid] < minuteUnix) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

public readonly record struct ProfileScale(int PipPoints, int PriceScale);

public static class VolumeProfileStore
{
    public const uint Magic = 0x41565846;
    public const int FormatVersion = 1;
    public const int HeaderSize = 64;
    public const int FixedRecordBytes = 16;
    public const int MaxSpan = 1024;

    public static readonly Dictionary<string, ProfileScale> SupportedPairs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EURUSD"] = new(10, 100000),
            ["GBPUSD"] = new(10, 100000),
            ["USDCHF"] = new(10, 100000),
            ["GER40"] = new(100, 100),
        };

    public static ProfileScale? ScaleOf(string? pair) =>
        pair != null && SupportedPairs.TryGetValue(pair, out var scale) ? scale : null;

    public static readonly HashSet<string> ReciprocalPairs =
        new(StringComparer.OrdinalIgnoreCase) { "USDCHF", "USDJPY", "USDCAD" };

    public static bool IsReciprocal(string pair) => ReciprocalPairs.Contains(pair);

    public static int SpotPoints(double futurePrice, bool reciprocal, int priceScale) =>
        (int)Math.Round((reciprocal ? 1.0 / futurePrice : futurePrice) * priceScale);

    public static string Directory(string symbolDirectory) => Path.Combine(symbolDirectory, "volume");

    public static string YearPath(string symbolDirectory, int year) =>
        Path.Combine(Directory(symbolDirectory), year.ToString(CultureInfo.InvariantCulture) + ".vap");

    public static long YearStartUnix(int year) =>
        new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    public static int PipOf(int points, int pipPoints) => (points + pipPoints / 2) / pipPoints;

    public static int EncodedLength(in ProfileRecord record) => FixedRecordBytes + record.Span * 4 + 4;

    public static void Encode(in ProfileRecord record, Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer, record.MinuteUnix);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(8), record.BasePip);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(12), record.Shift);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14), (ushort)record.Span);
        for (int i = 0; i < record.Span; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(16 + i * 4), record.Bid[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(18 + i * 4), record.Ask[i]);
        }
        int crcOffset = FixedRecordBytes + record.Span * 4;
        uint crc = Crc32.Compute(buffer.Slice(0, crcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(crcOffset), crc);
    }

    public static bool ValidateRecord(ReadOnlySpan<byte> buffer, int offset, int end,
        long yearStart, long yearEnd, out long minuteUnix, out int length)
    {
        minuteUnix = 0;
        length = 0;
        if (offset + FixedRecordBytes > end) return false;
        long minute = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        int span = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 14));
        if (minute < yearStart || minute >= yearEnd || minute % 60 != 0) return false;
        if (span < 1 || span > MaxSpan) return false;
        int len = FixedRecordBytes + span * 4 + 4;
        if (offset + len > end) return false;
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + len - 4));
        if (Crc32.Compute(buffer.Slice(offset, len - 4)) != stored) return false;
        minuteUnix = minute;
        length = len;
        return true;
    }

    public static ProfileRecord Decode(ReadOnlySpan<byte> buffer, int offset)
    {
        long minute = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        int basePip = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 8));
        short shift = BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(offset + 12));
        int span = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 14));
        var bid = new ushort[span];
        var ask = new ushort[span];
        for (int i = 0; i < span; i++)
        {
            bid[i] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 16 + i * 4));
            ask[i] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 18 + i * 4));
        }
        return new ProfileRecord(minute, basePip, shift, bid, ask);
    }

    public static void WriteHeader(Span<byte> header, string symbol, int year, int pipPoints, int priceScale)
    {
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), year);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), pipPoints);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16), priceScale);
        var name = Encoding.ASCII.GetBytes(symbol);
        name.AsSpan(0, Math.Min(name.Length, 16)).CopyTo(header.Slice(20));
    }

    public static bool ValidateHeader(ReadOnlySpan<byte> header, string symbol, int year,
        int pipPoints, int priceScale, out string error)
    {
        error = "";
        if (header.Length < HeaderSize) { error = "file shorter than the header"; return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) { error = "bad magic"; return false; }
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
        if (version != FormatVersion) { error = $"unsupported version {version}"; return false; }
        int fileYear = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8));
        int filePip = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12));
        int fileScale = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16));
        var name = Encoding.ASCII.GetString(header.Slice(20, 16)).TrimEnd('\0');
        if (fileYear != year) { error = $"year {fileYear}, expected {year}"; return false; }
        if (filePip != pipPoints) { error = $"pipPoints {filePip}, expected {pipPoints}"; return false; }
        if (fileScale != priceScale) { error = $"priceScale {fileScale}, expected {priceScale}"; return false; }
        if (!string.Equals(name, symbol, StringComparison.OrdinalIgnoreCase))
        {
            error = $"symbol {name}, expected {symbol}";
            return false;
        }
        return true;
    }

    public static ProfileRecord? Build(long minuteUnix, TickMinute accum, int shiftPoints, int pipPoints)
    {
        if (!accum.HasTrades) return null;
        int lo = int.MaxValue;
        int hi = int.MinValue;
        foreach (var points in accum.Cells.Keys)
        {
            int pip = PipOf(points + shiftPoints, pipPoints);
            if (pip < lo) lo = pip;
            if (pip > hi) hi = pip;
        }
        int span = hi - lo + 1;
        if (span < 1 || span > MaxSpan) return null;
        var bid = new long[span];
        var ask = new long[span];
        foreach (var (points, sides) in accum.Cells)
        {
            int cell = PipOf(points + shiftPoints, pipPoints) - lo;
            bid[cell] += sides.Bid;
            ask[cell] += sides.Ask;
        }
        var bidCells = new ushort[span];
        var askCells = new ushort[span];
        for (int i = 0; i < span; i++)
        {
            bidCells[i] = (ushort)Math.Min(bid[i], ushort.MaxValue);
            askCells[i] = (ushort)Math.Min(ask[i], ushort.MaxValue);
        }
        return new ProfileRecord(minuteUnix, lo, (short)Math.Clamp(shiftPoints, short.MinValue, short.MaxValue),
            bidCells, askCells);
    }

    public static List<int> Years(string symbolDirectory)
    {
        var years = new List<int>();
        var dir = Directory(symbolDirectory);
        if (!System.IO.Directory.Exists(dir)) return years;
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.vap"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var y))
                years.Add(y);
        years.Sort();
        return years;
    }

    public static List<ProfileRecord> ReadAll(string symbolDirectory, string symbol, int pipPoints,
        int priceScale, out int duplicates, Action<string>? log = null)
    {
        duplicates = 0;
        var result = new List<ProfileRecord>();
        foreach (var year in Years(symbolDirectory))
        {
            result.AddRange(ReadYear(symbolDirectory, symbol, year, pipPoints, priceScale, out int dup, log));
            duplicates += dup;
        }
        return result;
    }

    public static List<ProfileRecord> ReadYear(string symbolDirectory, string symbol, int year, int pipPoints,
        int priceScale, out int duplicates, Action<string>? log = null)
    {
        var scan = ScanYear(symbolDirectory, symbol, year, pipPoints, priceScale, log);
        duplicates = scan.Duplicates;
        if (scan.Sorted != null) return scan.Sorted;
        var records = new List<ProfileRecord>(scan.Records);
        int offset = HeaderSize;
        for (int k = 0; k < scan.Records; k++)
        {
            var r = Decode(scan.Bytes!, offset);
            records.Add(r);
            offset += EncodedLength(r);
        }
        return records;
    }

    public static ProfileYear ScanYear(string symbolDirectory, string symbol, int year, int pipPoints,
        int priceScale, Action<string>? log = null)
    {
        byte[] bytes;
        try
        {
            using var fs = new FileStream(YearPath(symbolDirectory, year), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        catch (Exception ex)
        {
            log?.Invoke($"profile {symbol} {year}: read failed: {ex.Message}");
            return default;
        }
        if (!ValidateHeader(bytes, symbol, year, pipPoints, priceScale, out var error))
        {
            log?.Invoke($"profile {symbol} {year}: {error}, skipped");
            return default;
        }
        long yearStart = YearStartUnix(year);
        long yearEnd = YearStartUnix(year + 1);
        int records = 0;
        int cells = 0;
        bool ordered = true;
        long previous = long.MinValue;
        int offset = HeaderSize;
        while (ValidateRecord(bytes, offset, bytes.Length, yearStart, yearEnd, out var minute, out var length))
        {
            if (minute <= previous) ordered = false;
            previous = minute;
            records++;
            cells += (length - FixedRecordBytes - 4) / 4;
            offset += length;
        }
        if (offset < bytes.Length)
            log?.Invoke($"profile {symbol} {year}: stopped at byte {offset} of {bytes.Length}");
        if (ordered) return new ProfileYear(bytes, records, cells, null, 0);
        var byMinute = new Dictionary<long, ProfileRecord>(records);
        int duplicates = 0;
        offset = HeaderSize;
        for (int k = 0; k < records; k++)
        {
            var r = Decode(bytes, offset);
            if (byMinute.ContainsKey(r.MinuteUnix)) duplicates++;
            byMinute[r.MinuteUnix] = r;
            offset += EncodedLength(r);
        }
        var sorted = byMinute.Values.ToList();
        sorted.Sort((a, b) => a.MinuteUnix.CompareTo(b.MinuteUnix));
        return new ProfileYear(null, sorted.Count, sorted.Sum(r => r.Span), sorted, duplicates);
    }
}

public readonly record struct ProfileYear(
    byte[]? Bytes, int Records, int Cells, List<ProfileRecord>? Sorted, int Duplicates);

public sealed class VolumeProfileCache
{
    private sealed class Entry
    {
        public Dictionary<int, string> Stamps = new();
        public ProfileSet? Set;
    }

    public readonly record struct LoadResult(ProfileSet? Set, int YearsRead, int RecordsRead, int Duplicates);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public LoadResult Load(string symbolDirectory, string symbol, ProfileScale scale, Action<string>? log = null)
    {
        Entry entry;
        lock (_entries)
        {
            var key = $"{symbolDirectory}|{symbol}|{scale.PipPoints}|{scale.PriceScale}";
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
        }
        lock (entry)
        {
            var stamps = new Dictionary<int, string>();
            foreach (var year in VolumeProfileStore.Years(symbolDirectory))
                stamps[year] = Stamp(VolumeProfileStore.YearPath(symbolDirectory, year));
            if (stamps.Count == 0)
            {
                entry.Stamps = stamps;
                entry.Set = null;
                return new LoadResult(null, 0, 0, 0);
            }
            int firstChanged = int.MaxValue;
            foreach (var (year, stamp) in stamps)
                if (!entry.Stamps.TryGetValue(year, out var had) || had != stamp)
                    firstChanged = Math.Min(firstChanged, year);
            foreach (var year in entry.Stamps.Keys)
                if (!stamps.ContainsKey(year)) firstChanged = Math.Min(firstChanged, year);
            if (entry.Set == null) firstChanged = int.MinValue;
            if (firstChanged == int.MaxValue) return new LoadResult(entry.Set, 0, 0, 0);

            var head = entry.Set != null && firstChanged != int.MinValue
                ? entry.Set.Before(VolumeProfileStore.YearStartUnix(firstChanged))
                : ProfileSet.Empty;
            var years = new List<ProfileYear>();
            int recordsRead = 0;
            int duplicates = 0;
            foreach (var year in stamps.Keys.Where(y => y >= firstChanged).OrderBy(y => y))
            {
                var scan = VolumeProfileStore.ScanYear(symbolDirectory, symbol, year,
                    scale.PipPoints, scale.PriceScale, log);
                years.Add(scan);
                recordsRead += scan.Records;
                duplicates += scan.Duplicates;
            }
            int yearsRead = years.Count;
            var set = ProfileSet.Build(head, years);
            entry.Stamps = stamps;
            entry.Set = set.Count > 0 ? set : null;
            return new LoadResult(entry.Set, yearsRead, recordsRead, duplicates);
        }
    }

    private static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return "";
        }
    }
}

public sealed class VolumeProfileWriter : IDisposable
{
    private sealed class YearFile
    {
        public required FileStream Fs;
        public long End;
    }

    private readonly string _symbolDirectory;
    private readonly string _symbol;
    private readonly int _pipPoints;
    private readonly int _priceScale;
    private readonly Action<string> _log;
    private readonly Dictionary<int, YearFile> _files = new();
    private readonly object _lock = new();

    public VolumeProfileWriter(string symbolDirectory, string symbol, int pipPoints, int priceScale,
        Action<string> log)
    {
        _symbolDirectory = symbolDirectory;
        _symbol = symbol;
        _pipPoints = pipPoints;
        _priceScale = priceScale;
        _log = log;
    }

    public List<ProfileRecord> Append(IReadOnlyList<ProfileRecord> records)
    {
        var written = new List<ProfileRecord>();
        if (records.Count == 0) return written;
        lock (_lock)
        {
            foreach (var group in records.GroupBy(r =>
                DateTimeOffset.FromUnixTimeSeconds(r.MinuteUnix).UtcDateTime.Year))
            {
                var year = Open(group.Key);
                if (year == null) continue;
                if (!TrimStrayTail(group.Key, year)) continue;
                int total = 0;
                foreach (var r in group) total += VolumeProfileStore.EncodedLength(r);
                var buf = new byte[total];
                int offset = 0;
                foreach (var r in group)
                {
                    int len = VolumeProfileStore.EncodedLength(r);
                    VolumeProfileStore.Encode(r, buf.AsSpan(offset, len));
                    offset += len;
                }
                try
                {
                    year.Fs.Position = year.End;
                    year.Fs.Write(buf);
                }
                catch (Exception ex)
                {
                    _log($"profile {_symbol} {group.Key}: append failed: {ex.Message}");
                    continue;
                }
                year.End += buf.Length;
                written.AddRange(group);
            }
        }
        return written;
    }

    private bool TrimStrayTail(int yearNumber, YearFile year)
    {
        if (year.Fs.Length == year.End) return true;
        try
        {
            var path = VolumeProfileStore.YearPath(_symbolDirectory, yearNumber);
            var stray = new byte[year.Fs.Length - year.End];
            year.Fs.Position = year.End;
            year.Fs.ReadExactly(stray);
            File.WriteAllBytes(path + ".torn", stray);
            year.Fs.SetLength(year.End);
            year.Fs.Flush(true);
            _log($"profile {_symbol} {yearNumber}: {stray.Length} stray bytes after a failed " +
                $"append kept in {yearNumber}.vap.torn, file trimmed to {year.End}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"profile {_symbol} {yearNumber}: stray tail trim failed: {ex.Message}");
            return false;
        }
    }

    public long LastStoredMinute()
    {
        lock (_lock)
        {
            var dir = VolumeProfileStore.Directory(_symbolDirectory);
            if (!Directory.Exists(dir)) return 0;
            var years = new List<int>();
            foreach (var path in Directory.GetFiles(dir, "*.vap"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var y))
                    years.Add(y);
            years.Sort();
            for (int i = years.Count - 1; i >= 0; i--)
            {
                var year = Open(years[i]);
                if (year == null || year.End <= VolumeProfileStore.HeaderSize) continue;
                var bytes = new byte[year.End];
                year.Fs.Position = 0;
                year.Fs.ReadExactly(bytes);
                long yearStart = VolumeProfileStore.YearStartUnix(years[i]);
                long yearEnd = VolumeProfileStore.YearStartUnix(years[i] + 1);
                long last = 0;
                int offset = VolumeProfileStore.HeaderSize;
                while (VolumeProfileStore.ValidateRecord(bytes, offset, bytes.Length, yearStart, yearEnd,
                    out var minute, out var length))
                {
                    if (minute > last) last = minute;
                    offset += length;
                }
                if (last > 0) return last;
            }
            return 0;
        }
    }

    private YearFile? Open(int year)
    {
        if (_files.TryGetValue(year, out var cached)) return cached;
        var path = VolumeProfileStore.YearPath(_symbolDirectory, year);
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            long end;
            if (fs.Length == 0)
            {
                Span<byte> header = stackalloc byte[VolumeProfileStore.HeaderSize];
                VolumeProfileStore.WriteHeader(header, _symbol, year, _pipPoints, _priceScale);
                fs.Write(header);
                fs.Flush(true);
                end = VolumeProfileStore.HeaderSize;
            }
            else
            {
                var bytes = new byte[fs.Length];
                fs.Position = 0;
                fs.ReadExactly(bytes);
                if (!VolumeProfileStore.ValidateHeader(bytes, _symbol, year, _pipPoints, _priceScale, out var error))
                {
                    fs.Dispose();
                    var bad = path + ".bad";
                    File.Move(path, bad, true);
                    _log($"profile {_symbol} {year}: {error}, moved to {Path.GetFileName(bad)}");
                    fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    Span<byte> header = stackalloc byte[VolumeProfileStore.HeaderSize];
                    VolumeProfileStore.WriteHeader(header, _symbol, year, _pipPoints, _priceScale);
                    fs.Write(header);
                    fs.Flush(true);
                    end = VolumeProfileStore.HeaderSize;
                }
                else
                {
                    long yearStart = VolumeProfileStore.YearStartUnix(year);
                    long yearEnd = VolumeProfileStore.YearStartUnix(year + 1);
                    int offset = VolumeProfileStore.HeaderSize;
                    while (VolumeProfileStore.ValidateRecord(bytes, offset, bytes.Length, yearStart, yearEnd,
                        out _, out var length))
                        offset += length;
                    if (offset < bytes.Length)
                    {
                        var torn = path + ".torn";
                        File.WriteAllBytes(torn, bytes.AsSpan(offset).ToArray());
                        fs.SetLength(offset);
                        fs.Flush(true);
                        _log($"profile {_symbol} {year}: torn tail, kept {bytes.Length - offset} bytes " +
                            $"in {Path.GetFileName(torn)}, file trimmed to {offset}");
                    }
                    end = offset;
                }
            }
            var opened = new YearFile { Fs = fs, End = end };
            _files[year] = opened;
            return opened;
        }
        catch (Exception ex)
        {
            _log($"profile {_symbol} {year}: open failed: {ex.Message}");
            return null;
        }
    }

    public void Flush()
    {
        lock (_lock)
            foreach (var year in _files.Values)
                try { year.Fs.Flush(true); } catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var year in _files.Values)
            {
                try { year.Fs.Flush(true); } catch { }
                year.Fs.Dispose();
            }
            _files.Clear();
        }
    }
}
