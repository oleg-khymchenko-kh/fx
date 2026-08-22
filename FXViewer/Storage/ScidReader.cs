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

    public static TailResult ReadMinuteVolumes(string path, long fromOffset, Dictionary<long, long> into,
        Dictionary<long, TickMinute>? ticksInto = null, bool reciprocal = false)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[MinHeaderSize];
            if (fs.Length < head.Length)
                return new TailResult(fromOffset, 0, 0, false, "file shorter than the header");
            fs.ReadExactly(head);
            if (head[0] != (byte)'S' || head[1] != (byte)'C' || head[2] != (byte)'I' || head[3] != (byte)'D')
                return new TailResult(fromOffset, 0, 0, false, "not a .scid file (bad magic)");
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            int recordSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
            if (headerSize < MinHeaderSize || recordSize < MinRecordSize)
                return new TailResult(fromOffset, 0, 0, false,
                    $"unexpected header {headerSize} / record {recordSize}");

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

    public static long Mod(long value, long m)
    {
        long r = value % m;
        return r < 0 ? r + m : r;
    }
}
