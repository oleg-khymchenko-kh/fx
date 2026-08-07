using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FXViewer.Storage;

public readonly record struct StoredCandle(int MinuteOfYear, int Min, int Max, int Avg, bool AvgApproximated);

public sealed class CandleYearFile : IDisposable
{
    public const int HeaderSize = 64;
    public const int RecordSize = 16;
    public const uint Magic = 0x314D5846;
    public const int FormatVersion = 1;

    public const uint FlagFilled = 1u << 0;
    public const uint FlagAvgApprox = 1u << 1;
    public const uint FlagProvisional = 1u << 4;

    public string Symbol { get; }
    public int Year { get; }
    public int MinutesInYear { get; }
    public int PriceScale { get; }
    public int Digits { get; }

    private readonly FileStream _fs;
    private readonly object _lock = new();
    private readonly byte[] _rec = new byte[RecordSize];

    public static int MinutesIn(int year) => (DateTime.IsLeapYear(year) ? 366 : 365) * 1440;

    public CandleYearFile(string path, string symbol, int year, int digits = 5, int priceScale = 100000)
    {
        Symbol = symbol;
        Year = year;
        Digits = digits;
        PriceScale = priceScale;
        MinutesInYear = MinutesIn(year);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (_fs.Length == 0)
            InitFile();
        else
            OpenExisting();
    }

    private long FileSize => HeaderSize + (long)MinutesInYear * RecordSize;

    private void InitFile()
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), Year);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), MinutesInYear);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), PriceScale);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), Digits);
        var name = Encoding.ASCII.GetBytes(Symbol);
        Array.Copy(name, 0, header, 28, Math.Min(name.Length, 16));
        _fs.Position = 0;
        _fs.Write(header);
        _fs.SetLength(FileSize);
        _fs.Flush(true);
    }

    private void OpenExisting()
    {
        var header = new byte[HeaderSize];
        _fs.Position = 0;
        _fs.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0));
        if (magic != Magic)
            throw new InvalidDataException("Not a candle file: " + _fs.Name);
        var recSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var year = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var mins = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if (recSize != RecordSize || year != Year || mins != MinutesInYear)
            throw new InvalidDataException(
                $"Header mismatch in {_fs.Name}: year={year}, minutes={mins}, recordSize={recSize}");
        if (_fs.Length != FileSize)
            _fs.SetLength(FileSize);
    }

    public void Write(int minuteOfYear, int min, int max, int avg, bool avgApprox,
        bool provisional = false)
    {
        if ((uint)minuteOfYear >= (uint)MinutesInYear)
            throw new ArgumentOutOfRangeException(nameof(minuteOfYear));
        uint flags = FlagFilled | (avgApprox ? FlagAvgApprox : 0u)
            | (provisional ? FlagProvisional : 0u);
        lock (_lock)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_rec.AsSpan(0), min);
            BinaryPrimitives.WriteInt32LittleEndian(_rec.AsSpan(4), max);
            BinaryPrimitives.WriteInt32LittleEndian(_rec.AsSpan(8), avg);
            BinaryPrimitives.WriteUInt32LittleEndian(_rec.AsSpan(12), flags);
            _fs.Position = HeaderSize + (long)minuteOfYear * RecordSize;
            _fs.Write(_rec);
        }
    }

    public bool TryGet(int minuteOfYear, out StoredCandle candle)
    {
        candle = default;
        if ((uint)minuteOfYear >= (uint)MinutesInYear) return false;
        Span<byte> buf = stackalloc byte[RecordSize];
        lock (_lock)
        {
            _fs.Position = HeaderSize + (long)minuteOfYear * RecordSize;
            _fs.ReadExactly(buf);
        }
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12));
        if ((flags & FlagFilled) == 0) return false;
        candle = new StoredCandle(
            minuteOfYear,
            BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(0)),
            BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4)),
            BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(8)),
            (flags & FlagAvgApprox) != 0);
        return true;
    }

    public List<StoredCandle> ReadRange(int fromMinute, int toMinute)
    {
        var result = new List<StoredCandle>();
        fromMinute = Math.Max(0, fromMinute);
        toMinute = Math.Min(MinutesInYear - 1, toMinute);
        if (toMinute < fromMinute) return result;
        int count = toMinute - fromMinute + 1;
        var buf = new byte[count * RecordSize];
        lock (_lock)
        {
            _fs.Position = HeaderSize + (long)fromMinute * RecordSize;
            _fs.ReadExactly(buf);
        }
        for (int i = 0; i < count; i++)
        {
            int off = i * RecordSize;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 12));
            if ((flags & FlagFilled) == 0) continue;
            result.Add(new StoredCandle(
                fromMinute + i,
                BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 0)),
                BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 8)),
                (flags & FlagAvgApprox) != 0));
        }
        return result;
    }

    public int FirstFilledMinute()
    {
        var buf = new byte[RecordSize * 4096];
        lock (_lock)
        {
            long start = 0;
            while (start < MinutesInYear)
            {
                int count = (int)Math.Min(4096, MinutesInYear - start);
                _fs.Position = HeaderSize + start * RecordSize;
                _fs.ReadExactly(buf.AsSpan(0, count * RecordSize));
                for (int i = 0; i < count; i++)
                {
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * RecordSize + 12));
                    if ((flags & FlagFilled) != 0) return (int)(start + i);
                }
                start += count;
            }
        }
        return -1;
    }

    public int LastFilledMinute()
    {
        var buf = new byte[RecordSize * 4096];
        lock (_lock)
        {
            long end = MinutesInYear;
            while (end > 0)
            {
                int count = (int)Math.Min(4096, end);
                long start = end - count;
                _fs.Position = HeaderSize + start * RecordSize;
                _fs.ReadExactly(buf.AsSpan(0, count * RecordSize));
                for (int i = count - 1; i >= 0; i--)
                {
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * RecordSize + 12));
                    if ((flags & FlagFilled) != 0) return (int)(start + i);
                }
                end = start;
            }
        }
        return -1;
    }

    public int CountFilled()
    {
        int count = 0;
        var buf = new byte[RecordSize * 4096];
        lock (_lock)
        {
            _fs.Position = HeaderSize;
            long remaining = (long)MinutesInYear * RecordSize;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buf.Length, remaining);
                _fs.ReadExactly(buf.AsSpan(0, toRead));
                for (int off = 0; off < toRead; off += RecordSize)
                {
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 12));
                    if ((flags & FlagFilled) != 0) count++;
                }
                remaining -= toRead;
            }
        }
        return count;
    }

    public int FirstProvisionalMinute(int fromMinute, int toMinute) =>
        ScanProvisional(fromMinute, toMinute, forward: true);

    public int LastProvisionalMinute(int fromMinute, int toMinute) =>
        ScanProvisional(fromMinute, toMinute, forward: false);

    private const uint ProvisionalMask = FlagFilled | FlagProvisional;

    private int ScanProvisional(int fromMinute, int toMinute, bool forward)
    {
        fromMinute = Math.Max(0, fromMinute);
        toMinute = Math.Min(MinutesInYear - 1, toMinute);
        if (toMinute < fromMinute) return -1;
        var buf = new byte[RecordSize * 4096];
        lock (_lock)
        {
            if (forward)
            {
                long start = fromMinute;
                while (start <= toMinute)
                {
                    int count = (int)Math.Min(4096, toMinute - start + 1);
                    _fs.Position = HeaderSize + start * RecordSize;
                    _fs.ReadExactly(buf.AsSpan(0, count * RecordSize));
                    for (int i = 0; i < count; i++)
                    {
                        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * RecordSize + 12));
                        if ((flags & ProvisionalMask) == ProvisionalMask) return (int)(start + i);
                    }
                    start += count;
                }
            }
            else
            {
                long end = toMinute + 1;
                while (end > fromMinute)
                {
                    int count = (int)Math.Min(4096, end - fromMinute);
                    long start = end - count;
                    _fs.Position = HeaderSize + start * RecordSize;
                    _fs.ReadExactly(buf.AsSpan(0, count * RecordSize));
                    for (int i = count - 1; i >= 0; i--)
                    {
                        var flags = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * RecordSize + 12));
                        if ((flags & ProvisionalMask) == ProvisionalMask) return (int)(start + i);
                    }
                    end = start;
                }
            }
        }
        return -1;
    }

    public void Flush()
    {
        lock (_lock) _fs.Flush(true);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _fs.Flush(true); } catch { }
            _fs.Dispose();
        }
    }
}
