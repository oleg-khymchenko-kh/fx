using System.IO;

namespace FXViewer.Game;

public sealed class GameLogEntry
{
    public string Day { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public long Seconds { get; set; }
    public long ActiveSeconds { get; set; }
    public string FromUtc { get; set; } = "";
    public string ToUtc { get; set; } = "";
    public List<string> Pairs { get; set; } = new();
    public double Pips { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public List<GameLogTrade> Trades { get; set; } = new();
    public List<GameLogAction> Actions { get; set; } = new();
}

public sealed class GameLogAction
{
    public string TimeUtc { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public double Price { get; set; }
    public double StopPips { get; set; }
    public double TakePips { get; set; }
    public int Id { get; set; }
    public int Target { get; set; }
}

public sealed class GameLogTrade
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public string OpenUtc { get; set; } = "";
    public double OpenPrice { get; set; }
    public string CloseUtc { get; set; } = "";
    public double ClosePrice { get; set; }
    public string Reason { get; set; } = "";
    public double Pips { get; set; }
}

public static class GameLogStore
{
    private const string DayFileName = "game-log.jsonl";
    private const string AfternoonFileName = "game-log-1400.jsonl";

    private static string FileName(string mode) =>
        GameModes.IsAfternoon(mode) ? AfternoonFileName : DayFileName;

    private static string FilePath(string mode) => Path.Combine(AppConfig.Dir, FileName(mode));

    public static List<GameLogEntry> Load(string mode) =>
        JsonLines.Load<GameLogEntry>(FilePath(mode), e => e.Day.Length > 0);

    public static void Append(string mode, GameLogEntry entry) => JsonLines.Append(FilePath(mode), entry);

    public static Dictionary<string, int> PlayCounts(IEnumerable<GameLogEntry> entries) =>
        entries.GroupBy(e => e.Day, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
