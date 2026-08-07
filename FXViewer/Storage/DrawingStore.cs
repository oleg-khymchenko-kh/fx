using System.IO;
using System.Text.Json;
using FXViewer.Compute;

namespace FXViewer.Storage;

public static class DrawingStore
{
    private const string FileName = "drawing.json";

    public static PivotPoint[][] Load(string symbolDir)
    {
        var path = Path.Combine(symbolDir, FileName);
        if (!File.Exists(path)) return Array.Empty<PivotPoint[]>();
        long[][][]? raw;
        try
        {
            raw = JsonSerializer.Deserialize<long[][][]>(File.ReadAllText(path));
        }
        catch
        {
            try { File.Move(path, path + ".bad", true); } catch { }
            return Array.Empty<PivotPoint[]>();
        }
        if (raw == null) return Array.Empty<PivotPoint[]>();
        var lines = new List<PivotPoint[]>(raw.Length);
        foreach (var line in raw)
        {
            if (line == null) continue;
            var points = new List<PivotPoint>(line.Length);
            foreach (var p in line)
                if (p is { Length: >= 2 })
                    points.Add(new PivotPoint(p[0] - p[0] % 60, (int)p[1]));
            if (points.Count > 0) lines.Add(points.ToArray());
        }
        return lines.ToArray();
    }

    public static void Save(string symbolDir, PivotPoint[][] lines)
    {
        Directory.CreateDirectory(symbolDir);
        var raw = new long[lines.Length][][];
        for (int i = 0; i < lines.Length; i++)
        {
            raw[i] = new long[lines[i].Length][];
            for (int j = 0; j < lines[i].Length; j++)
                raw[i][j] = new[] { lines[i][j].UnixSeconds, lines[i][j].Value };
        }
        var path = Path.Combine(symbolDir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw));
        File.Move(tmp, path, true);
    }
}
