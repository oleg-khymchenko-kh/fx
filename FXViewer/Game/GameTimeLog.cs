using System.IO;

namespace FXViewer.Game;

public sealed class GameTimeSession
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public long Seconds { get; set; }
    public long ActiveSeconds { get; set; }
    public int Games { get; set; }
    public int Pauses { get; set; }
    public List<string> Days { get; set; } = new();
}

public static class GameTimeStore
{
    private const string FileName = "game-time.jsonl";

    private static string FilePath => Path.Combine(AppConfig.Dir, FileName);

    public static List<GameTimeSession> Load() =>
        JsonLines.Load<GameTimeSession>(FilePath, s => s.StartedAt != default);

    public static void Append(GameTimeSession session) => JsonLines.Append(FilePath, session);
}
