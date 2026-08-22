using FXViewer.Storage;

namespace FXViewer;

public static class HistoryDownloader
{
    private static readonly DateTimeOffset HistoryStartUtc = new(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(250);
    private const int WindowDays = 7;
    private const int ProbeChunkDays = 1800;

    public static async Task<(int Total, long EarliestUnix)> DownloadTailToDbAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName,
        CandleDatabase db, Action<string> log, CancellationToken ct, int priceDiv = 1)
    {
        var toUtc = DateTimeOffset.UtcNow;
        var lastFilled = db.LastFilledMinuteUtc(symbolName);
        if (lastFilled == null)
        {
            log($"{symbolName}: DB empty, use 'Download history 2010+' for full backfill");
            return (0, 0);
        }
        var from = new DateTimeOffset(lastFilled.Value, TimeSpan.Zero).AddMinutes(1);
        if (from >= toUtc)
        {
            log($"{symbolName}: already up to date");
            return (0, 0);
        }
        var currentMinute = (uint)(toUtc.ToUnixTimeSeconds() / 60);
        log($"{symbolName}: fetching tail {from:yyyy-MM-dd HH:mm}..{toUtc:yyyy-MM-dd HH:mm} UTC");
        var result = await DownloadRangeAsync(client, accountId, symbolId, symbolName, db,
            from, toUtc, currentMinute, 0, priceDiv, log, ct);
        db.FlushAll();
        return result;
    }

    public static async Task<(int Total, long EarliestUnix)> DownloadRangeToDbAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName,
        CandleDatabase db, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        Action<string> log, CancellationToken ct, int priceDiv = 1)
    {
        if (fromUtc >= toUtc) return (0, 0);
        var currentMinute = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60);
        var result = await DownloadRangeAsync(client, accountId, symbolId, symbolName, db,
            fromUtc, toUtc, currentMinute, 0, priceDiv, log, ct);
        db.FlushSymbol(symbolName);
        return result;
    }

    public static async Task<(int Total, long EarliestUnix)> DownloadM1ToDbAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName,
        CandleDatabase db, Action<string> log, CancellationToken ct, int priceDiv = 1)
    {
        var toUtc = DateTimeOffset.UtcNow;
        var ranges = FindMissingRanges(db, symbolName, toUtc);
        if (ranges.Count == 0)
        {
            log($"{symbolName}: already up to date");
            return (0, 0);
        }
        var dataStart = await FindFirstDataAsync(client, accountId, symbolId, ranges[0].From, toUtc, ct);
        if (dataStart == null)
        {
            log($"{symbolName}: server has no history");
            return (0, 0);
        }
        ranges = ranges
            .Select(r => (From: r.From < dataStart.Value ? dataStart.Value : r.From, r.To))
            .Where(r => r.From < r.To)
            .ToList();
        if (ranges.Count == 0)
        {
            log($"{symbolName}: already up to date");
            return (0, 0);
        }
        var currentMinute = (uint)(toUtc.ToUnixTimeSeconds() / 60);
        var total = 0;
        long earliest = 0;
        foreach (var (from, to) in ranges)
        {
            log($"{symbolName}: downloading M1 {from:yyyy-MM-dd HH:mm}..{to:yyyy-MM-dd HH:mm} UTC");
            var part = await DownloadRangeAsync(client, accountId, symbolId, symbolName, db,
                from, to, currentMinute, total, priceDiv, log, ct);
            total += part.Total;
            if (part.EarliestUnix > 0 && (earliest == 0 || part.EarliestUnix < earliest))
                earliest = part.EarliestUnix;
        }
        db.FlushAll();
        return (total, earliest);
    }

    private static List<(DateTimeOffset From, DateTimeOffset To)> FindMissingRanges(
        CandleDatabase db, string symbolName, DateTimeOffset toUtc)
    {
        var ranges = new List<(DateTimeOffset From, DateTimeOffset To)>();
        void Add(DateTimeOffset from, DateTimeOffset to)
        {
            if (from >= to) return;
            if (ranges.Count > 0 && ranges[^1].To == from)
                ranges[^1] = (ranges[^1].From, to);
            else
                ranges.Add((from, to));
        }
        for (int year = HistoryStartUtc.Year; year <= toUtc.Year; year++)
        {
            var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var yearEnd = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var upper = yearEnd < toUtc ? yearEnd : toUtc;
            var filled = db.FilledRangeUtc(symbolName, year);
            if (filled == null)
            {
                Add(yearStart, upper);
                continue;
            }
            Add(yearStart, new DateTimeOffset(filled.Value.FirstUtc));
            Add(new DateTimeOffset(filled.Value.LastUtc).AddMinutes(1), upper);
        }
        return ranges;
    }

    private static async Task<(int Total, long EarliestUnix)> DownloadRangeAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName, CandleDatabase db,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, uint currentMinute, int totalSoFar,
        int priceDiv, Action<string> log, CancellationToken ct)
    {
        var total = 0;
        long earliest = 0;
        uint lastMinute = 0;
        bool any = false;
        var windowStart = fromUtc;
        while (windowStart < toUtc)
        {
            ct.ThrowIfCancellationRequested();
            var windowEnd = windowStart.AddDays(WindowDays);
            if (windowEnd > toUtc) windowEnd = toUtc;
            var res = await client.GetTrendbarsAsync(accountId, symbolId, ProtoOATrendbarPeriod.M1,
                windowStart.ToUnixTimeMilliseconds(), windowEnd.ToUnixTimeMilliseconds(), ct);
            if (res.HasHasMore && res.HasMore)
                log($"Warning: server truncated bars in window {windowStart:yyyy-MM-dd}..{windowEnd:yyyy-MM-dd}");
            var inWindow = 0;
            foreach (var bar in res.Trendbar)
            {
                if (!bar.HasUtcTimestampInMinutes || !bar.HasLow) continue;
                if (bar.UtcTimestampInMinutes >= currentMinute) continue;
                if (any && bar.UtcTimestampInMinutes <= lastMinute) continue;
                lastMinute = bar.UtcTimestampInMinutes;
                any = true;
                long low = ScaleDown(bar.Low, priceDiv);
                long open = ScaleDown(bar.Low + (long)bar.DeltaOpen, priceDiv);
                long close = ScaleDown(bar.Low + (long)bar.DeltaClose, priceDiv);
                long high = ScaleDown(bar.Low + (long)bar.DeltaHigh, priceDiv);
                int avg = (int)Math.Round((open + high + low + close) / 4.0, MidpointRounding.AwayFromZero);
                long barUnix = bar.UtcTimestampInMinutes * 60L;
                var time = DateTimeOffset.FromUnixTimeSeconds(barUnix).UtcDateTime;
                db.WriteMinute(symbolName, time, (int)low, (int)high, avg, avgApprox: true,
                    spreadCode: SpreadCodes.Keep, volume: VolumeCodes.Keep);
                if (earliest == 0 || barUnix < earliest) earliest = barUnix;
                total++;
                inWindow++;
            }
            log($"{symbolName} {windowStart:yyyy-MM-dd}..{windowEnd:yyyy-MM-dd}: {inWindow} bars, total {totalSoFar + total}");
            windowStart = windowEnd;
            await Task.Delay(RequestDelay, ct);
        }
        return (total, earliest);
    }

    private static long ScaleDown(long rawPoints, int priceDiv) =>
        priceDiv == 1 ? rawPoints : (rawPoints + priceDiv / 2) / priceDiv;

    private static async Task<DateTimeOffset?> FindFirstDataAsync(
        CTraderClient client, long accountId, long symbolId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var cursor = fromUtc;
        while (cursor < toUtc)
        {
            ct.ThrowIfCancellationRequested();
            var end = cursor.AddDays(ProbeChunkDays);
            if (end > toUtc) end = toUtc;
            var res = await client.GetTrendbarsAsync(accountId, symbolId, ProtoOATrendbarPeriod.W1,
                cursor.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), ct);
            foreach (var bar in res.Trendbar)
                if (bar.HasUtcTimestampInMinutes)
                    return DateTimeOffset.FromUnixTimeSeconds(bar.UtcTimestampInMinutes * 60L);
            cursor = end;
            await Task.Delay(RequestDelay, ct);
        }
        return null;
    }
}
