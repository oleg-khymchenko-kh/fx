using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FXViewer.Storage;

public sealed class CommentPoint
{
    public string Id { get; set; } = "";
    public string Pair { get; set; } = "";
    public long MinuteUnix { get; set; }
    public double Price { get; set; }

    public bool IsUsable() =>
        CommentStore.IsFileSafe(Id) && Pair.Length > 0 && MinuteUnix > 0 && double.IsFinite(Price);

    public string Day() =>
        DateTimeOffset.FromUnixTimeSeconds(MinuteUnix).UtcDateTime.ToString("yyyy-MM-dd");

    public CommentPoint Clone() => (CommentPoint)MemberwiseClone();
}

public sealed class ChartComment
{
    public string Id { get; set; } = "";
    public string Pair { get; set; } = "";
    public long MinuteUnix { get; set; }
    public double Price { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    public CommentPoint Point() => new()
    {
        Id = Id,
        Pair = Pair,
        MinuteUnix = MinuteUnix,
        Price = Price,
    };

    public static ChartComment Of(CommentPoint point) => new()
    {
        Id = point.Id,
        Pair = point.Pair,
        MinuteUnix = point.MinuteUnix,
        Price = point.Price,
    };
}

public sealed class CommentStore
{
    private const string IndexFileName = "index.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<CommentPoint> _points;
    private readonly Dictionary<string, ChartComment> _bodies = new(StringComparer.Ordinal);

    private CommentStore(List<CommentPoint> points) => _points = points;

    public static string Folder => Path.Combine(AppConfig.Dir, "comments");

    private static string IndexPath => Path.Combine(Folder, IndexFileName);

    private static string BodyPath(string id) => Path.Combine(Folder, id + ".json");

    public IReadOnlyList<CommentPoint> Points => _points;

    public static CommentStore Load()
    {
        var points = ReadIndex();
        if (points == null)
        {
            points = ScanBodies();
            if (points.Count > 0) WriteIndex(points);
        }
        points.Sort((a, b) => a.MinuteUnix.CompareTo(b.MinuteUnix));
        return new CommentStore(points);
    }

    public ChartComment Body(string id)
    {
        if (_bodies.TryGetValue(id, out var cached)) return cached;
        var point = _points.FirstOrDefault(p => p.Id == id);
        var body = ReadBody(BodyPath(id)) ?? (point == null ? null : ChartComment.Of(point));
        body ??= new ChartComment { Id = id };
        _bodies[id] = body;
        return body;
    }

    public void Save(ChartComment comment)
    {
        comment.MinuteUnix -= comment.MinuteUnix % 60;
        string oldId = comment.Id;
        string id = IdFor(comment, oldId);
        Directory.CreateDirectory(Folder);
        if (oldId.Length > 0 && oldId != id) Forget(oldId);
        comment.Id = id;
        WriteJson(BodyPath(id), comment);
        _bodies[id] = comment;
        var point = comment.Point();
        int at = _points.FindIndex(p => p.Id == id);
        if (at < 0) _points.Add(point);
        else _points[at] = point;
        _points.Sort((a, b) => a.MinuteUnix.CompareTo(b.MinuteUnix));
        WriteIndex(_points);
    }

    public static string IdOf(string pair, long minuteUnix) =>
        FileSafe(pair) + "-" + DateTimeOffset.FromUnixTimeSeconds(minuteUnix - minuteUnix % 60)
            .UtcDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

    private string IdFor(ChartComment comment, string currentId)
    {
        string wanted = IdOf(comment.Pair, comment.MinuteUnix);
        if (IsSamePoint(currentId, wanted)) return currentId;
        if (!Taken(wanted, currentId)) return wanted;
        for (int copy = 2; copy < 1000; copy++)
        {
            string candidate = wanted + "-" + copy.ToString(CultureInfo.InvariantCulture);
            if (!Taken(candidate, currentId)) return candidate;
        }
        return wanted + "-" + Guid.NewGuid().ToString("N");
    }

    private static bool IsSamePoint(string id, string wanted)
    {
        if (id.Length == 0) return false;
        if (id == wanted) return true;
        if (!id.StartsWith(wanted + "-", StringComparison.Ordinal)) return false;
        var copy = id.AsSpan(wanted.Length + 1);
        if (copy.Length == 0) return false;
        foreach (var c in copy)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    private bool Taken(string id, string currentId) =>
        id != currentId && (_points.Any(p => p.Id == id) || File.Exists(BodyPath(id)));

    private void Forget(string id)
    {
        _points.RemoveAll(p => p.Id == id);
        _bodies.Remove(id);
        try { File.Delete(BodyPath(id)); }
        catch { }
    }

    public void Delete(string id)
    {
        Forget(id);
        WriteIndex(_points);
    }

    public static bool IsFileSafe(string id)
    {
        if (id.Length == 0 || id.Length > 120 || id == "." || id == "..") return false;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.') return false;
        return true;
    }

    private static string FileSafe(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        return new string(chars);
    }

    private static List<CommentPoint>? ReadIndex()
    {
        if (!File.Exists(IndexPath)) return null;
        List<CommentPoint>? points;
        try
        {
            points = JsonSerializer.Deserialize<List<CommentPoint>>(File.ReadAllText(IndexPath), Options);
        }
        catch
        {
            try { File.Move(IndexPath, IndexPath + ".bad", true); } catch { }
            return null;
        }
        return points == null ? new List<CommentPoint>() : Usable(points);
    }

    private static List<CommentPoint> ScanBodies()
    {
        var points = new List<CommentPoint>();
        try
        {
            if (!Directory.Exists(Folder)) return points;
            foreach (var path in Directory.GetFiles(Folder, "*.json"))
            {
                if (string.Equals(Path.GetFileName(path), IndexFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var body = ReadBody(path);
                if (body != null) points.Add(body.Point());
            }
        }
        catch
        {
        }
        return Usable(points);
    }

    private static ChartComment? ReadBody(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var body = JsonSerializer.Deserialize<ChartComment>(File.ReadAllText(path), Options);
            if (body == null) return null;
            body.Id = Path.GetFileNameWithoutExtension(path);
            body.MinuteUnix -= body.MinuteUnix % 60;
            return body;
        }
        catch
        {
            return null;
        }
    }

    private static List<CommentPoint> Usable(List<CommentPoint> points)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<CommentPoint>(points.Count);
        foreach (var point in points)
        {
            point.MinuteUnix -= point.MinuteUnix % 60;
            if (!point.IsUsable() || !seen.Add(point.Id)) continue;
            list.Add(point);
        }
        return list;
    }

    private static void WriteIndex(IReadOnlyList<CommentPoint> points)
    {
        Directory.CreateDirectory(Folder);
        WriteJson(IndexPath, points);
    }

    private static void WriteJson(string path, object value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}
