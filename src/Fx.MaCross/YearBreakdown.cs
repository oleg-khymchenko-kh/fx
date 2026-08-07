namespace Fx.MaCross;

internal sealed class YearRow
{
    public int Key;
    public string Label = "";
    public double Start;
    public double End;
    public double MaxDd;
    public long Tenths;
    public int Entries;
    public int Wins;

    public double Pips => Tenths / 10.0;
    public double ReturnPercent => Start <= 0 ? 0 : (End / Start - 1) * 100;
    public double WinRate => Entries == 0 ? 0 : Wins / (2.0 * Entries);
}

internal static class YearBreakdown
{
    public static List<YearRow> Build(History h, double deposit, List<Sweep.Trade> trades, bool byMonth = false)
    {
        var rows = new List<YearRow>();
        YearRow? current = null;
        double equity = deposit;
        double peak = deposit;

        foreach (var t in trades)
        {
            var opened = DateTimeOffset.FromUnixTimeSeconds(h.Ts[t.EntryIndex]).UtcDateTime;
            int key = byMonth ? opened.Year * 12 + opened.Month : opened.Year;
            if (current is null || current.Key != key)
            {
                current = new YearRow
                {
                    Key = key,
                    Label = byMonth ? $"{opened:yyyy-MM}" : $"{opened.Year}",
                    Start = equity,
                    End = equity,
                };
                rows.Add(current);
            }

            double mid = deposit * t.FirstEquity;
            if (mid > peak) peak = mid;
            else current.MaxDd = Math.Max(current.MaxDd, (1 - mid / peak) * 100);

            equity = deposit * t.Equity;
            if (equity > peak) peak = equity;
            else current.MaxDd = Math.Max(current.MaxDd, (1 - equity / peak) * 100);

            current.End = equity;
            current.Tenths += t.Tenths;
            current.Entries++;
            current.Wins += (t.BuyWin ? 1 : 0) + (t.SellWin ? 1 : 0);
        }

        return rows;
    }
}
