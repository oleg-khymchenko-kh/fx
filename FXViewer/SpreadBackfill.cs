using FXViewer.Storage;

namespace FXViewer;

public static class SpreadBackfill
{
    private const int WindowHours = 24;
    private const int EmptyWindowsToStop = 3;

    public readonly record struct Result(int Written, int Windows, int MaxTenths, bool ReachedStart);

    public static async Task<Result> RunAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName, CandleDatabase db,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int priceDiv, int pipPoints,
        Action<string> log, CancellationToken ct)
    {
        long toUnix = toUtc.ToUnixTimeSeconds();
        var windowEnd = DateTimeOffset.FromUnixTimeSeconds(toUnix - toUnix % 60);
        int written = 0;
        int windows = 0;
        int maxTenths = 0;
        int emptyWindows = 0;
        while (windowEnd > fromUtc)
        {
            ct.ThrowIfCancellationRequested();
            var windowStart = windowEnd.AddHours(-WindowHours);
            if (windowStart < fromUtc) windowStart = fromUtc;
            var minutes = db.ReadRange(symbolName, windowStart.UtcDateTime,
                windowEnd.AddMinutes(-1).UtcDateTime, includeWide: true);
            int missing = 0;
            foreach (var c in minutes)
                if (!c.HasSpread) missing++;
            if (missing == 0)
            {
                windowEnd = windowStart;
                continue;
            }

            var bid = await TickFetcher.FetchWindowAsync(client, accountId, symbolId, symbolName,
                ProtoOAQuoteType.Bid, windowStart.ToUnixTimeMilliseconds(),
                windowEnd.ToUnixTimeMilliseconds(), log, ct);
            var ask = await TickFetcher.FetchWindowAsync(client, accountId, symbolId, symbolName,
                ProtoOAQuoteType.Ask, windowStart.ToUnixTimeMilliseconds(),
                windowEnd.ToUnixTimeMilliseconds(), log, ct);
            windows++;
            if (bid.Count == 0 || ask.Count == 0)
            {
                emptyWindows++;
                log($"{symbolName} {windowStart:yyyy-MM-dd HH:mm}: no ticks on the server " +
                    $"({emptyWindows}/{EmptyWindowsToStop})");
                if (emptyWindows >= EmptyWindowsToStop)
                {
                    log($"{symbolName}: tick history ends here, stopping");
                    return new Result(written, windows, maxTenths, false);
                }
                windowEnd = windowStart;
                continue;
            }
            emptyWindows = 0;

            var spreads = MaxSpreadPerMinute(bid, ask, priceDiv, pipPoints);
            int windowWritten = 0;
            int windowMax = 0;
            foreach (var c in minutes)
            {
                if (!spreads.TryGetValue(c.MinuteUnixSeconds, out var tenths)) continue;
                if (!db.WriteSpread(symbolName, c.TimeUtc, SpreadCodes.FromTenths(tenths))) continue;
                windowWritten++;
                if (tenths > windowMax) windowMax = tenths;
            }
            db.FlushSymbol(symbolName);
            written += windowWritten;
            if (windowMax > maxTenths) maxTenths = windowMax;
            log($"{symbolName} {windowStart:yyyy-MM-dd HH:mm}: {bid.Count:N0} bid + {ask.Count:N0} ask ticks, " +
                $"spread on {windowWritten} of {minutes.Count} minutes, max {windowMax / 10.0:F1} pips, " +
                $"total {written:N0}");
            windowEnd = windowStart;
        }
        return new Result(written, windows, maxTenths, true);
    }

    private static Dictionary<long, int> MaxSpreadPerMinute(
        List<(long Ms, long Price)> bid, List<(long Ms, long Price)> ask, int priceDiv, int pipPoints)
    {
        var result = new Dictionary<long, int>();
        int i = 0;
        int j = 0;
        long lastBid = 0;
        long lastAsk = 0;
        while (i < bid.Count || j < ask.Count)
        {
            bool takeBid = j >= ask.Count || (i < bid.Count && bid[i].Ms <= ask[j].Ms);
            long ms;
            if (takeBid)
            {
                lastBid = bid[i].Price;
                ms = bid[i].Ms;
                i++;
            }
            else
            {
                lastAsk = ask[j].Price;
                ms = ask[j].Ms;
                j++;
            }
            if (lastBid <= 0 || lastAsk <= 0 || lastAsk < lastBid) continue;
            long points = (lastAsk - lastBid + priceDiv / 2) / priceDiv;
            int tenths = SpreadCodes.TenthsFromPoints(points, pipPoints);
            long minute = ms / 60000 * 60;
            if (!result.TryGetValue(minute, out var current) || tenths > current)
                result[minute] = tenths;
        }
        return result;
    }
}
