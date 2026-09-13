using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FXViewer.Game;

public static class JsonLines
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static List<T> Load<T>(string path, Func<T, bool> keep)
    {
        var items = new List<T>();
        if (!File.Exists(path)) return items;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return items;
        }
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, Options);
                if (item != null && keep(item)) items.Add(item);
            }
            catch (JsonException)
            {
            }
        }
        return items;
    }

    public static void Append<T>(string path, T item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(item, Options) + "\n";
        if (EndsWithoutNewline(path)) line = "\n" + line;
        File.AppendAllText(path, line, new UTF8Encoding(false));
    }

    private static bool EndsWithoutNewline(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }
}
