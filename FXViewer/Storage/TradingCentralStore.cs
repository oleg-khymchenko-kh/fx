using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FXViewer.Storage;

public static class TradingCentralSessions
{
    public const string Morning = "Morning";
    public const string Midday = "Midday";
    public const string Signal = "Signal";

    public static bool IsMidday(string session) =>
        string.Equals(session, Midday, StringComparison.OrdinalIgnoreCase);

    public static bool IsSignal(string session) =>
        string.Equals(session, Signal, StringComparison.OrdinalIgnoreCase);
}

public sealed class TradingCentralLevels
{
    public string Pair { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public double Pivot { get; set; }
    public List<double> Targets { get; set; } = new();
    public List<double> Alternatives { get; set; } = new();
    public string Comment { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Images { get; set; } = new();
    public string View { get; set; } = "";
    public long PublishedAtUnix { get; set; }

    public bool IsUsable() => Pair.Length > 0 && Pivot > 0;

    public bool IsUp() =>
        Targets.Count > 0 ? Targets[0] > Pivot
        : Alternatives.Count > 0 && Alternatives[0] < Pivot;
}

public sealed class TradingCentralSet
{
    public long MadeAtUnix { get; set; }
    public string Subject { get; set; } = "";
    public string MessageId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Manual { get; set; }

    public List<TradingCentralLevels> Pairs { get; set; } = new();
}

public sealed class TradingCentralDay
{
    public int Version { get; set; } = 1;
    public string Day { get; set; } = "";
    public TradingCentralSet? Morning { get; set; }
    public TradingCentralSet? Midday { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TradingCentralSet>? Signals { get; set; }

    public TradingCentralSet? SetOf(string session) =>
        TradingCentralSessions.IsMidday(session) ? Midday : Morning;

    public void Put(string session, TradingCentralSet set)
    {
        if (TradingCentralSessions.IsMidday(session)) Midday = set;
        else Morning = set;
    }

    public IEnumerable<(string Session, TradingCentralSet Set)> Sets()
    {
        if (Morning != null) yield return (TradingCentralSessions.Morning, Morning);
        if (Midday != null) yield return (TradingCentralSessions.Midday, Midday);
        if (Signals == null) yield break;
        foreach (var signal in Signals) yield return (TradingCentralSessions.Signal, signal);
    }
}

public static class TradingCentralTimes
{
    public const long PublishedAheadSeconds = 600;

    public static long StartOf(TradingCentralSet set, TradingCentralLevels levels) =>
        levels.PublishedAtUnix > 0 && levels.PublishedAtUnix <= set.MadeAtUnix + PublishedAheadSeconds
            ? levels.PublishedAtUnix
            : set.MadeAtUnix;
}

public static class TradingCentralStore
{
    public const string DefaultFolder = "trading-central";
    public const string FilePattern = "*.json";
    public const string DayFormat = "yyyy-MM-dd";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string PathOf(string folder, string day) => Path.Combine(folder, day + ".json");

    public static string Serialize(TradingCentralDay day) => JsonSerializer.Serialize(day, Options);

    public static List<TradingCentralDay> LoadFolder(string folder)
    {
        var days = new List<TradingCentralDay>();
        try
        {
            if (!Directory.Exists(folder)) return days;
            var files = Directory.GetFiles(folder, FilePattern);
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var day = LoadFile(file);
                if (day == null) continue;
                if (day.Day.Length == 0) day.Day = Path.GetFileNameWithoutExtension(file);
                days.Add(day);
            }
        }
        catch
        {
        }
        return days;
    }

    public static TradingCentralDay? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TradingCentralDay>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string path, TradingCentralDay day)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, Serialize(day));
        File.Move(tmp, full, true);
    }
}
