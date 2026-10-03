using System.IO;
using System.Text.Json;
using FXViewer.Compute;
using FXViewer.Storage;

namespace Fx.WeekPuzzle;

internal static class DrawingFile
{
    private sealed record NoteRaw(string? Name, Dictionary<string, double[][][]>? Drawings);

    public static PivotPoint[][] Read(string dataRoot, string indicator)
    {
        string key = Key(indicator);
        string path = Path.Combine(dataRoot, key, "drawing.json");
        string notes = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataRoot))!, "notes.json");
        var lines = new List<PivotPoint[]>();
        bool found = File.Exists(path);
        if (found) lines.AddRange(DrawingStore.FromRaw(Parse<double[][][]>(path)));
        if (File.Exists(notes))
            foreach (var note in Parse<List<NoteRaw>>(notes) ?? new List<NoteRaw>())
                foreach (var (name, raw) in note.Drawings ?? new Dictionary<string, double[][][]>())
                    if (Key(name) == key)
                    {
                        found = true;
                        lines.AddRange(DrawingStore.FromRaw(raw));
                    }
        if (!found) throw new FileNotFoundException($"no drawing for {indicator} in {dataRoot} or {notes}", path);
        return Distinct(lines).ToArray();
    }

    private static T? Parse<T>(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return JsonSerializer.Deserialize<T>(fs);
    }

    private static string Key(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static List<PivotPoint[]> Distinct(List<PivotPoint[]> lines)
    {
        var kept = new List<PivotPoint[]>();
        foreach (var line in lines)
            if (!kept.Any(k => k.AsSpan().SequenceEqual(line))) kept.Add(line);
        return kept;
    }
}
