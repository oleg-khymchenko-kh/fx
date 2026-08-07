using System.Globalization;
using System.IO;

namespace FXViewer.Storage;

public static class HistoryImporter
{
    private const int PriceScale = 100000;

    public static int ImportOhlcCsv(
        CandleDatabase db, string symbol, string csvPath, Action<string>? log = null,
        CancellationToken ct = default, int priceDiv = 1)
    {
        int written = 0;
        int lineNo = 0;
        int skipped = 0;
        using var reader = new StreamReader(csvPath);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            lineNo++;
            if (lineNo == 1 && line.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Length == 0) continue;

            var f = line.Split(';');
            if (f.Length < 5) { skipped++; continue; }
            if (!TryParseUtc(f[0], out var timeUtc)
                || !TryPoints(f[1], priceDiv, out var open)
                || !TryPoints(f[2], priceDiv, out var high)
                || !TryPoints(f[3], priceDiv, out var low)
                || !TryPoints(f[4], priceDiv, out var close))
            {
                skipped++;
                continue;
            }

            int min = low;
            int max = high;
            int avg = (int)Math.Round(((long)open + high + low + close) / 4.0, MidpointRounding.AwayFromZero);
            db.WriteMinute(symbol, timeUtc, min, max, avg, avgApprox: true);
            written++;

            if (written % 50000 == 0)
                log?.Invoke($"imported {written} minutes...");
        }

        if (skipped > 0)
            log?.Invoke($"skipped {skipped} malformed lines");
        return written;
    }

    private static bool TryParseUtc(string s, out DateTime utc) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private static bool TryPoints(string s, int priceDiv, out int points)
    {
        points = 0;
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var price))
            return false;
        var scaled = Math.Round(price * PriceScale / priceDiv, MidpointRounding.AwayFromZero);
        if (scaled < int.MinValue || scaled > int.MaxValue)
            return false;
        points = (int)scaled;
        return true;
    }
}
