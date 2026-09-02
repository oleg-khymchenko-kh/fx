namespace FXViewer.Storage;

public static class AskViewRule
{
    private static readonly Dictionary<string, int> Pairs = new(StringComparer.OrdinalIgnoreCase);

    public static bool Show;

    public static void SetPairs(IEnumerable<(string Symbol, int PipPoints)> pairs)
    {
        Pairs.Clear();
        foreach (var (symbol, pipPoints) in pairs) Pairs[symbol.Trim()] = pipPoints;
    }

    public static bool Applies(string symbol) => Show && Pairs.ContainsKey(symbol.Trim());

    private static int PointsOf(int tenths, int pipPoints) => (tenths * pipPoints + 5) / 10;

    public static int TickShift(string symbol, int spreadTenths) =>
        spreadTenths <= 0 || !Show || !Pairs.TryGetValue(symbol.Trim(), out int pipPoints)
            ? 0
            : PointsOf(spreadTenths, pipPoints);

    public static bool TryShift(string symbol, bool hasSpread, int spreadCode, out int points)
    {
        points = 0;
        if (!Show || !Pairs.TryGetValue(symbol.Trim(), out int pipPoints)) return true;
        if (!hasSpread) return false;
        points = PointsOf(SpreadCodes.ToTenths(spreadCode), pipPoints);
        return true;
    }

    public static List<Candle> ToAsk(List<Candle> candles, string symbol)
    {
        if (!Show || !Pairs.TryGetValue(symbol.Trim(), out int pipPoints)) return candles;
        var result = new List<Candle>(candles.Count);
        foreach (var c in candles)
        {
            if (!c.HasSpread) continue;
            int shift = PointsOf(SpreadCodes.ToTenths(c.SpreadCode), pipPoints);
            result.Add(c with { Min = c.Min + shift, Max = c.Max + shift, Avg = c.Avg + shift });
        }
        return result;
    }
}
