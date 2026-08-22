using System.IO;
using System.Text;

namespace FXViewer;

public static class CrashLog
{
    private static readonly string CrashFile = Path.Combine(AppContext.BaseDirectory, "fxviewer-crash.log");
    private static readonly string MainFile = Path.Combine(AppContext.BaseDirectory, "fxviewer.log");
    private static readonly object Gate = new();

    public static event Action<string>? Reported;

    public static void Write(string source, Exception? ex)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string details = ex?.ToString() ?? "unknown error";
        var text = new StringBuilder()
            .Append(stamp).Append("  CRASH [").Append(source).Append("] ")
            .AppendLine(details)
            .ToString();
        lock (Gate)
        {
            try { File.AppendAllText(CrashFile, text); } catch { }
            try { File.AppendAllText(MainFile, text); } catch { }
        }
        string headline = "CRASH [" + source + "] " + FirstLine(details);
        try { Reported?.Invoke(headline); } catch { }
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOf('\n');
        return nl < 0 ? text : text[..nl].TrimEnd('\r');
    }
}
