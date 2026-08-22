using System.Buffers.Binary;
using System.Globalization;
using System.IO;

namespace FXViewer.OrderBook;

public readonly record struct DepthSnapshot(long MinuteUnix, int MidPoints, ushort[] Bid, ushort[] Ask);

public static class DepthStore
{
    public const int Version = 1;
    public const int Levels = 100;
    public const int HeaderBytes = 16;
    public const int RecordBytes = 8 + 4 + Levels * 2 * 2;

    private static readonly byte[] Magic = { (byte)'F', (byte)'X', (byte)'D', (byte)'P' };

    public static string Directory(string symbolDirectory) => Path.Combine(symbolDirectory, "depth");

    public static string YearPath(string symbolDirectory, int year) =>
        Path.Combine(Directory(symbolDirectory), year.ToString(CultureInfo.InvariantCulture) + ".dpt");

    public static void WriteHeader(Stream stream, int pipPoints)
    {
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Levels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), pipPoints);
        stream.Write(header);
    }

    public static bool ReadHeader(Stream stream, out int levels, out int pipPoints)
    {
        levels = 0;
        pipPoints = 0;
        if (stream.Length < HeaderBytes) return false;
        var header = new byte[HeaderBytes];
        stream.Position = 0;
        stream.ReadExactly(header);
        for (int i = 0; i < Magic.Length; i++)
            if (header[i] != Magic[i]) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != Version) return false;
        levels = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        pipPoints = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        return levels > 0 && pipPoints > 0;
    }

    public static void WriteRecord(Stream stream, byte[] buffer, long minuteUnix, int midPoints,
        ushort[] bid, ushort[] ask)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0), minuteUnix);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), midPoints);
        for (int i = 0; i < Levels; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12 + i * 2), bid[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12 + Levels * 2 + i * 2), ask[i]);
        }
        stream.Write(buffer, 0, RecordBytes);
    }

    public static long LastMinute(string symbolDirectory, int year)
    {
        var path = YearPath(symbolDirectory, year);
        if (!File.Exists(path)) return 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long body = fs.Length - HeaderBytes;
            if (body < RecordBytes || body % RecordBytes != 0) return 0;
            fs.Position = fs.Length - RecordBytes;
            var buf = new byte[8];
            fs.ReadExactly(buf);
            return BinaryPrimitives.ReadInt64LittleEndian(buf);
        }
        catch
        {
            return 0;
        }
    }

    public static int Append(string symbolDirectory, int year, int pipPoints,
        IReadOnlyList<DepthSnapshot> records, Action<string> log)
    {
        if (records.Count == 0) return 0;
        var path = YearPath(symbolDirectory, year);
        try
        {
            System.IO.Directory.CreateDirectory(Directory(symbolDirectory));
            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.Read);
            if (fs.Length < HeaderBytes)
            {
                fs.SetLength(0);
                WriteHeader(fs, pipPoints);
            }
            else
            {
                long body = fs.Length - HeaderBytes;
                long stray = body % RecordBytes;
                if (stray != 0)
                {
                    fs.SetLength(fs.Length - stray);
                    log($"depth {year}: dropped {stray} stray byte(s) at the end");
                }
            }
            fs.Position = fs.Length;
            var buffer = new byte[RecordBytes];
            foreach (var r in records)
                WriteRecord(fs, buffer, r.MinuteUnix, r.MidPoints, r.Bid, r.Ask);
            fs.Flush(true);
            return records.Count;
        }
        catch (Exception ex)
        {
            log($"depth {year}: append failed: {ex.Message}");
            return 0;
        }
    }

    public static List<DepthSnapshot> ReadAll(string symbolDirectory, out int pipPoints)
    {
        pipPoints = 0;
        var result = new List<DepthSnapshot>();
        var dir = Directory(symbolDirectory);
        if (!System.IO.Directory.Exists(dir)) return result;
        var years = new List<int>();
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.dpt"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var y))
                years.Add(y);
        years.Sort();
        foreach (var year in years)
        {
            var part = ReadAll(symbolDirectory, year, out int pp);
            if (pp > 0) pipPoints = pp;
            result.AddRange(part);
        }
        return result;
    }

    public static List<DepthSnapshot> ReadAll(string symbolDirectory, int year, out int pipPoints)
    {
        pipPoints = 0;
        var result = new List<DepthSnapshot>();
        var path = YearPath(symbolDirectory, year);
        if (!File.Exists(path)) return result;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!ReadHeader(fs, out int levels, out pipPoints)) return result;
        if (levels != Levels) return result;
        long count = (fs.Length - HeaderBytes) / RecordBytes;
        if (count <= 0) return result;
        fs.Position = HeaderBytes;
        var buf = new byte[RecordBytes];
        for (long i = 0; i < count; i++)
        {
            fs.ReadExactly(buf);
            var bid = new ushort[Levels];
            var ask = new ushort[Levels];
            for (int k = 0; k < Levels; k++)
            {
                bid[k] = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(12 + k * 2));
                ask[k] = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(12 + Levels * 2 + k * 2));
            }
            result.Add(new DepthSnapshot(
                BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0)),
                BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8)),
                bid, ask));
        }
        return result;
    }
}
