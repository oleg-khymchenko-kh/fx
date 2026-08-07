using System.IO;
using System.Text.Json;

namespace FXViewer.Storage;

public sealed record FindResult(
    long SourceUnix, double Sim, double Score, double Rho, double Zoom, bool Mirrored, int Overlap,
    double MeanA, double MeanB, string Symbol, double TimeFit = 0);

public sealed record FindData(
    int Version, string Source, long FragStartUnix, long FragEndUnix, long SearchedUnix,
    int WindowMinutes, List<FindResult> Results, List<string> Targets,
    int SmoothMinutes, int ZoomPercent, int StepMinutes,
    string SearchType = FindSearchTypes.Similarity, string TargetIndicator = "");

public static class FindSearchTypes
{
    public const string Similarity = "Similarity";
    public const string ZigZag = "ZigZag";

    public static bool IsZigZag(string type) =>
        string.Equals(type, ZigZag, StringComparison.OrdinalIgnoreCase);
}

public static class FindStore
{
    public const int CurrentVersion = 3;

    private const string FileName = "find.json";

    public static FindData? Load(string symbolDir)
    {
        var path = Path.Combine(symbolDir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<FindData>(File.ReadAllText(path));
            return data is { Version: CurrentVersion, Source.Length: > 0, Results.Count: > 0 } ? data : null;
        }
        catch
        {
            try { File.Move(path, path + ".bad", true); } catch { }
            return null;
        }
    }

    public static void Save(string symbolDir, FindData data)
    {
        Directory.CreateDirectory(symbolDir);
        var path = Path.Combine(symbolDir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data));
        File.Move(tmp, path, true);
    }
}
