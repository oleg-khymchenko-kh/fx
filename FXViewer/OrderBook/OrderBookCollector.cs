using System.Net;
using System.Net.Http;

namespace FXViewer.OrderBook;

public sealed class OrderBookCollector : IDisposable
{
    public static readonly string[] Pairs = { "EURUSD", "GBPUSD" };
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    private readonly Func<string, string> _symbolDirectory;
    private readonly Action<string> _log;
    private readonly Dictionary<string, long> _lastTime = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _client;
    private bool _seeded;

    public OrderBookCollector(Func<string, string> symbolDirectory, Action<string> log)
    {
        _symbolDirectory = symbolDirectory;
        _log = log;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("FXViewer/1.0");
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        Seed();
        foreach (var pair in Pairs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await CollectAsync(pair, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"order book {pair}: {ex.Message}");
            }
        }
    }

    private void Seed()
    {
        if (_seeded) return;
        _seeded = true;
        foreach (var pair in Pairs)
        {
            long last = 0;
            try { last = OrderBookStore.LastTimeUnix(_symbolDirectory(pair)); }
            catch (Exception ex) { _log($"order book {pair}: cannot read the store: {ex.Message}"); }
            _lastTime[pair] = last;
            if (last > 0)
                _log($"order book {pair}: stored up to {Utc(last):yyyy-MM-dd HH:mm} UTC");
        }
    }

    private async Task CollectAsync(string pair, CancellationToken ct)
    {
        _lastTime.TryGetValue(pair, out long after);
        var fetched = await OrderBookFetcher.FetchNewAsync(_client, pair, after, _log, ct);
        if (fetched.Count == 0) return;

        var dir = _symbolDirectory(pair);
        await Task.Run(() =>
        {
            foreach (var item in fetched)
            {
                ct.ThrowIfCancellationRequested();
                OrderBookStore.SaveRaw(dir, item.Snapshot.ImageTimeUnix, item.Png);
                OrderBookStore.Append(dir, item.Snapshot, _log);
                _lastTime[pair] = item.Snapshot.TimeUnix;
            }
        }, ct);

        var last = fetched[^1];
        if (fetched.Count > 1)
            _log($"order book {pair}: stored {fetched.Count} snapshots " +
                $"{Utc(fetched[0].Snapshot.TimeUnix):MM-dd HH:mm}..{Utc(last.Snapshot.TimeUnix):MM-dd HH:mm} UTC");
        _log($"order book {pair}: stored {Utc(last.Snapshot.TimeUnix):yyyy-MM-dd HH:mm} UTC, " +
            $"{last.Snapshot.OrdersCount} orders / {last.Snapshot.PositionsCount} positions, {last.Report}");
    }

    public void Dispose() => _client.Dispose();

    private static DateTime Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
}
