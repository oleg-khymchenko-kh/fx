using FXViewer.Storage;

namespace FXViewer.Game;

public readonly record struct GameQuote(long Unix, int BidLow, int BidHigh, int BidAvg, int Spread)
{
    public int AskLow => BidLow + Spread;
    public int AskHigh => BidHigh + Spread;
    public int AskAvg => BidAvg + Spread;
}

public sealed record GamePosition(int Id, bool Buy, long OpenUnix, int OpenPrice,
    int StopPrice, int TakePrice, bool HasStop, bool HasTake, double Pips);

public sealed record GameOrder(int Id, bool Buy, long PlacedUnix, int Price,
    double StopPips, double TakePips);

public sealed record GameTrade(int Id, bool Buy, long OpenUnix, int OpenPrice, long CloseUnix,
    int ClosePrice, string Reason, double Pips);

public sealed record GameResult(
    IReadOnlyList<GamePosition> Positions,
    IReadOnlyList<GameOrder> Orders,
    IReadOnlyList<GameTrade> Trades,
    GameQuote? Quote)
{
    public static readonly GameResult Empty = new(
        Array.Empty<GamePosition>(), Array.Empty<GameOrder>(), Array.Empty<GameTrade>(), null);

    public double OpenPips => Positions.Sum(p => p.Pips);

    public double ClosedPips => Trades.Sum(t => t.Pips);

    public double TotalPips => OpenPips + ClosedPips;

    public int Wins => Trades.Count(t => t.Pips > 0);
}

public static class GameSim
{
    public const string ReasonStop = "stop";
    public const string ReasonTake = "take";
    public const string ReasonManual = "manual";
    public const string ReasonDayEnd = "day end";

    private const double FallbackSpreadPips = 1.0;

    private sealed class OpenPosition
    {
        public int Id;
        public bool Buy;
        public long OpenUnix;
        public int OpenPrice;
        public int StopPrice;
        public int TakePrice;
        public bool HasStop;
        public bool HasTake;
    }

    private sealed class PendingOrder
    {
        public int Id;
        public bool Buy;
        public long PlacedUnix;
        public int Price;
        public double StopPips;
        public double TakePips;
    }

    public static int PipToPoints(double pips, int pipPoints) =>
        (int)Math.Round(pips * pipPoints);

    public static double PointsToPips(double points, int pipPoints) =>
        pipPoints <= 0 ? 0 : points / pipPoints;

    public static int SpreadPoints(Candle candle, int pipPoints) =>
        candle.HasSpread && candle.SpreadTenths > 0
            ? (candle.SpreadTenths * pipPoints + 5) / 10
            : -1;

    public static int IndexAt(Candle[] minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static long StepTime(IEnumerable<Candle[]> series, long fromUnix, int direction)
    {
        long best = fromUnix;
        foreach (var minutes in series)
        {
            long t = StepTime(minutes, fromUnix, direction);
            if (t == fromUnix) continue;
            if (best == fromUnix || (direction > 0 ? t < best : t > best)) best = t;
        }
        return best;
    }

    private static long StepTime(Candle[] minutes, long fromUnix, int direction)
    {
        if (direction > 0)
        {
            int next = IndexAt(minutes, fromUnix + 1);
            return next < minutes.Length ? minutes[next].MinuteUnixSeconds : fromUnix;
        }
        int prev = IndexAt(minutes, fromUnix);
        return prev > 0 ? minutes[prev - 1].MinuteUnixSeconds : fromUnix;
    }

    public static long? SnapTime(IEnumerable<Candle[]> series, long unixSeconds)
    {
        long? before = null;
        long? after = null;
        foreach (var minutes in series)
        {
            if (minutes.Length == 0) continue;
            int i = IndexAt(minutes, unixSeconds + 1);
            if (i > 0)
            {
                long t = minutes[i - 1].MinuteUnixSeconds;
                if (before == null || t > before) before = t;
            }
            else if (after == null || minutes[0].MinuteUnixSeconds < after)
            {
                after = minutes[0].MinuteUnixSeconds;
            }
        }
        return before ?? after;
    }

    public static GameQuote QuoteOf(Candle candle, int pipPoints, bool askValues, int fallbackSpread)
    {
        int spread = SpreadPoints(candle, pipPoints);
        if (spread < 0) spread = fallbackSpread;
        int lo = candle.Min;
        int hi = candle.Max;
        int avg = candle.Avg;
        if (askValues)
        {
            lo -= spread;
            hi -= spread;
            avg -= spread;
        }
        return new GameQuote(candle.MinuteUnixSeconds, lo, hi, avg, spread);
    }

    public static GameResult Run(IReadOnlyList<GameAction> actions, long startUnix, Candle[] minutes,
        int pipPoints, bool askValues, long uptoUnix)
    {
        var positions = new List<OpenPosition>();
        var orders = new List<PendingOrder>();
        var trades = new List<GameTrade>();
        if (minutes.Length == 0) return GameResult.Empty;
        int spread = PipToPoints(FallbackSpreadPips, pipPoints);
        long from = actions.Count > 0 ? Math.Min(startUnix, actions[0].AtUnix) : startUnix;
        int ai = 0;
        GameQuote? quote = null;
        for (int i = Math.Max(0, IndexAt(minutes, from + 1) - 1); i < minutes.Length; i++)
        {
            var candle = minutes[i];
            long t = candle.MinuteUnixSeconds;
            if (t > uptoUnix) break;
            for (; ai < actions.Count && actions[ai].AtUnix < t; ai++)
                if (quote is { } last)
                    Apply(actions[ai], actions[ai].AtUnix, last, positions, orders, trades, pipPoints);
            var q = QuoteOf(candle, pipPoints, askValues, spread);
            spread = q.Spread;
            quote = q;
            for (int p = 0; p < positions.Count; p++)
            {
                var pos = positions[p];
                if (pos.OpenUnix >= t) continue;
                int close = 0;
                string? reason = null;
                if (pos.Buy)
                {
                    if (pos.HasStop && q.BidLow <= pos.StopPrice)
                    {
                        close = pos.StopPrice;
                        reason = ReasonStop;
                    }
                    else if (pos.HasTake && q.BidHigh >= pos.TakePrice)
                    {
                        close = pos.TakePrice;
                        reason = ReasonTake;
                    }
                }
                else
                {
                    if (pos.HasStop && q.AskHigh >= pos.StopPrice)
                    {
                        close = pos.StopPrice;
                        reason = ReasonStop;
                    }
                    else if (pos.HasTake && q.AskLow <= pos.TakePrice)
                    {
                        close = pos.TakePrice;
                        reason = ReasonTake;
                    }
                }
                if (reason == null) continue;
                trades.Add(TradeOf(pos, t, close, reason, pipPoints));
                positions.RemoveAt(p--);
            }
            for (int o = 0; o < orders.Count; o++)
            {
                var ord = orders[o];
                if (ord.PlacedUnix >= t) continue;
                if (ord.Price < q.BidLow || ord.Price > q.BidHigh) continue;
                positions.Add(OpenAt(ord.Id, ord.Buy, t,
                    ord.Buy ? ord.Price + q.Spread : ord.Price, ord.StopPips, ord.TakePips, pipPoints));
                orders.RemoveAt(o--);
            }
            for (; ai < actions.Count && actions[ai].AtUnix <= t; ai++)
                Apply(actions[ai], t, q, positions, orders, trades, pipPoints);
        }
        for (; ai < actions.Count && actions[ai].AtUnix <= uptoUnix; ai++)
            if (quote is { } last)
                Apply(actions[ai], actions[ai].AtUnix, last, positions, orders, trades, pipPoints);
        var open = new List<GamePosition>(positions.Count);
        foreach (var pos in positions)
        {
            double pips = quote is { } last
                ? PointsToPips(pos.Buy
                    ? last.BidAvg - pos.OpenPrice
                    : pos.OpenPrice - last.AskAvg, pipPoints)
                : 0;
            open.Add(new GamePosition(pos.Id, pos.Buy, pos.OpenUnix, pos.OpenPrice,
                pos.StopPrice, pos.TakePrice, pos.HasStop, pos.HasTake, pips));
        }
        var pending = orders
            .Select(o => new GameOrder(o.Id, o.Buy, o.PlacedUnix, o.Price, o.StopPips, o.TakePips))
            .ToList();
        return new GameResult(open, pending, trades, quote);
    }

    private static void Apply(GameAction action, long t, GameQuote q, List<OpenPosition> positions,
        List<PendingOrder> orders, List<GameTrade> trades, int pipPoints)
    {
        switch (action.Kind)
        {
            case GameActions.Market:
                positions.Add(OpenAt(action.Id, action.Buy, t,
                    action.Buy ? q.AskHigh : q.BidLow, action.StopPips, action.TakePips, pipPoints));
                break;
            case GameActions.Order:
                orders.Add(new PendingOrder
                {
                    Id = action.Id,
                    Buy = action.Buy,
                    PlacedUnix = t,
                    Price = action.Price,
                    StopPips = action.StopPips,
                    TakePips = action.TakePips,
                });
                break;
            case GameActions.Cancel:
                orders.RemoveAll(o => o.Id == action.Target);
                break;
            case GameActions.Close:
                for (int i = 0; i < positions.Count; i++)
                {
                    var pos = positions[i];
                    if (pos.Id != action.Target) continue;
                    int close = pos.Buy ? q.BidLow : q.AskHigh;
                    trades.Add(TradeOf(pos, t, close, ReasonManual, pipPoints));
                    positions.RemoveAt(i);
                    break;
                }
                break;
            case GameActions.DayEnd:
                foreach (var pos in positions)
                    trades.Add(TradeOf(pos, t, pos.Buy ? q.BidLow : q.AskHigh, ReasonDayEnd, pipPoints));
                positions.Clear();
                orders.Clear();
                break;
        }
    }

    private static OpenPosition OpenAt(int id, bool buy, long t, int fill, double stopPips,
        double takePips, int pipPoints)
    {
        int stop = PipToPoints(stopPips, pipPoints);
        int take = PipToPoints(takePips, pipPoints);
        return new OpenPosition
        {
            Id = id,
            Buy = buy,
            OpenUnix = t,
            OpenPrice = fill,
            HasStop = stop > 0,
            HasTake = take > 0,
            StopPrice = buy ? fill - stop : fill + stop,
            TakePrice = buy ? fill + take : fill - take,
        };
    }

    private static GameTrade TradeOf(OpenPosition pos, long t, int close, string reason, int pipPoints) =>
        new(pos.Id, pos.Buy, pos.OpenUnix, pos.OpenPrice, t, close, reason,
            PointsToPips(pos.Buy ? close - pos.OpenPrice : pos.OpenPrice - close, pipPoints));
}
