using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FXViewer.Storage;

public readonly record struct SentimentPoint(
    long TimeUnix,
    double ShortLots,
    double LongLots,
    int ShortPositions,
    int LongPositions)
{
    public double LongShareByLots =>
        ShortLots + LongLots > 0 ? LongLots / (ShortLots + LongLots) : double.NaN;

    public double LongShareByPositions =>
        ShortPositions + LongPositions > 0
            ? (double)LongPositions / (ShortPositions + LongPositions)
            : double.NaN;
}

public static class SentimentTimeframes
{
    public static readonly IReadOnlyList<string> All =
        new[] { "m1", "m5", "m15", "m30", "h1", "h4", "d1", "w1", "mn1" };

    public static readonly IReadOnlyList<string> BackfillOrder =
        new[] { "d1", "w1", "mn1", "h4", "h1", "m30", "m15", "m5" };

    public static bool IsKnown(string timeframe) => All.Contains(Normalize(timeframe));

    public static string Normalize(string timeframe) => timeframe.Trim().ToLowerInvariant();

    public static int Minutes(string timeframe) => Normalize(timeframe) switch
    {
        "m1" => 1,
        "m5" => 5,
        "m15" => 15,
        "m30" => 30,
        "h1" => 60,
        "h4" => 240,
        "d1" => 1440,
        "w1" => 10080,
        "mn1" => 43200,
        _ => throw new ArgumentException("Unknown sentiment timeframe: " + timeframe),
    };
}

public static class SentimentStore
{
    public const string Source = "myfxbook";
    private const string Header = "time,short_lots,long_lots,short_positions,long_positions";

    public static string Directory(string symbolDirectory) =>
        Path.Combine(symbolDirectory, "sentiment", Source);

    public static string RawDirectory(string symbolDirectory) =>
        Path.Combine(Directory(symbolDirectory), "raw");

    public static string SeriesPath(string symbolDirectory, string timeframe) =>
        Path.Combine(Directory(symbolDirectory), SentimentTimeframes.Normalize(timeframe) + ".csv");

    public static string RawPath(string symbolDirectory, string timeframe, long fetchedUnix) =>
        Path.Combine(RawDirectory(symbolDirectory),
            SentimentTimeframes.Normalize(timeframe) + "-"
            + fetchedUnix.ToString(CultureInfo.InvariantCulture) + ".json.gz");

    public static List<SentimentPoint> Load(string path)
    {
        var points = new SortedDictionary<long, SentimentPoint>();
        if (!File.Exists(path)) return new List<SentimentPoint>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryParseLine(line, out var point)) points[point.TimeUnix] = point;
        }
        return points.Values.ToList();
    }

    public static int Merge(string path, IEnumerable<SentimentPoint> incoming)
    {
        var merged = new SortedDictionary<long, SentimentPoint>();
        foreach (var point in Load(path)) merged[point.TimeUnix] = point;
        int changed = 0;
        foreach (var point in incoming)
        {
            if (merged.TryGetValue(point.TimeUnix, out var existing) && existing == point) continue;
            merged[point.TimeUnix] = point;
            changed++;
        }
        if (changed == 0 && File.Exists(path)) return 0;
        Save(path, merged.Values);
        return changed;
    }

    public static void Save(string path, IEnumerable<SentimentPoint> points)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        System.IO.Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(Header);
            foreach (var point in points) writer.WriteLine(FormatLine(point));
        }
        File.Move(temp, path, true);
    }

    public static void SaveRaw(string path, string json)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            writer.Write(json);
        }
        File.Move(temp, path, true);
    }

    public static string FormatLine(SentimentPoint point) => string.Join(",",
        point.TimeUnix.ToString(CultureInfo.InvariantCulture),
        point.ShortLots.ToString("0.##", CultureInfo.InvariantCulture),
        point.LongLots.ToString("0.##", CultureInfo.InvariantCulture),
        point.ShortPositions.ToString(CultureInfo.InvariantCulture),
        point.LongPositions.ToString(CultureInfo.InvariantCulture));

    public static bool TryParseLine(string line, out SentimentPoint point)
    {
        point = default;
        var parts = line.Split(',');
        if (parts.Length < 5) return false;
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long time)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double shortLots)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double longLots)) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shortPositions)) return false;
        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int longPositions)) return false;
        point = new SentimentPoint(time, shortLots, longLots, shortPositions, longPositions);
        return true;
    }
}
