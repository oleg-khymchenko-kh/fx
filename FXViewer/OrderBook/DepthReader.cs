using System.Buffers.Binary;
using System.IO;

namespace FXViewer.OrderBook;

public sealed class DepthBook
{
    private readonly Dictionary<int, uint> _bids = new(1024);
    private readonly Dictionary<int, uint> _asks = new(1024);
    private bool _sawClear;

    public bool Started { get; private set; }

    public long CurrentMinute { get; private set; } = -1;

    public void Reset()
    {
        _bids.Clear();
        _asks.Clear();
        _sawClear = false;
        Started = false;
        CurrentMinute = -1;
    }

    public void Apply(byte command, int pricePoints, uint quantity)
    {
        if (command == DepthReader.CommandClearBook)
        {
            _bids.Clear();
            _asks.Clear();
            _sawClear = true;
            return;
        }
        switch (command)
        {
            case DepthReader.CommandAddBid:
            case DepthReader.CommandModifyBid:
                _bids[pricePoints] = quantity;
                break;
            case DepthReader.CommandAddAsk:
            case DepthReader.CommandModifyAsk:
                _asks[pricePoints] = quantity;
                break;
            case DepthReader.CommandDeleteBid:
                _bids.Remove(pricePoints);
                break;
            case DepthReader.CommandDeleteAsk:
                _asks.Remove(pricePoints);
                break;
        }
    }

    public void TryStart(byte flags, long minute)
    {
        if (Started || !_sawClear) return;
        if ((flags & DepthReader.FlagEndOfBatch) == 0) return;
        if (_bids.Count == 0 || _asks.Count == 0) return;
        Started = true;
        CurrentMinute = minute;
    }

    public void OpenMinute(long minute) => CurrentMinute = minute;

    public bool TryBuild(long minuteUnix, int pipPoints, out DepthSnapshot snapshot)
    {
        snapshot = default;
        if (_bids.Count == 0 || _asks.Count == 0) return false;
        int bestBid = int.MinValue;
        foreach (var k in _bids.Keys) if (k > bestBid) bestBid = k;
        int bestAsk = int.MaxValue;
        foreach (var k in _asks.Keys) if (k < bestAsk) bestAsk = k;
        if (bestAsk <= bestBid) return false;
        int mid = (int)(((long)bestBid + bestAsk) / 2);

        var bid = new ushort[DepthStore.Levels];
        var ask = new ushort[DepthStore.Levels];
        foreach (var (k, q) in _bids)
        {
            int off = mid - k;
            if (off < 0) continue;
            int idx = off / pipPoints;
            if (idx >= DepthStore.Levels) continue;
            bid[idx] = (ushort)Math.Min(bid[idx] + (long)q, ushort.MaxValue);
        }
        foreach (var (k, q) in _asks)
        {
            int off = k - mid;
            if (off < 0) continue;
            int idx = off / pipPoints;
            if (idx >= DepthStore.Levels) continue;
            ask[idx] = (ushort)Math.Min(ask[idx] + (long)q, ushort.MaxValue);
        }
        snapshot = new DepthSnapshot(minuteUnix, mid, bid, ask);
        return true;
    }
}

public static class DepthReader
{
    public const long EpochOffset = 2209161600L;
    public const int HeaderMin = 64;
    public const int RecordSize = 24;
    public const byte CommandClearBook = 1;
    public const byte CommandAddBid = 2;
    public const byte CommandAddAsk = 3;
    public const byte CommandModifyBid = 4;
    public const byte CommandModifyAsk = 5;
    public const byte CommandDeleteBid = 6;
    public const byte CommandDeleteAsk = 7;
    public const byte FlagEndOfBatch = 0x01;

    private const int ReadRecords = 16384;

    public readonly record struct TailResult(
        long Offset, long Records, bool Restarted, string Error)
    {
        public bool Ok => Error.Length == 0;
    }

    public static TailResult ReadTail(string path, long fromOffset, int priceScale,
        DepthBook book, Action<long, DepthBook> onMinuteClosed)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                1 << 20, FileOptions.SequentialScan);
            var head = new byte[HeaderMin];
            if (fs.Length < head.Length)
                return new TailResult(fromOffset, 0, false, "shorter than the header");
            fs.ReadExactly(head);
            if (head[0] != (byte)'S' || head[1] != (byte)'C' || head[2] != (byte)'D' || head[3] != (byte)'D')
                return new TailResult(fromOffset, 0, false, "not a .depth file (bad magic)");
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            int recordSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
            if (headerSize < HeaderMin || recordSize != RecordSize)
                return new TailResult(fromOffset, 0, false,
                    $"unexpected header {headerSize} / record {recordSize}");

            bool restarted = false;
            long start = fromOffset;
            if (start < headerSize || start > fs.Length || (start - headerSize) % recordSize != 0)
            {
                restarted = fromOffset > 0;
                start = headerSize;
                book.Reset();
            }

            fs.Position = start;
            var buf = new byte[recordSize * ReadRecords];
            long offset = start;
            long records = 0;
            while (true)
            {
                int read = fs.Read(buf, 0, buf.Length);
                if (read < recordSize) break;
                int usable = read - read % recordSize;
                for (int off = 0; off < usable; off += recordSize)
                {
                    long micros = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off));
                    byte command = buf[off + 8];
                    byte flags = buf[off + 9];
                    float price = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(off + 12));
                    uint quantity = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 16));
                    records++;

                    long unix = micros / 1_000_000 - EpochOffset;
                    long minute = unix - Mod(unix, 60);
                    if (book.Started && book.CurrentMinute >= 0 && minute > book.CurrentMinute)
                    {
                        onMinuteClosed(book.CurrentMinute, book);
                        book.OpenMinute(minute);
                    }
                    book.Apply(command, (int)Math.Round(price * priceScale), quantity);
                    book.TryStart(flags, minute);
                }
                offset += usable;
                if (usable < buf.Length) break;
            }
            return new TailResult(offset, records, restarted, "");
        }
        catch (Exception ex)
        {
            return new TailResult(fromOffset, 0, false, ex.Message);
        }
    }

    public static long Mod(long value, long m)
    {
        long r = value % m;
        return r < 0 ? r + m : r;
    }
}
