using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;

namespace FXViewer.Calendar;

public static class CalendarDownloader
{
    private static readonly string[] WeeklyUrls =
    {
        "https://nfs.faireconomy.media/ff_calendar_thisweek.json",
    };

    public const string HistoryCsvUrl =
        "https://huggingface.co/datasets/Ehsanrs2/Forex_Factory_Calendar/resolve/main/forex_factory_cache.csv";

    private const string WeekPageUrl = "https://www.forexfactory.com/calendar?week=";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly TimeSpan WeekPagePause = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WeekPageFirstRetryPause = TimeSpan.FromSeconds(10);
    private const int WeekPageAttempts = 4;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FXViewer/1.0");
        return client;
    }

    private static HttpClient CreatePageClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(2),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public static string WeekKey(DateTime weekStart) =>
        weekStart.ToString("MMM", CultureInfo.InvariantCulture).ToLowerInvariant()
        + weekStart.Day.ToString(CultureInfo.InvariantCulture)
        + "." + weekStart.Year.ToString(CultureInfo.InvariantCulture);

    public static DateTime WeekStartOf(DateTime utc) =>
        utc.Date.AddDays(-(int)utc.Date.DayOfWeek);

    public static async Task DownloadWeekRangeAsync(
        DateTime firstWeekStart, DateTime lastWeekStart,
        Func<DateTime, IReadOnlyList<CalendarDetail>, Task> onWeek,
        Action<string> log, CancellationToken ct)
    {
        using var client = CreatePageClient();
        int total = (int)((lastWeekStart - firstWeekStart).TotalDays / 7) + 1;
        int done = 0;
        for (var week = firstWeekStart; week <= lastWeekStart; week = week.AddDays(7))
        {
            ct.ThrowIfCancellationRequested();
            var events = await DownloadWeekPageAsync(client, week, log, ct);
            done++;
            if (events == null)
            {
                log($"Calendar week {WeekKey(week)}: skipped");
            }
            else
            {
                await onWeek(week, events);
                log($"Calendar week {WeekKey(week)}: {events.Count} events ({done}/{total})");
            }
            if (week < lastWeekStart) await Task.Delay(WeekPagePause, ct);
        }
    }

    private static async Task<IReadOnlyList<CalendarDetail>?> DownloadWeekPageAsync(
        HttpClient client, DateTime weekStart, Action<string> log, CancellationToken ct)
    {
        var url = WeekPageUrl + WeekKey(weekStart);
        for (int attempt = 1; attempt <= WeekPageAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = await client.GetStringAsync(url, ct);
                if (ForexFactoryParser.LooksLikeWeekPage(html))
                    return ForexFactoryParser.ReadWeekPage(html);
                log($"Calendar week {WeekKey(weekStart)}: no calendar data in page (rate limited?), attempt {attempt}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"Calendar week {WeekKey(weekStart)} failed (attempt {attempt}): {ex.Message}");
            }
            if (attempt < WeekPageAttempts)
                await Task.Delay(WeekPageFirstRetryPause * (1 << (attempt - 1)), ct);
        }
        return null;
    }

    public static async Task<List<CalendarDetail>> DownloadWeeklyAsync(
        Action<string> log, CancellationToken ct)
    {
        using var client = CreateClient();
        var all = new List<CalendarDetail>();
        foreach (var url in WeeklyUrls)
        {
            try
            {
                var json = await client.GetStringAsync(url, ct);
                if (!json.AsSpan().TrimStart().StartsWith('['))
                {
                    log($"Calendar fetch for {WeekLabel(url)}: source returned a page, not JSON (rate limited?)");
                    continue;
                }
                var events = ForexFactoryParser.ReadWeeklyJson(json);
                all.AddRange(events);
                log($"Calendar fetch: {events.Count} events from {WeekLabel(url)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"Calendar fetch failed for {WeekLabel(url)}: {ex.Message}");
            }
        }
        return all;
    }

    public static async Task DownloadHistoryCsvAsync(
        string destPath, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var client = CreateClient();
        using var response = await client.GetAsync(
            HistoryCsvUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        var tmp = destPath + ".tmp";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1 << 16];
            long copied = 0;
            long nextLog = 8L << 20;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                if (copied >= nextLog)
                {
                    log(total.HasValue
                        ? $"Calendar CSV: {copied >> 20} / {total.Value >> 20} MB"
                        : $"Calendar CSV: {copied >> 20} MB");
                    nextLog += 8L << 20;
                }
            }
        }
        File.Move(tmp, destPath, true);
        log($"Calendar CSV downloaded ({new FileInfo(destPath).Length >> 20} MB)");
    }

    private static string WeekLabel(string url) =>
        url.Contains("lastweek") ? "last week" :
        url.Contains("nextweek") ? "next week" : "this week";
}
