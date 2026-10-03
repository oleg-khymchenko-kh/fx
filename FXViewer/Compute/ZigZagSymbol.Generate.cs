using FXViewer.Storage;

namespace FXViewer.Compute;

public static partial class ZigZagSymbol
{
    public static int Generate(
        CandleDatabase db, string sourceSymbol, string targetSymbol, ZigZagLimits limits,
        Action<string>? log = null, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var years = db.ExistingYears(sourceSymbol);
        if (years.Count == 0)
        {
            db.DeleteSymbol(targetSymbol);
            ZigZagStore.Save(db.SymbolDirectory(targetSymbol), Array.Empty<PivotPoint>());
            log?.Invoke($"{sourceSymbol}: no data, {targetSymbol} cleared");
            progress?.Report(1.0);
            return 0;
        }
        var from = new DateTime(years[0], 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(years[^1], 12, 31, 23, 59, 0, DateTimeKind.Utc);
        var minutes = db.ReadRange(sourceSymbol, from, to);
        var points = BuildPoints(minutes, limits, ct, progress);
        db.DeleteSymbol(targetSymbol);
        ZigZagStore.Save(db.SymbolDirectory(targetSymbol), points);
        progress?.Report(1.0);
        log?.Invoke($"{targetSymbol}: {points.Count:N0} points over {minutes.Count:N0} minutes");
        return points.Count;
    }
}
