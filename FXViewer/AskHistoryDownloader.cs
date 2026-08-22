using FXViewer.Storage;

namespace FXViewer;

public static class AskHistoryDownloader
{
    public const int SeedDays = 14;
    public const int MaxTailDays = 30;

    private const int WindowHours = 24;

    public static async Task<(int Total, long EarliestUnix)> DownloadTailToDbAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName,
        CandleDatabase db, Action<string> log, CancellationToken ct, int priceDiv = 1)
    {
        var toUtc = DateTimeOffset.UtcNow;
        var lastFilled = db.LastFilledMinuteUtc(symbolName);
        DateTimeOffset from;
        if (lastFilled == null)
        {
            from = toUtc.AddDays(-SeedDays);
            log($"{symbolName}: DB empty, seeding last {SeedDays} days from ask ticks");
        }
        else
        {
            from = new DateTimeOffset(lastFilled.Value, TimeSpan.Zero).AddMinutes(1);
            var floor = toUtc.AddDays(-MaxTailDays);
            if (from < floor)
            {
                log($"{symbolName}: gap is longer than {MaxTailDays} days, older ask minutes stay missing");
                from = floor;
            }
        }
        if (from >= toUtc)
        {
            log($"{symbolName}: already up to date");
            return (0, 0);
        }
        log($"{symbolName}: fetching ask ticks {from:yyyy-MM-dd HH:mm}..{toUtc:yyyy-MM-dd HH:mm} UTC");
        var result = await DownloadRangeAsync(
            client, accountId, symbolId, symbolName, db, from, toUtc, priceDiv, log, ct);
        db.FlushAll();
        return result;
    }

    public static async Task<(int Total, long EarliestUnix)> DownloadRangeToDbAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName,
        CandleDatabase db, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        Action<string> log, CancellationToken ct, int priceDiv = 1)
    {
        if (fromUtc >= toUtc) return (0, 0);
        var result = await DownloadRangeAsync(
            client, accountId, symbolId, symbolName, db, fromUtc, toUtc, priceDiv, log, ct);
        db.FlushSymbol(symbolName);
        return result;
    }

    private static async Task<(int Total, long EarliestUnix)> DownloadRangeAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName, CandleDatabase db,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int priceDiv,
        Action<string> log, CancellationToken ct)
    {
        long fromUnix = fromUtc.ToUnixTimeSeconds();
        fromUtc = DateTimeOffset.FromUnixTimeSeconds(fromUnix - fromUnix % 60);
        long currentMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        int total = 0;
        long earliest = 0;
        var windowStart = fromUtc;
        while (windowStart < toUtc)
        {
            ct.ThrowIfCancellationRequested();
            var windowEnd = windowStart.AddHours(WindowHours);
            if (windowEnd > toUtc) windowEnd = toUtc;
            var ticks = await TickFetcher.FetchWindowAsync(client, accountId, symbolId, symbolName,
                ProtoOAQuoteType.Ask, windowStart.ToUnixTimeMilliseconds(),
                windowEnd.ToUnixTimeMilliseconds(), log, ct);
            int minutes = WriteMinutes(db, symbolName, ticks, currentMinute, priceDiv, ref earliest);
            total += minutes;
            log($"{symbolName} {windowStart:yyyy-MM-dd HH:mm}..{windowEnd:yyyy-MM-dd HH:mm}: " +
                $"{ticks.Count:N0} ticks, {minutes} minutes, total {total}");
            windowStart = windowEnd;
        }
        return (total, earliest);
    }

    private static int WriteMinutes(CandleDatabase db, string symbolName,
        List<(long Ms, long Price)> ticks, long currentMinute, int priceDiv, ref long earliest)
    {
        int written = 0;
        int i = 0;
        while (i < ticks.Count)
        {
            long minute = ticks[i].Ms / 60000;
            if (minute >= currentMinute) break;
            long open = ticks[i].Price;
            long low = open;
            long high = open;
            long close = open;
            int j = i + 1;
            while (j < ticks.Count && ticks[j].Ms / 60000 == minute)
            {
                long p = ticks[j].Price;
                if (p < low) low = p;
                if (p > high) high = p;
                close = p;
                j++;
            }
            i = j;
            long lowPts = ScaleDown(low, priceDiv);
            long highPts = ScaleDown(high, priceDiv);
            int avg = (int)Math.Round(
                (ScaleDown(open, priceDiv) + highPts + lowPts + ScaleDown(close, priceDiv)) / 4.0,
                MidpointRounding.AwayFromZero);
            long minuteUnix = minute * 60;
            db.WriteMinute(symbolName, DateTimeOffset.FromUnixTimeSeconds(minuteUnix).UtcDateTime,
                (int)lowPts, (int)highPts, avg, avgApprox: true, spreadCode: SpreadCodes.Keep,
                volume: VolumeCodes.Keep);
            if (earliest == 0 || minuteUnix < earliest) earliest = minuteUnix;
            written++;
        }
        return written;
    }

    private static long ScaleDown(long rawPoints, int priceDiv) =>
        priceDiv == 1 ? rawPoints : (rawPoints + priceDiv / 2) / priceDiv;
}
