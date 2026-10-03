using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Fx.TradingCentral;

public static class ChartImages
{
    public const string Folder = "images";
    public const string ChartAlt = "Analyst Views Chart";

    private const string AllowedDomain = "tradingcentral.com";
    private const int StampLength = 14;
    private const string StampFormat = "yyyyMMddHHmmss";
    private const int MaxAgeDays = 3;
    private const int MaxAheadMinutes = 10;

    private static readonly Regex ChartName = new(
        @"/charts/(?<id>\d+)_(?<stamp>\d{14,})(?:_\d+x\d+)?\.[a-z]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly TimeZoneInfo? Paris = Zone("Europe/Paris") ?? Zone("Romance Standard Time");

    public static string ViewOf(string url)
    {
        var match = ChartName.Match(url.Trim());
        return match.Success ? $"{match.Groups["id"].Value}_{match.Groups["stamp"].Value[..StampLength]}" : "";
    }

    public static long PublishedOf(string url, long deliveredUnix)
    {
        var match = ChartName.Match(url.Trim());
        if (!match.Success) return 0;
        string stamp = match.Groups["stamp"].Value;
        if (!DateTime.TryParseExact(stamp[..StampLength], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var clock))
            return 0;
        DateTime utc;
        if (stamp.Length > StampLength) utc = DateTime.SpecifyKind(clock, DateTimeKind.Utc);
        else if (Paris != null && !Paris.IsInvalidTime(clock)) utc = TimeZoneInfo.ConvertTimeToUtc(clock, Paris);
        else return 0;
        long published = new DateTimeOffset(utc).ToUnixTimeSeconds();
        bool fits = published <= deliveredUnix + MaxAheadMinutes * 60L
            && published >= deliveredUnix - MaxAgeDays * 86400L;
        return fits ? published : 0;
    }

    private static TimeZoneInfo? Zone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    private static readonly Regex ImageTag = new(@"<img\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Attribute = new(
        @"\b(?<name>[a-z-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<string> ChartUrls(string html)
    {
        var urls = new List<string>();
        foreach (Match tag in ImageTag.Matches(html))
        {
            string alt = "";
            string src = "";
            foreach (Match attribute in Attribute.Matches(tag.Value))
            {
                string name = attribute.Groups["name"].Value;
                string value = WebUtility.HtmlDecode(attribute.Groups["value"].Value).Trim();
                if (name.Equals("alt", StringComparison.OrdinalIgnoreCase)) alt = value;
                else if (name.Equals("src", StringComparison.OrdinalIgnoreCase)) src = value;
            }
            if (alt.Equals(ChartAlt, StringComparison.OrdinalIgnoreCase)) urls.Add(src);
        }
        return urls;
    }

    public static Uri? AllowedUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        string host = uri.Host.ToLowerInvariant();
        bool allowed = host == AllowedDomain || host.EndsWith("." + AllowedDomain, StringComparison.Ordinal);
        return allowed ? uri : null;
    }
}

public sealed class ImageDownloader : IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int ChunkBytes = 81920;

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/gif"] = ".gif",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/bmp"] = ".bmp",
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(1) };

    public void Dispose() => _http.Dispose();

    public async Task<(string? File, string Error)> DownloadAsync(
        Uri uri, string folder, string baseName, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!Extensions.TryGetValue(type, out var extension))
                return (null, $"not a supported image type: '{type}'");
            if (response.Content.Headers.ContentLength > MaxBytes) return (null, "the file is too big");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[ChunkBytes];
            int read;
            while ((read = await input.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBytes) return (null, "the file is too big");
                buffer.Write(chunk, 0, read);
            }
            Directory.CreateDirectory(folder);
            string name = baseName + extension;
            string path = Path.Combine(folder, name);
            string tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, buffer.ToArray(), ct);
            File.Move(tmp, path, true);
            foreach (var other in Extensions.Values)
                if (other != extension) File.Delete(Path.Combine(folder, baseName + other));
            return (name, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, ex.Message);
        }
    }
}
