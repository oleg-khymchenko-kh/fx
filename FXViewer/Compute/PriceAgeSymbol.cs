using FXViewer.Storage;

namespace FXViewer.Compute;

public static class PriceAgeSymbol
{
    public const int NeverSeen = int.MaxValue;

    public static int[] Evaluate(List<Candle> minutes, int pipPoints)
    {
        int n = minutes.Count;
        var ages = new int[n];
        var lastSeen = new Dictionary<int, int>();
        int prevAvgPip = 0;
        for (int i = 0; i < n; i++)
        {
            int lo = PipLevel(minutes[i].Min, pipPoints);
            int hi = PipLevel(minutes[i].Max, pipPoints);
            int avgPip = PipLevel(minutes[i].Avg, pipPoints);
            int maxUp = 0;
            int maxDown = 0;
            for (int p = lo; p <= hi; p++)
            {
                int a = lastSeen.TryGetValue(p, out int seen) ? i - seen : NeverSeen;
                if (p >= prevAvgPip)
                {
                    if (a > maxUp) maxUp = a;
                }
                else if (a > maxDown) maxDown = a;
                lastSeen[p] = i;
            }
            bool up = i == 0 || maxUp > maxDown || (maxUp == maxDown && avgPip >= prevAvgPip);
            ages[i] = up ? maxUp : -maxDown;
            prevAvgPip = avgPip;
        }
        return ages;
    }

    private static int PipLevel(int points, int pipPoints) => (points + pipPoints / 2) / pipPoints;

    public static (int Years, int Minutes) Generate(
        CandleDatabase db, string sourceSymbol, string targetSymbol, int pipPoints,
        long confirmedEndUnix = 0,
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
        var to = Clamp(new DateTime(years[^1], 12, 31, 23, 59, 0, DateTimeKind.Utc), confirmedEndUnix);
        var minutes = db.ReadRange(sourceSymbol, from, to);
        db.DeleteSymbol(targetSymbol);
        int written = Write(db, targetSymbol, minutes, DateTime.MinValue, pipPoints, ct, progress);
        db.FlushAll();
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: {written} minutes written (pip {pipPoints} points)");
        return (years.Count, written);
    }

    public static int Refresh(
        CandleDatabase db, string sourceSymbol, string targetSymbol, int pipPoints,
        long confirmedEndUnix = 0,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var lastTarget = db.LastFilledMinuteUtc(targetSymbol);
        if (lastTarget == null)
            return Generate(db, sourceSymbol, targetSymbol, pipPoints, confirmedEndUnix,
                log, ct, progress).Minutes;

        var lastSource = db.LastFilledMinuteUtc(sourceSymbol);
        if (lastSource == null)
        {
            progress?.Report(1.0);
            log?.Invoke($"{sourceSymbol}: no data");
            return 0;
        }
        var years = db.ExistingYears(sourceSymbol);
        var targetYears = db.ExistingYears(targetSymbol);
        if (targetYears.Count > 0 && years[0] < targetYears[0])
        {
            log?.Invoke($"{targetSymbol}: source history now starts earlier, doing a full rebuild");
            return Generate(db, sourceSymbol, targetSymbol, pipPoints, confirmedEndUnix,
                log, ct, progress).Minutes;
        }
        var readTo = Clamp(lastSource.Value, confirmedEndUnix);
        if (readTo <= lastTarget.Value)
        {
            progress?.Report(1.0);
            log?.Invoke($"{targetSymbol}: up to date");
            return 0;
        }
        var from = new DateTime(years[0], 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var minutes = db.ReadRange(sourceSymbol, from, readTo);
        var writeFrom = lastTarget.Value.AddMinutes(1);
        int written = Write(db, targetSymbol, minutes, writeFrom, pipPoints, ct, progress);
        db.FlushSymbol(targetSymbol);
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: refreshed {written} minutes from {writeFrom:yyyy-MM-dd HH:mm} UTC");
        return written;
    }

    private static DateTime Clamp(DateTime to, long confirmedEndUnix)
    {
        if (confirmedEndUnix <= 0) return to;
        var confirmed = DateTimeOffset.FromUnixTimeSeconds(confirmedEndUnix).UtcDateTime;
        return confirmed < to ? confirmed : to;
    }

    private static int Write(CandleDatabase db, string targetSymbol, List<Candle> minutes,
        DateTime writeFrom, int pipPoints, CancellationToken ct, IProgress<double>? progress)
    {
        int n = minutes.Count;
        if (n == 0) return 0;
        var ages = Evaluate(minutes, pipPoints);
        int written = 0;
        int reportEvery = Math.Max(1, n / 100);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % reportEvery == 0) progress?.Report((double)(i + 1) / n);
            if (minutes[i].TimeUtc < writeFrom) continue;
            int age = ages[i];
            db.WriteMinute(targetSymbol, minutes[i].TimeUtc, age, age, age, false);
            written++;
        }
        return written;
    }
}
