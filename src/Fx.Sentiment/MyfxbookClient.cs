using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FXViewer.Storage;

namespace Fx.Sentiment;

public sealed record FetchResult(bool RateLimited, HttpStatusCode Status, string Json, List<SentimentPoint> Points, int SkippedRows);

public sealed class MyfxbookClient : IDisposable
{
    public const string Host = "https://www.myfxbook.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string RateLimitMarker = "Max requests reached";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    public static readonly IReadOnlyDictionary<string, int> SymbolOids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["EURUSD"] = 1,
        ["GBPUSD"] = 2,
        ["USDJPY"] = 3,
        ["USDCAD"] = 5,
        ["AUDUSD"] = 11,
        ["EURGBP"] = 17,
        ["NZDUSD"] = 28,
        ["USDCHF"] = 29,
        ["XAUUSD"] = 51,
    };

    private readonly string _curlPath;

    public MyfxbookClient()
    {
        _curlPath = FindCurl();
    }

    public static bool IsKnownSymbol(string symbol) => SymbolOids.ContainsKey(symbol);

    public async Task<FetchResult> FetchAsync(string symbol, string timeframe, CancellationToken cancellation)
    {
        if (!SymbolOids.TryGetValue(symbol, out int oid))
            throw new ArgumentException("No Myfxbook symbol id for " + symbol);
        int minutes = SentimentTimeframes.Minutes(timeframe);
        string url = $"{Host}/outlook-data.json?type=0&symbolOid={oid}&timeFrame={minutes}";
        string referer = $"{Host}/community/outlook/{symbol.ToUpperInvariant()}";
        var (status, body) = await PostAsync(url, referer, cancellation);
        bool limited = status == HttpStatusCode.TooManyRequests
            || body.Contains(RateLimitMarker, StringComparison.OrdinalIgnoreCase);
        if (limited) return new FetchResult(true, status, body, new List<SentimentPoint>(), 0);
        if ((int)status < 200 || (int)status >= 300)
            throw new HttpRequestException($"Myfxbook answered {(int)status} for {symbol} {timeframe}: {Trim(body)}");
        var (points, skipped) = Parse(body);
        return new FetchResult(false, status, body, points, skipped);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string url, string referer, CancellationToken cancellation)
    {
        string bodyFile = Path.GetTempFileName();
        try
        {
            var start = new ProcessStartInfo(_curlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[]
            {
                "-s", "-S",
                "--max-time", ((int)RequestTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                "-o", bodyFile,
                "-w", "%{http_code}",
                "-X", "POST",
                "-A", UserAgent,
                "-H", "Accept: application/json, text/javascript, */*; q=0.01",
                "-H", "Accept-Language: en-US,en;q=0.9",
                "-H", "X-Requested-With: XMLHttpRequest",
                "-H", "Origin: " + Host,
                "-H", "Referer: " + referer,
                "-H", "Content-Type: text/plain; charset=utf-8",
                "-d", "",
                url,
            })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + _curlPath);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            string statusText = (await stdout).Trim();
            string error = (await stderr).Trim();
            if (process.ExitCode != 0)
                throw new HttpRequestException($"curl exit {process.ExitCode}: {error}");
            if (!int.TryParse(statusText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int status))
                throw new HttpRequestException("curl gave no HTTP status: " + statusText + " " + error);
            string body = File.Exists(bodyFile) ? File.ReadAllText(bodyFile, Encoding.UTF8) : "";
            return ((HttpStatusCode)status, body);
        }
        finally
        {
            try { File.Delete(bodyFile); } catch { }
        }
    }

    private static string FindCurl()
    {
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (File.Exists(system)) return system;
        return "curl";
    }

    public static (List<SentimentPoint> Points, int Skipped) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("Myfxbook answer has no data array: " + Trim(json));
        var points = new List<SentimentPoint>();
        int skipped = 0;
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 9) { skipped++; continue; }
            var cells = row.EnumerateArray().Select(CellText).ToArray();
            if (!long.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long timeMs)
                || !TryNumber(cells[5], out double shortLots)
                || !TryNumber(cells[6], out double longLots)
                || !TryNumber(cells[7], out double shortPositions)
                || !TryNumber(cells[8], out double longPositions))
            {
                skipped++;
                continue;
            }
            points.Add(new SentimentPoint(timeMs / 1000, shortLots, longLots,
                (int)Math.Round(shortPositions), (int)Math.Round(longPositions)));
        }
        return (points, skipped);
    }

    private static string CellText(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => cell.GetString() ?? "",
        JsonValueKind.Number => cell.GetRawText(),
        _ => "",
    };

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Trim(string body) =>
        body.Length > 160 ? body[..160].ReplaceLineEndings(" ") + "..." : body.ReplaceLineEndings(" ");

    public void Dispose()
    {
    }
}
