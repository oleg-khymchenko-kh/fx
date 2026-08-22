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
    public readonly long[] MinuteUnix;
    public readonly int[] BasePip;
    public readonly int[] Start;
    public readonly int[] Bid;
    public readonly int[] Ask;

    private ProfileSet(long[] minuteUnix, int[] basePip, int[] start, int[] bid, int[] ask)
    {
        MinuteUnix = minuteUnix;
        BasePip = basePip;
        Start = start;
        Bid = bid;
        Ask = ask;
    }

    public int Count => MinuteUnix.Length;

    public static readonly ProfileSet Empty = new(
        Array.Empty<long>(), Array.Empty<int>(), new int[1], Array.Empty<int>(), Array.Empty<int>());

    public static ProfileSet FromRecords(IReadOnlyList<ProfileRecord> records)
    {
        int n = records.Count;
        var minute = new long[n];
        var basePip = new int[n];
        var start = new int[n + 1];
        int cells = 0;
        for (int i = 0; i < n; i++) cells += records[i].Span;
        var bid = new int[cells];
        var ask = new int[cells];
        int offset = 0;
        for (int i = 0; i < n; i++)
        {
            var r = records[i];
            minute[i] = r.MinuteUnix;
            basePip[i] = r.BasePip;
            start[i] = offset;
            for (int j = 0; j < r.Span; j++)
            {
                bid[offset + j] = r.Bid[j];
                ask[offset + j] = r.Ask[j];
            }
            offset += r.Span;
        }
        start[n] = offset;
        return new ProfileSet(minute, basePip, start, bid, ask);
    }

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
        int n = Count;
        int m = deduped.Count;
        var minute = new List<long>(n + m);
        var basePip = new List<int>(n + m);
        var start = new List<int>(n + m + 1);
        var bid = new List<int>(Bid.Length + m * 4);
        var ask = new List<int>(Ask.Length + m * 4);
        int i2 = 0;
        int j2 = 0;
        void TakeOld(int idx)
        {
            minute.Add(MinuteUnix[idx]);
            basePip.Add(BasePip[idx]);
            start.Add(bid.Count);
            for (int c = Start[idx]; c < Start[idx + 1]; c++)
            {
                bid.Add(Bid[c]);
                ask.Add(Ask[c]);
            }
        }
        void TakeFresh(ProfileRecord r)
        {
            minute.Add(r.MinuteUnix);
            basePip.Add(r.BasePip);
            start.Add(bid.Count);
            for (int c = 0; c < r.Span; c++)
            {
                bid.Add(r.Bid[c]);
                ask.Add(r.Ask[c]);
            }
        }
        while (i2 < n || j2 < m)
        {
            if (j2 >= m) TakeOld(i2++);
            else if (i2 >= n) TakeFresh(deduped[j2++]);
            else if (MinuteUnix[i2] < deduped[j2].MinuteUnix) TakeOld(i2++);
            else if (MinuteUnix[i2] > deduped[j2].MinuteUnix) TakeFresh(deduped[j2++]);
            else
            {
                TakeFresh(deduped[j2++]);
                i2++;
            }
        }
        start.Add(bid.Count);
        return new ProfileSet(minute.ToArray(), basePip.ToArray(), start.ToArray(),
            bid.ToArray(), ask.ToArray());
    }

    public int LowerBound(long minuteUnix)
    {
        int lo = 0;
        int hi = MinuteUnix.Length;
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

public static class VolumeProfileStore
{
    public const uint Magic = 0x41565846;
    public const int FormatVersion = 1;
    public const int HeaderSize = 64;
    public const int FixedRecordBytes = 16;
    public const int MaxSpan = 1024;

    public static readonly HashSet<string> SupportedPairs =
        new(StringComparer.OrdinalIgnoreCase) { "EURUSD", "GBPUSD", "USDCHF" };

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

    public static List<ProfileRecord> ReadAll(string symbolDirectory, string symbol, int pipPoints,
        int priceScale, out int duplicates, Action<string>? log = null)
    {
        duplicates = 0;
        var byMinute = new Dictionary<long, ProfileRecord>();
        var dir = Directory(symbolDirectory);
        if (!System.IO.Directory.Exists(dir)) return new List<ProfileRecord>();
        var years = new List<int>();
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.vap"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var y))
                years.Add(y);
        years.Sort();
        foreach (var year in years)
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
                continue;
            }
            if (!ValidateHeader(bytes, symbol, year, pipPoints, priceScale, out var error))
            {
                log?.Invoke($"profile {symbol} {year}: {error}, skipped");
                continue;
            }
            long yearStart = YearStartUnix(year);
            long yearEnd = YearStartUnix(year + 1);
            int offset = HeaderSize;
            while (ValidateRecord(bytes, offset, bytes.Length, yearStart, yearEnd, out var minute, out var length))
            {
                if (byMinute.ContainsKey(minute)) duplicates++;
                byMinute[minute] = Decode(bytes, offset);
                offset += length;
            }
            if (offset < bytes.Length)
                log?.Invoke($"profile {symbol} {year}: stopped at byte {offset} of {bytes.Length}");
        }
        var result = byMinute.Values.ToList();
        result.Sort((a, b) => a.MinuteUnix.CompareTo(b.MinuteUnix));
        return result;
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
