using System.Buffers.Binary;
using System.IO;

namespace FXViewer.Storage;

public static class ScidReader
{
    public const long EpochOffset = 2209161600L;

    private const int MinHeaderSize = 56;
    private const int MinRecordSize = 40;
    private const int VolumeFieldOffset = 28;
    private const int ReadRecords = 4096;
    private const int PriceScale = 100000;

    public readonly record struct TailResult(
        long Offset, int Records, long NewestMinute, bool Restarted, string Error)
    {
        public bool Ok => Error.Length == 0;
    }

    public readonly record struct RecentResult(
        bool Exists, long Volume, long WideVolume, long NewestUnix, string Error)
    {
        public bool Ok => Error.Length == 0;
    }

    public static TailResult ReadMinuteVolumes(string path, long fromOffset, Dictionary<long, long> into,
        Dictionary<long, TickMinute>? ticksInto = null, bool reciprocal = false)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!TryReadHeader(fs, out int headerSize, out int recordSize, out string error))
                return new TailResult(fromOffset, 0, 0, false, error);

            bool restarted = false;
            long start = fromOffset;
            if (start < headerSize || start > fs.Length || (start - headerSize) % recordSize != 0)
            {
                restarted = fromOffset > 0;
                start = headerSize;
            }
            if (restarted)
            {
                into.Clear();
                ticksInto?.Clear();
            }

            fs.Position = start;
            var buf = new byte[recordSize * ReadRecords];
            long offset = start;
            int records = 0;
            long newest = 0;
            while (true)
            {
                int read = fs.Read(buf, 0, buf.Length);
                if (read < recordSize) break;
                int usable = read - read % recordSize;
                for (int off = 0; off < usable; off += recordSize)
                {
                    long micros = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off));
                    if (micros <= 0) continue;
                    long unix = micros / 1_000_000 - EpochOffset;
                    long minute = unix - Mod(unix, 60);
                    if (minute > newest) newest = minute;
                    uint volume = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + VolumeFieldOffset));
                    if (volume == 0) continue;
                    into[minute] = into.TryGetValue(minute, out var have) ? have + volume : volume;
                    if (ticksInto == null) continue;
                    float open = BitConverter.ToSingle(buf, off + 8);
                    if (open != 0f && open >= -1e30f) continue;
                    float close = BitConverter.ToSingle(buf, off + 20);
                    if (close <= 0f) continue;
                    uint bid = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 32));
                    uint ask = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + 36));
                    if (!ticksInto.TryGetValue(minute, out var accum))
                    {
                        accum = new TickMinute();
                        ticksInto[minute] = accum;
                    }
                    accum.Add(VolumeProfileStore.SpotPoints(close, reciprocal, PriceScale), volume,
                        reciprocal ? ask : bid, reciprocal ? bid : ask);
                }
                offset += usable;
                records += usable / recordSize;
                if (usable < buf.Length) break;
            }
            return new TailResult(offset, records, newest, restarted, "");
        }
        catch (Exception ex)
        {
            return new TailResult(fromOffset, 0, 0, false, ex.Message);
        }
    }

    public static RecentResult ReadRecent(string path, long sinceUnix, long wideSinceUnix)
    {
        if (!File.Exists(path)) return new RecentResult(false, 0, 0, 0, "");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!TryReadHeader(fs, out int headerSize, out int recordSize, out string error))
                return new RecentResult(true, 0, 0, 0, error);

            long records = (fs.Length - headerSize) / recordSize;
            var buf = new byte[recordSize * ReadRecords];
            long volume = 0;
            long wide = 0;
            long newest = 0;
            long end = records;
            while (end > 0)
            {
                long begin = Math.Max(0, end - ReadRecords);
                int count = (int)(end - begin);
                fs.Position = headerSize + begin * recordSize;
                fs.ReadExactly(buf, 0, count * recordSize);
                for (int i = count - 1; i >= 0; i--)
                {
                    int off = i * recordSize;
                    long micros = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off));
                    if (micros <= 0) continue;
                    long unix = micros / 1_000_000 - EpochOffset;
                    if (unix > newest) newest = unix;
                    if (unix < wideSinceUnix)
                        return new RecentResult(true, volume, wide, newest, "");
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off + VolumeFieldOffset));
                    wide += v;
                    if (unix >= sinceUnix) volume += v;
                }
                end = begin;
            }
            return new RecentResult(true, volume, wide, newest, "");
        }
        catch (Exception ex)
        {
            return new RecentResult(true, 0, 0, 0, ex.Message);
        }
    }

    private static bool TryReadHeader(FileStream fs, out int headerSize, out int recordSize, out string error)
    {
        headerSize = 0;
        recordSize = 0;
        error = "";
        if (fs.Length < MinHeaderSize)
        {
            error = "file shorter than the header";
            return false;
        }
        Span<byte> head = stackalloc byte[MinHeaderSize];
        fs.Position = 0;
        fs.ReadExactly(head);
        if (head[0] != (byte)'S' || head[1] != (byte)'C' || head[2] != (byte)'I' || head[3] != (byte)'D')
        {
            error = "not a .scid file (bad magic)";
            return false;
        }
        headerSize = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(4));
        recordSize = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(8));
        if (headerSize < MinHeaderSize || recordSize < MinRecordSize)
        {
            error = $"unexpected header {headerSize} / record {recordSize}";
            return false;
        }
        return true;
    }

    public static long Mod(long value, long m)
    {
        long r = value % m;
        return r < 0 ? r + m : r;
    }
}
