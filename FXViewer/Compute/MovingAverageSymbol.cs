using FXViewer.Storage;

namespace FXViewer.Compute;

public static class MovingAverageSymbol
{
    public static int WindowBars(int period, string unit)
    {
        long bars = (long)Math.Max(1, period) * IndicatorUnits.BarsPerUnit(unit);
        return (int)Math.Clamp(bars, 1, int.MaxValue);
    }

    public static int[] ComputeSma(IReadOnlyList<Candle> minutes, int windowBars, bool fromFuture)
    {
        int n = minutes.Count;
        var result = new int[n];
        if (n == 0) return result;
        int w = Math.Max(1, windowBars);
        var prefix = new long[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + minutes[i].Avg;
        for (int i = 0; i < n; i++)
        {
            int lo;
            int hi;
            if (fromFuture)
            {
                lo = i;
                hi = (int)Math.Min(n - 1, (long)i + w - 1);
            }
            else
            {
                hi = i;
                lo = (int)Math.Max(0, (long)i - w + 1);
            }
            long sum = prefix[hi + 1] - prefix[lo];
            int count = hi - lo + 1;
            result[i] = (int)Math.Round(sum / (double)count, MidpointRounding.AwayFromZero);
        }
        return result;
    }

    public static (int Years, int Minutes) Generate(
        CandleDatabase db, string sourceSymbol, string targetSymbol, int windowBars, bool fromFuture,
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
        int n = minutes.Count;
        int written = 0;
        if (n > 0)
        {
            var avg = ComputeSma(minutes, windowBars, fromFuture);
            int reportEvery = Math.Max(1, n / 100);
            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = minutes[i];
                int v = avg[i];
                db.WriteMinute(targetSymbol, c.TimeUtc, v, v, v, false);
                written++;
                if (i % reportEvery == 0) progress?.Report((double)(i + 1) / n);
            }
        }
        db.FlushAll();
        progress?.Report(1.0);
        log?.Invoke(
            $"{targetSymbol}: {written} minutes written (SMA {windowBars} bars{(fromFuture ? ", future" : ", past")})");
        return (years.Count, written);
    }

    public static int Refresh(
        CandleDatabase db, string sourceSymbol, string targetSymbol, int windowBars, bool fromFuture,
        long redoFromUnix = 0,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null)
            return Generate(db, sourceSymbol, targetSymbol, windowBars, fromFuture, log, ct, progress).Minutes;

        var lastSource = db.LastFilledMinuteUtc(sourceSymbol);
        if (lastSource == null)
        {
            progress?.Report(1.0);
            log?.Invoke($"{sourceSymbol}: no data");
            return 0;
        }

        long lastSourceUnix = ((DateTimeOffset)lastSource.Value).ToUnixTimeSeconds();
        long anchorUnix = ((DateTimeOffset)lastTarget.Value).ToUnixTimeSeconds();
        if (redoFromUnix > 0 && redoFromUnix - 60 < anchorUnix) anchorUnix = redoFromUnix - 60;
        if (lastSourceUnix <= anchorUnix)
        {
            progress?.Report(1.0);
            log?.Invoke($"{targetSymbol}: up to date");
            return 0;
        }
        var anchor = DateTimeOffset.FromUnixTimeSeconds(anchorUnix).UtcDateTime;

        int w = Math.Max(1, windowBars);
        var years = db.ExistingYears(sourceSymbol);
        var seriesStart = new DateTime(years[0], 1, 1, 0, 0, 0, DateTimeKind.Utc);

        long backSeconds = (long)(w + 8) * 60;
        List<Candle> minutes;
        int firstNewIdx;
        while (true)
        {
            var from = anchor.AddSeconds(-backSeconds);
            if (from < seriesStart) from = seriesStart;
            minutes = db.ReadRange(sourceSymbol, from, lastSource.Value);
            firstNewIdx = FirstMinuteAfter(minutes, anchorUnix);
            if (firstNewIdx >= w || from <= seriesStart) break;
            backSeconds *= 2;
        }

        int n = minutes.Count;
        var avg = ComputeSma(minutes, w, fromFuture);
        int writeStart = fromFuture ? Math.Max(0, firstNewIdx - (w - 1)) : firstNewIdx;
        int total = Math.Max(1, n - writeStart);
        int written = 0;
        for (int i = writeStart; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = minutes[i];
            int v = avg[i];
            db.WriteMinute(targetSymbol, c.TimeUtc, v, v, v, false);
            written++;
            if (i % Math.Max(1, total / 100) == 0) progress?.Report((double)(i - writeStart + 1) / total);
        }
        db.FlushSymbol(targetSymbol);
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: refreshed {written} minutes");
        return written;
    }

    private static int FirstMinuteAfter(IReadOnlyList<Candle> minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (minutes[mid].MinuteUnixSeconds <= unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
