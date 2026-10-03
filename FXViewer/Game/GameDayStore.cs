using System.IO;
using System.Text.Json;
using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer.Game;

public static class GameDrawingLayers
{
    public const string Draft = "draft";
    public const string Day = "day";
}

public sealed class GameDayRecord
{
    public string Comment { get; set; } = "";
    public Dictionary<string, double[][][]> Drawing { get; set; } = new();
    public Dictionary<string, double[][][]> Draft { get; set; } = new();

    public bool IsEmpty() => Comment.Length == 0 && Drawing.Count == 0 && Draft.Count == 0;

    public Dictionary<string, double[][][]> Layer(string layer) =>
        layer == GameDrawingLayers.Day ? Drawing : Draft;
}

public sealed class GameDayStore
{
    private const string DayFileName = "game-days.json";
    private const string AfternoonFileName = "game-days-1400.json";

    private readonly Dictionary<string, GameDayRecord> _days;
    private readonly Dictionary<(string Day, string Layer, string Pair), PivotPoint[][]> _lines = new();
    private readonly string _path;

    private GameDayStore(string path, Dictionary<string, GameDayRecord> days)
    {
        _path = path;
        _days = days;
    }

    private static string PathOf(string mode) =>
        Path.Combine(AppConfig.Dir, GameModes.IsAfternoon(mode) ? AfternoonFileName : DayFileName);

    public static GameDayStore Load(string mode)
    {
        string path = PathOf(mode);
        var empty = new Dictionary<string, GameDayRecord>(StringComparer.Ordinal);
        if (!File.Exists(path)) return new GameDayStore(path, empty);
        try
        {
            var days = JsonSerializer.Deserialize<Dictionary<string, GameDayRecord>>(File.ReadAllText(path));
            return new GameDayStore(path, days == null
                ? empty
                : new Dictionary<string, GameDayRecord>(days, StringComparer.Ordinal));
        }
        catch
        {
            try { File.Move(path, path + ".bad", true); } catch { }
            return new GameDayStore(path, empty);
        }
    }

    public string Comment(string day) => _days.TryGetValue(day, out var record) ? record.Comment : "";

    public void SetComment(string day, string text) => Record(day).Comment = text;

    public PivotPoint[][] Lines(string day, string layer, string pair)
    {
        var key = (day, layer, pair);
        if (_lines.TryGetValue(key, out var lines)) return lines;
        lines = _days.TryGetValue(day, out var record) && record.Layer(layer).TryGetValue(pair, out var raw)
            ? DrawingStore.FromRaw(raw)
            : Array.Empty<PivotPoint[]>();
        _lines[key] = lines;
        return lines;
    }

    public void SetLines(string day, string layer, string pair, PivotPoint[][] lines)
    {
        var map = Record(day).Layer(layer);
        if (lines.Length == 0) map.Remove(pair);
        else map[pair] = DrawingStore.ToRaw(lines);
        _lines[(day, layer, pair)] = lines;
    }

    public void ClearDraft(string day)
    {
        if (_days.TryGetValue(day, out var record)) record.Draft.Clear();
        foreach (var key in _lines.Keys.Where(k => k.Day == day && k.Layer == GameDrawingLayers.Draft).ToList())
            _lines.Remove(key);
    }

    public void Save()
    {
        var days = _days
            .Where(x => !x.Value.IsEmpty())
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Value);
        Directory.CreateDirectory(AppConfig.Dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(days, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, true);
    }

    private GameDayRecord Record(string day)
    {
        if (!_days.TryGetValue(day, out var record))
        {
            record = new GameDayRecord();
            _days[day] = record;
        }
        return record;
    }
}
