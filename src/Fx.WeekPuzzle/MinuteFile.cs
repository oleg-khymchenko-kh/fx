using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using FXViewer.Storage;

namespace Fx.WeekPuzzle;

internal static class MinuteFile
{
    private const int HeaderSize = 64;
    private const int RecordSize = 16;
    private const uint FlagFilled = 1u << 0;
    private const uint FlagWideSpread = 1u << 5;

    public static List<Candle> Read(string dataRoot, string symbol, long fromUnix)
    {
        string dir = Path.Combine(dataRoot, symbol);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"no data folder {dir}");
        var result = new List<Candle>();
        var files = Directory.GetFiles(dir, "*.m1")
            .Select(p => (Path: p, Year: int.Parse(Path.GetFileNameWithoutExtension(p), CultureInfo.InvariantCulture)))
            .OrderBy(f => f.Year);
        foreach (var (path, year) in files)
        {
            long yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            long yearEnd = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            if (yearEnd <= fromUnix) continue;
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }
            int records = (bytes.Length - HeaderSize) / RecordSize;
            int first = (int)Math.Max(0, (fromUnix - yearStart) / 60);
            for (int i = first; i < records; i++)
            {
                var record = bytes.AsSpan(HeaderSize + i * RecordSize, RecordSize);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(12));
                if ((flags & FlagFilled) == 0 || (flags & FlagWideSpread) != 0) continue;
                result.Add(new Candle(yearStart + (long)i * 60,
                    BinaryPrimitives.ReadInt32LittleEndian(record),
                    BinaryPrimitives.ReadInt32LittleEndian(record.Slice(4)),
                    BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8)), false));
            }
        }
        return result;
    }
}
