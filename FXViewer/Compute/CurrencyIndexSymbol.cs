using FXViewer.Storage;

namespace FXViewer.Compute;

public static class CurrencyIndexSymbol
{
    private const int PriceScale = 100000;
    private const int PipScale = 10;

    public sealed record Spec(string Name, string IndexSymbol, string Pair, bool UsePips, int PairPipPoints);

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Spec> Specs = new();
    private static readonly Dictionary<string, double?> StartPrices = new();

    public static bool UsdIsBase(string pair) =>
        pair.StartsWith("USD", StringComparison.OrdinalIgnoreCase);

    public static string CurrencyOf(string pair)
    {
        var key = IndicatorSymbol.NameKey(pair);
        if (key.Length < 6) return key;
        return UsdIsBase(key) ? key.Substring(3, 3) : key.Substring(0, 3);
    }

    public static void Register(IEnumerable<Spec> specs)
    {
        lock (Lock)
        {
            Specs.Clear();
            StartPrices.Clear();
            foreach (var spec in specs) Specs[IndicatorSymbol.NameKey(spec.Name)] = spec;
        }
    }

    public static Spec? SpecOf(string symbol)
    {
        lock (Lock)
        {
            return Specs.GetValueOrDefault(IndicatorSymbol.NameKey(symbol));
        }
    }

    public static IReadOnlyList<int> ExistingYears(CandleDatabase db, Spec spec) =>
        db.ExistingYears(spec.IndexSymbol);

    public static List<Candle> ReadRange(CandleDatabase db, Spec spec, DateTime fromUtc, DateTime toUtc)
    {
        var index = db.ReadRange(spec.IndexSymbol, fromUtc, toUtc);
        if (index.Count == 0) return new List<Candle>();
        var pair = db.ReadRange(spec.Pair, fromUtc, toUtc, includeWide: true);
        return Combine(db, spec, index, pair);
    }

    public static Candle? LastCandle(CandleDatabase db, Spec spec)
    {
        var last = db.LastFilledMinuteUtc(spec.IndexSymbol);
        if (last == null) return null;
        var combined = ReadRange(db, spec, last.Value.AddDays(-1), last.Value);
        return combined.Count > 0 ? combined[^1] : null;
    }

    public static List<Candle> Tail(
        CandleDatabase db, Spec spec, IReadOnlyList<Candle> indexTail, IReadOnlyList<Candle>? pairLive)
    {
        if (indexTail.Count == 0) return new List<Candle>();
        long from = indexTail[0].MinuteUnixSeconds;
        long to = indexTail[^1].MinuteUnixSeconds;
        var pair = DollarIndexSymbol.WithLive(
            db.ReadRange(spec.Pair, UnixToUtc(from), UnixToUtc(to), includeWide: true), pairLive, from - 60, to);
        return Combine(db, spec, indexTail, pair);
    }

    public static List<Candle> Combine(
        CandleDatabase db, Spec spec, IReadOnlyList<Candle> index, IReadOnlyList<Candle> pair)
    {
        var result = new List<Candle>(index.Count);
        double? start = StartPrice(db, spec);
        if (start == null) return result;
        bool usdBase = UsdIsBase(spec.Pair);
        int j = 0;
        for (int i = 0; i < index.Count; i++)
        {
            long t = index[i].MinuteUnixSeconds;
            while (j < pair.Count && pair[j].MinuteUnixSeconds < t) j++;
            if (j >= pair.Count) break;
            if (pair[j].MinuteUnixSeconds != t || index[i].Avg <= 0) continue;
            if (!DollarIndexSymbol.TryEffectivePrice(pair[j], spec.PairPipPoints, out double price)) continue;
            result.Add(DollarIndexSymbol.Flat(t,
                Value(index[i].Avg, price, start.Value, usdBase, spec.UsePips, spec.PairPipPoints)));
        }
        return result;
    }

    public static int Value(int indexAvg, double pairPrice, double startPrice, bool usdBase, bool usePips,
        int pairPipPoints)
    {
        if (usePips)
        {
            double u = (indexAvg - PriceScale) / (double)PipScale;
            double r = (pairPrice - startPrice) / pairPipPoints;
            double pips = usdBase ? u - r : u + r;
            return (int)Math.Round(PriceScale + pips * PipScale, MidpointRounding.AwayFromZero);
        }
        double logU = Math.Log(indexAvg / (double)PriceScale);
        double logR = Math.Log(pairPrice / startPrice);
        double log = usdBase ? logU - logR : logU + logR;
        return (int)Math.Round(Math.Exp(log) * PriceScale, MidpointRounding.AwayFromZero);
    }

    private static double? StartPrice(CandleDatabase db, Spec spec)
    {
        string key = IndicatorSymbol.NameKey(spec.IndexSymbol) + "|" + IndicatorSymbol.NameKey(spec.Pair);
        lock (Lock)
        {
            if (StartPrices.TryGetValue(key, out var cached)) return cached;
        }
        double? value = null;
        foreach (int year in db.ExistingYears(spec.IndexSymbol))
        {
            var range = db.FilledRangeUtc(spec.IndexSymbol, year);
            if (range == null) continue;
            var first = range.Value.FirstUtc;
            var candles = db.ReadRange(spec.Pair, first, first, includeWide: true);
            if (candles.Count > 0 && DollarIndexSymbol.TryEffectivePrice(candles[0], spec.PairPipPoints, out double p))
                value = p;
            break;
        }
        lock (Lock)
        {
            StartPrices[key] = value;
        }
        return value;
    }

    private static DateTime UnixToUtc(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
}
