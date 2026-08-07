using System.Buffers.Binary;
using System.Globalization;

namespace Fx.MaCross;

internal sealed class History
{
    private const int HeaderSize = 64;
    private const int RecordSize = 16;
    private const uint FlagFilled = 1u << 0;

    public int[] Lo = [];
    public int[] Hi = [];
    public int[] Avg = [];
    public long[] Ts = [];
    public int Count;
    public int MinPrice;
    public int MaxPrice;

    public static History? Load(string dataRoot, string symbol, int fromYear, int toYear)
    {
        string dir = Path.Combine(dataRoot, symbol);
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Symbol directory not found: {dir}");
            return null;
        }

        var files = Directory.GetFiles(dir, "*.m1")
            .Select(p => (Path: p, Year: ParseYear(p)))
            .Where(t => t.Year >= fromYear && t.Year <= toYear)
            .OrderBy(t => t.Year)
            .ToList();

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No .m1 files in {dir} for years {fromYear}..{toYear}");
            return null;
        }

        long capacity = 0;
        foreach (var (path, _) in files) capacity += (new FileInfo(path).Length - HeaderSize) / RecordSize;

        var h = new History
        {
            Lo = new int[capacity],
            Hi = new int[capacity],
            Avg = new int[capacity],
            Ts = new long[capacity],
            MinPrice = int.MaxValue,
            MaxPrice = int.MinValue,
        };

        int n = 0;
        var buffer = Array.Empty<byte>();
        foreach (var (path, year) in files)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int length = (int)fs.Length;
            if (buffer.Length < length) buffer = new byte[length];
            fs.ReadExactly(buffer, 0, length);

            long yearStartUnix = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            int records = (length - HeaderSize) / RecordSize;
            int filled = 0;
            for (int i = 0; i < records; i++)
            {
                int off = HeaderSize + i * RecordSize;
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(off + 12));
                if ((flags & FlagFilled) == 0) continue;
                int lo = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(off + 0));
                int hi = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(off + 4));
                int avg = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(off + 8));
                h.Lo[n] = lo;
                h.Hi[n] = hi;
                h.Avg[n] = avg;
                h.Ts[n] = yearStartUnix + (long)i * 60;
                if (lo < h.MinPrice) h.MinPrice = lo;
                if (hi > h.MaxPrice) h.MaxPrice = hi;
                n++;
                filled++;
            }

            Console.WriteLine($"  {year}: {filled,9:N0} minutes");
        }

        h.Count = n;
        return n == 0 ? null : h;
    }

    public DateTime TimeUtc(int index) => DateTimeOffset.FromUnixTimeSeconds(Ts[index]).UtcDateTime;

    private static int ParseYear(string path) =>
        int.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);
}
