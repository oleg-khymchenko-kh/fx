using System.Runtime.InteropServices;
using FXViewer.Storage;

namespace FXViewer.Compute;

public static class EntryPointsSymbol
{
    public const byte BuyBit = 1;
    public const byte SellBit = 2;
    public const byte BothLostBit = 4;

    private const long MaxRedoSeconds = 400L * 86400;

    public static bool StoredBuyWin(int max) => max > 0;

    public static bool StoredSellWin(int min) => min < 0;

    public static bool StoredBothLost(long avgSum) => avgSum > 0;

    public static bool StoredUndecided(Candle c) => c.Min == 0 && c.Max == 0 && c.Avg == 0;

    private static (int Min, int Max, int Avg) Encode(byte state) => (
        (state & SellBit) != 0 ? -1 : 0,
        (state & BuyBit) != 0 ? 1 : 0,
        (state & BothLostBit) != 0 ? 1 : 0);

    public static byte[] Evaluate(List<Candle> minutes, int stopPoints, int takePoints)
    {
        int n = minutes.Count;
        var state = new byte[n];
        if (n == 0) return state;
        var span = CollectionsMarshal.AsSpan(minutes);
        int minPrice = int.MaxValue;
        int maxPrice = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (span[i].Min < minPrice) minPrice = span[i].Min;
            if (span[i].Max > maxPrice) maxPrice = span[i].Max;
        }
        int capacity = (int)Math.Min((long)n + 1, (long)maxPrice - minPrice + 2);
        var upIndex = new int[capacity];
        var upValue = new int[capacity];
        var downIndex = new int[capacity];
        var downValue = new int[capacity];
        int upTop = 0;
        int downTop = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            int c = i + 1;
            if (c < n)
            {
                int high = span[c].Max;
                while (upTop > 0 && upValue[upTop - 1] <= high) upTop--;
                upIndex[upTop] = c;
                upValue[upTop] = high;
                upTop++;
                int low = span[c].Min;
                while (downTop > 0 && downValue[downTop - 1] >= low) downTop--;
                downIndex[downTop] = c;
                downValue[downTop] = low;
                downTop++;
            }
            long price = span[i].Avg;
            int buyTake = FirstAtOrAbove(upIndex, upValue, upTop, price + takePoints, n);
            int buyStop = FirstAtOrBelow(downIndex, downValue, downTop, price - stopPoints, n);
            int sellTake = FirstAtOrBelow(downIndex, downValue, downTop, price - takePoints, n);
            int sellStop = FirstAtOrAbove(upIndex, upValue, upTop, price + stopPoints, n);
            bool buyWin = buyTake < buyStop;
            bool sellWin = sellTake < sellStop;
            bool buyLost = buyStop < n && buyStop <= buyTake;
            bool sellLost = sellStop < n && sellStop <= sellTake;
            byte s = 0;
            if (buyWin) s |= BuyBit;
            if (sellWin) s |= SellBit;
            if (buyLost && sellLost) s |= BothLostBit;
            state[i] = s;
        }
        return state;
    }

    private static int FirstAtOrAbove(int[] index, int[] value, int top, long level, int missing)
    {
        if (top == 0 || value[0] < level) return missing;
        int lo = 0;
        int hi = top - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (value[mid] >= level) lo = mid;
            else hi = mid - 1;
        }
        return index[lo];
    }

    private static int FirstAtOrBelow(int[] index, int[] value, int top, long level, int missing)
    {
        if (top == 0 || value[0] > level) return missing;
        int lo = 0;
        int hi = top - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (value[mid] <= level) lo = mid;
            else hi = mid - 1;
        }
        return index[lo];
    }

    public static (int Years, int Minutes) Generate(
        CandleDatabase db, string sourceSymbol, string targetSymbol, int stopPoints, int takePoints,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var years = db.ExistingYears(sourceSymbol);
        if (years.Count == 0)
        {
            db.DeleteSymbol(targetSymbol);
            log?.Invoke($"{sourceSymbol}: no data, {targetSymbol} cleared");
            progress?.Report(1.0);
            return (0, 0);
        }
        var from = new DateTime(years[0], 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(years[^1], 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var minutes = db.ReadRange(sourceSymbol, from, to);
        db.DeleteSymbol(targetSymbol);
        int written = Write(db, targetSymbol, minutes, stopPoints, takePoints, ct, progress);
        db.FlushAll();
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: {written} minutes written " +
            $"(stop {stopPoints} points, take {takePoints} points)");
        return (years.Count, written);
    }

    public static int Refresh(
        CandleDatabase db, string sourceSymbol, string targetSymbol, int stopPoints, int takePoints,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null)
            return Generate(db, sourceSymbol, targetSymbol, stopPoints, takePoints, log, ct, progress)
                .Minutes;

        var lastSource = db.LastFilledMinuteUtc(sourceSymbol);
        if (lastSource == null)
        {
            progress?.Report(1.0);
            log?.Invoke($"{sourceSymbol}: no data");
            return 0;
        }

        var redoFrom = UnresolvedTailStart(db, targetSymbol, lastTarget.Value);
        if (redoFrom > lastSource.Value)
        {
            progress?.Report(1.0);
            log?.Invoke($"{targetSymbol}: up to date");
            return 0;
        }
        var minutes = db.ReadRange(sourceSymbol, redoFrom, lastSource.Value);
        int written = Write(db, targetSymbol, minutes, stopPoints, takePoints, ct, progress);
        db.FlushSymbol(targetSymbol);
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: refreshed {written} minutes from {redoFrom:yyyy-MM-dd HH:mm} UTC");
        return written;
    }

    private static DateTime UnresolvedTailStart(CandleDatabase db, string targetSymbol, DateTime lastTarget)
    {
        long backSeconds = 86400;
        while (true)
        {
            var from = lastTarget.AddSeconds(-backSeconds);
            var tail = db.ReadRange(targetSymbol, from, lastTarget);
            for (int i = tail.Count - 1; i >= 0; i--)
            {
                if (StoredUndecided(tail[i])) continue;
                return tail[i].TimeUtc.AddMinutes(1);
            }
            if (backSeconds >= MaxRedoSeconds || tail.Count == 0) return from;
            backSeconds *= 2;
        }
    }

    private static int Write(CandleDatabase db, string targetSymbol, List<Candle> minutes,
        int stopPoints, int takePoints, CancellationToken ct, IProgress<double>? progress)
    {
        int n = minutes.Count;
        if (n == 0) return 0;
        var state = Evaluate(minutes, stopPoints, takePoints);
        int reportEvery = Math.Max(1, n / 100);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (min, max, avg) = Encode(state[i]);
            db.WriteMinute(targetSymbol, minutes[i].TimeUtc, min, max, avg, false);
            if (i % reportEvery == 0) progress?.Report((double)(i + 1) / n);
        }
        return n;
    }
}
