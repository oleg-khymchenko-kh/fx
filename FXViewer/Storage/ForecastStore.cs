using System.IO;
using System.Text.Json;

namespace FXViewer.Storage;

public static class ForecastKinds
{
    public const string Point = "Point";
    public const string Range = "Range";

    public static bool IsRange(string kind) =>
        string.Equals(kind, Range, StringComparison.OrdinalIgnoreCase);
}

public static class ForecastAuthors
{
    public const string Own = "Claude";

    public static bool IsOwn(string author) =>
        string.Equals(author, Own, StringComparison.OrdinalIgnoreCase);
}

public static class ForecastHorizons
{
    public const string Day = "Day";
    public const string Week = "Week";
}

public sealed class ForecastRecord
{
    public string Id { get; set; } = "";
    public string Pair { get; set; } = "";
    public string Kind { get; set; } = ForecastKinds.Point;
    public double Price { get; set; }
    public double Low { get; set; }
    public double High { get; set; }
    public long MadeAtUnix { get; set; }
    public long UntilUnix { get; set; }
    public string Horizon { get; set; } = "";
    public string Author { get; set; } = "";
    public string Title { get; set; } = "";
    public string Basis { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public double Probability { get; set; }
    public string Note { get; set; } = "";
    public string Outcome { get; set; } = "";

    public bool IsOwn => ForecastAuthors.IsOwn(Author);
    public bool IsRange => ForecastKinds.IsRange(Kind);
    public double TopPrice => IsRange ? Math.Max(Low, High) : Price;
    public double BottomPrice => IsRange ? Math.Min(Low, High) : Price;

    public bool IsUsable() =>
        Pair.Length > 0 && MadeAtUnix > 0 && UntilUnix > MadeAtUnix
        && (IsRange ? Low > 0 && High > 0 : Price > 0);
}

public sealed class ForecastFileModel
{
    public int Version { get; set; } = 1;
    public long UpdatedAtUnix { get; set; }
    public List<ForecastRecord> Forecasts { get; set; } = new();
}

public static class ForecastStore
{
    public const string DefaultFolder = "forecast";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static SortedDictionary<string, List<ForecastRecord>> LoadFolderByDay(string folder)
    {
        var days = new SortedDictionary<string, List<ForecastRecord>>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(folder)) return days;
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                var records = LoadFile(file);
                if (records.Count > 0) days[Path.GetFileNameWithoutExtension(file)] = records;
            }
        }
        catch
        {
        }
        return days;
    }

    public static List<ForecastRecord> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<ForecastRecord>();
            var model = JsonSerializer.Deserialize<ForecastFileModel>(File.ReadAllText(path), Options);
            return model?.Forecasts ?? new List<ForecastRecord>();
        }
        catch
        {
            return new List<ForecastRecord>();
        }
    }

    public static void Save(string path, ForecastFileModel model)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, Options));
        File.Move(tmp, full, true);
    }
}
