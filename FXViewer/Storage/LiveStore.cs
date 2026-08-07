using System.IO;
using System.Text.Json;

namespace FXViewer.Storage;

public sealed record LiveData(int Version, long ConfirmedToUnix, long LastWrittenUnix, long UpdatedUnix);

public static class LiveStore
{
    public const int CurrentVersion = 1;

    private const string FileName = "live.json";

    public static LiveData? Load(string symbolDir)
    {
        var path = Path.Combine(symbolDir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<LiveData>(File.ReadAllText(path));
            return data is { Version: CurrentVersion, LastWrittenUnix: > 0 } ? data : null;
        }
        catch
        {
            try { File.Move(path, path + ".bad", true); } catch { }
            return null;
        }
    }

    public static void Save(string symbolDir, LiveData data)
    {
        Directory.CreateDirectory(symbolDir);
        var path = Path.Combine(symbolDir, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data));
        File.Move(tmp, path, true);
    }
}
