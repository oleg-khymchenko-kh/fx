using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fx.WeekPuzzle;

internal sealed class DayStore
{
    public const string FileName = "day-puzzle.json";
    public const string DaysTab = "days";
    public const string WeeksTab = "weeks";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed class Data
    {
        public string Tab { get; set; } = DaysTab;
        public string? Start { get; set; }
        public int Days { get; set; }
        public List<DayGroupState> Groups { get; set; } = new();
        public Dictionary<string, double[]> Positions { get; set; } = new();
        public List<string> Mirrored { get; set; } = new();
        public bool WeekDayFill { get; set; } = true;
        public string? WeekDayFillGroup { get; set; }
    }

    private DayStore(string path, Data data)
    {
        Path = path;
        Current = data;
    }

    public string Path { get; }

    public Data Current { get; }

    public static DayStore Load(string path)
    {
        if (!File.Exists(path)) return new DayStore(path, new Data());
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var data = JsonSerializer.Deserialize<Data>(fs, Json) ?? new Data();
            data.Groups ??= new List<DayGroupState>();
            data.Positions ??= new Dictionary<string, double[]>();
            data.Mirrored ??= new List<string>();
            return new DayStore(path, data);
        }
        catch (JsonException)
        {
            File.Copy(path, $"{path}.bad-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}");
            return new DayStore(path, new Data());
        }
    }

    public void Save()
    {
        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
        File.Move(temp, Path, true);
    }
}
