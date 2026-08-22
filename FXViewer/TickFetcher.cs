namespace FXViewer;

public static class TickFetcher
{
    public static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(250);

    private const int RetryLimit = 5;

    public static async Task<List<(long Ms, long Price)>> FetchWindowAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName,
        ProtoOAQuoteType quoteType, long fromMs, long toMs, Action<string> log, CancellationToken ct)
    {
        var ticks = new List<(long Ms, long Price)>();
        long acceptBelow = long.MaxValue;
        long requestTo = toMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var res = await FetchPageAsync(client, accountId, symbolId, symbolName,
                quoteType, fromMs, requestTo, log, ct);
            var page = Decode(res, symbolName, quoteType);
            if (page.Count == 0) break;
            foreach (var t in page)
                if (t.Ms >= fromMs && t.Ms < acceptBelow)
                    ticks.Add(t);
            long oldest = page[^1].Ms;
            if (!res.HasMore || oldest <= fromMs) break;
            if (oldest >= acceptBelow)
            {
                log($"{symbolName}: tick paging made no progress at {oldest}, stopping window early");
                break;
            }
            acceptBelow = oldest;
            requestTo = oldest;
        }
        ticks.Reverse();
        if (ticks.Count > 0)
        {
            long lo = long.MaxValue;
            long hi = 0;
            foreach (var t in ticks)
            {
                if (t.Price < lo) lo = t.Price;
                if (t.Price > hi) hi = t.Price;
            }
            if (hi > lo * 2)
                throw new InvalidOperationException(
                    $"{symbolName}: decoded {quoteType} ticks look wrong (min {lo}, max {hi}), aborting");
        }
        return ticks;
    }

    private static async Task<ProtoOAGetTickDataRes> FetchPageAsync(
        CTraderClient client, long accountId, long symbolId, string symbolName,
        ProtoOAQuoteType quoteType, long fromMs, long toMs, Action<string> log, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var res = await client.GetTickDataAsync(
                    accountId, symbolId, quoteType, fromMs, toMs, ct);
                await Task.Delay(RequestDelay, ct);
                return res;
            }
            catch (InvalidOperationException ex) when (attempt < RetryLimit
                && (ex.Message.Contains("rate limited", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("BLOCKED_PAYLOAD_TYPE", StringComparison.OrdinalIgnoreCase)))
            {
                int waitSeconds = 10 * (attempt + 1);
                log($"{symbolName}: rate limited, waiting {waitSeconds} s...");
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
            }
        }
    }

    // Server sends ticks newest first; entry 0 holds absolute timestamp and price,
    // every next entry holds deltas from the previous one for both fields.
    private static List<(long Ms, long Price)> Decode(
        ProtoOAGetTickDataRes res, string symbolName, ProtoOAQuoteType quoteType)
    {
        var result = new List<(long Ms, long Price)>(res.TickData.Count);
        long ms = 0;
        long price = 0;
        foreach (var t in res.TickData)
        {
            if (result.Count == 0)
            {
                ms = t.Timestamp;
                price = t.Tick;
            }
            else
            {
                ms += t.Timestamp;
                price += t.Tick;
            }
            if (ms <= 0 || price <= 0)
                throw new InvalidOperationException(
                    $"{symbolName}: unexpected {quoteType} tick encoding (ms={ms}, price={price})");
            result.Add((ms, price));
        }
        return result;
    }
}
