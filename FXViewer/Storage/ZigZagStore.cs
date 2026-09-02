using System.IO;
using System.Text.Json;
using FXViewer.Compute;

namespace FXViewer.Storage;

public static class ZigZagStore
{
    private const string FileName = "zigzag.json";

    public static PivotPoint[] Load(string symbolDir)
    {
        var path = Path.Combine(symbolDir, FileName);
        if (!File.Exists(path)) return Array.Empty<PivotPoint>();
        long[][]? raw;
        try
        {
            raw = JsonSerializer.Deserialize<long[][]>(File.ReadAllText(path));
        }
        catch
        {
            try { File.Move(path, path + ".bad", true); } catch { }
            return Array.Empty<PivotPoint>();
        }
        if (raw == null) return Array.Empty<PivotPoint>();
        var points = new List<PivotPoint>(raw.Length);
        foreach (var p in raw)
            if (p is { Length: >= 2 })
                points.Add(new PivotPoint(p[0] - p[0] % 60, (int)p[1]));
        points.Sort((a, b) => a.UnixSeconds.CompareTo(b.UnixSeconds));
        return points.ToArray();
    }

    public static void Save(string symbolDir, IReadOnlyList<PivotPoint> points)
    {
        Directory.CreateDirectory(symbolDir);
        var raw = new long[points.Count][];
        for (int i = 0; i < points.Count; i++)
            raw[i] = new[] { points[i].UnixSeconds, (long)Math.Round(points[i].Value) };
        var path = Path.Combine(symbolDir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw));
        File.Move(tmp, path, true);
    }
}
