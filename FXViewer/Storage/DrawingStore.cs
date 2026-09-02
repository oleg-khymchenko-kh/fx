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
        double[][][]? raw;
        try
        {
            raw = JsonSerializer.Deserialize<double[][][]>(File.ReadAllText(path));
        }
        catch
        {
            try { File.Move(path, path + ".bad", true); } catch { }
            return Array.Empty<PivotPoint[]>();
        }
        return FromRaw(raw);
    }

    public const int ValueDecimals = 9;

    public static PivotPoint[][] FromRaw(double[][][]? raw)
    {
        if (raw == null) return Array.Empty<PivotPoint[]>();
        var lines = new List<PivotPoint[]>(raw.Length);
        foreach (var line in raw)
        {
            if (line == null) continue;
            bool level = false;
            foreach (var p in line)
                if (p is { Length: >= 3 } && p[2] != 0) level = true;
            var points = new List<PivotPoint>(line.Length);
            foreach (var p in line)
                if (p is { Length: >= 2 })
                {
                    long t = (long)p[0];
                    points.Add(new PivotPoint(t - t % 60, p[1], level));
                }
            if (points.Count > 0) lines.Add(points.ToArray());
        }
        return lines.ToArray();
    }

    public static double[][][] ToRaw(PivotPoint[][] lines)
    {
        var raw = new double[lines.Length][][];
        for (int i = 0; i < lines.Length; i++)
        {
            raw[i] = new double[lines[i].Length][];
            for (int j = 0; j < lines[i].Length; j++)
            {
                double value = Math.Round(lines[i][j].Value, ValueDecimals);
                raw[i][j] = lines[i][j].Level
                    ? new[] { (double)lines[i][j].UnixSeconds, value, 1.0 }
                    : new[] { (double)lines[i][j].UnixSeconds, value };
            }
        }
        return raw;
    }

    public static void Save(string symbolDir, PivotPoint[][] lines)
    {
        Directory.CreateDirectory(symbolDir);
        var raw = ToRaw(lines);
        var path = Path.Combine(symbolDir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw));
        File.Move(tmp, path, true);
    }
}
