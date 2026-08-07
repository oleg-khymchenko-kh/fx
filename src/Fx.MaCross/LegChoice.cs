using System.Globalization;

namespace Fx.MaCross;

internal static class LegChoice
{
    private static readonly int[] TrendPeriods = { 1440, 2880, 4320, 7200, 14400, 28800 };
    private static readonly int[] MomentumLags = { 30, 120, 480, 1440, 4320, 7200 };
    private static readonly int[] RangeWindows = { 240, 1440, 4320, 7200, 14400, 28800 };

    private sealed class Score
    {
        public int Decided;
        public int Hits;
        public double Rate => Decided == 0 ? 0 : (double)Hits / Decided;
    }

    public static void Analyze(History h, long[] prefix, Options opt, Result best, List<Sweep.Trade> trades,
        int splitYear)
    {
        int m = trades.Count;
        if (m == 0) return;

        var entry = new int[m];
        var winner = new sbyte[m];
        var year = new int[m];
        int decided = 0;
        for (int k = 0; k < m; k++)
        {
            var t = trades[k];
            entry[k] = t.EntryIndex;
            winner[k] = (sbyte)(t.BuyWin == t.SellWin ? 0 : t.BuyWin ? 1 : -1);
            year[k] = DateTimeOffset.FromUnixTimeSeconds(h.Ts[t.EntryIndex]).UtcDateTime.Year;
            if (winner[k] != 0) decided++;
        }

        int tp = best.TpPips * 10 - opt.SpreadTenths;
        int sl = best.SlPips * 10 + opt.SpreadTenths;
        long bothLegs = trades.Sum(t => (long)t.Tenths);
        double matchRate = (bothLegs + (double)(m - decided) * sl) / decided / (tp + sl) + (double)sl / (tp + sl);

        Console.WriteLine();
        Console.WriteLine($"========== LEG CHOICE: A1={best.W1} A2={best.W2} SL={best.SlPips} TP={best.TpPips} ==========");
        Console.WriteLine($"{decided:N0} of {m:N0} entries have a winner; on the other {m - decided:N0} both legs " +
                          "lose and the choice changes nothing.");
        Console.WriteLine($"Hit rate needed to match taking both legs: {matchRate:P2}. " +
                          $"Coin flip noise on {decided:N0} samples is +-{100 * Math.Sqrt(0.25 / decided):F2} " +
                          "points per sigma.");
        Console.WriteLine();
        Console.WriteLine($"criterion            param      all           ..{splitYear - 1}        {splitYear}..        " +
                          "pips     vs both");

        Report("cross direction", "-", CrossDirection(prefix, entry, best.W1, best.W2));

        foreach (int period in TrendPeriods)
            Report("price vs SMA", Label(period), Trend(prefix, h.Avg, entry, period));

        foreach (int lag in MomentumLags)
            Report("momentum", Label(lag), Momentum(h.Avg, entry, lag));

        foreach (int window in RangeWindows)
            Report("range position", Label(window), RangePosition(h, entry, window));

        Console.WriteLine();
        Console.WriteLine("A rate below 50% is the same signal reversed: subtract it from 100%.");
        Console.WriteLine($"Both legs give {bothLegs / 10.0:N0} pips. " +
                          $"A perfect choice would give {(decided * (double)tp - (m - decided) * (double)sl) / 10:N0}.");

        void Report(string name, string param, sbyte[] dirs)
        {
            var all = new Score();
            var inSample = new Score();
            var outSample = new Score();
            long tenths = 0;

            for (int k = 0; k < m; k++)
            {
                if (winner[k] == 0)
                {
                    tenths -= sl;
                    continue;
                }

                bool hit = dirs[k] == winner[k];
                tenths += hit ? tp : -sl;
                Add(all, hit);
                Add(year[k] < splitYear ? inSample : outSample, hit);
            }

            double pips = tenths / 10.0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name,-20} {param,-8}  {all.Rate,6:P2} {all.Decided,6:N0}  {inSample.Rate,6:P2} {inSample.Decided,5:N0}  " +
                $"{outSample.Rate,6:P2} {outSample.Decided,5:N0}  {pips,8:N0}  " +
                $"{pips - bothLegs / 10.0,8:+#,##0,-#,##0,0}"));
        }

        static void Add(Score s, bool hit)
        {
            s.Decided++;
            if (hit) s.Hits++;
        }
    }

    public static void ThirdMa(History h, long[] prefix, Options opt, Result best, List<Sweep.Trade> trades,
        int splitYear, int[] periods, string csvPath)
    {
        int m = trades.Count;
        if (m == 0) return;

        var entry = new int[m];
        var winner = new sbyte[m];
        var year = new int[m];
        int decided = 0;
        for (int k = 0; k < m; k++)
        {
            entry[k] = trades[k].EntryIndex;
            winner[k] = (sbyte)(trades[k].BuyWin == trades[k].SellWin ? 0 : trades[k].BuyWin ? 1 : -1);
            year[k] = DateTimeOffset.FromUnixTimeSeconds(h.Ts[entry[k]]).UtcDateTime.Year;
            if (winner[k] != 0) decided++;
        }

        int tp = best.TpPips * 10 - opt.SpreadTenths;
        int sl = best.SlPips * 10 + opt.SpreadTenths;
        long bothLegs = trades.Sum(t => (long)t.Tenths);
        double matchRate = (bothLegs + (double)m * sl) / decided / (tp + sl);
        double sigma = 100 * Math.Sqrt(0.25 / decided);

        double perTenth = best.RiskBp / 10000.0 / (best.SlPips * 10);
        double keep = 1 - opt.MaxDdPercent / 100;
        var leg = new short[m];
        var rows = new List<(int Period, Outcome Price, Outcome Cross)>();

        var crossLevel = new double[m];
        for (int k = 0; k < m; k++)
            crossLevel[k] = (Sma(prefix, entry[k], best.W1) + Sma(prefix, entry[k], best.W2)) / 2;

        foreach (int period in periods)
        {
            var vsPrice = new sbyte[m];
            var vsCross = new sbyte[m];
            for (int k = 0; k < m; k++)
            {
                double ma3 = Sma(prefix, entry[k], period);
                vsPrice[k] = (sbyte)(ma3 > h.Avg[entry[k]] ? 1 : -1);
                vsCross[k] = (sbyte)(ma3 > crossLevel[k] ? 1 : -1);
            }

            rows.Add((period,
                Rate(vsPrice, winner, year, splitYear, tp, sl, m, decided, perTenth, keep, leg),
                Rate(vsCross, winner, year, splitYear, tp, sl, m, decided, perTenth, keep, leg)));
        }

        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("periodMinutes,periodLabel,vsPriceAll,vsPriceIn,vsPriceOut,vsPricePips,vsPriceEquity," +
                        "vsPriceMaxDd,vsCrossAll,vsCrossIn,vsCrossOut,vsCrossPips,vsCrossEquity,vsCrossMaxDd");
            foreach (var (period, price, cross) in rows)
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{period},{Label(period)},{price.All:F4},{price.In:F4},{price.Out:F4},{price.Pips:F0}," +
                    $"{opt.Deposit * price.Equity:F0},{price.MaxDd * 100:F2}," +
                    $"{cross.All:F4},{cross.In:F4},{cross.Out:F4},{cross.Pips:F0}," +
                    $"{opt.Deposit * cross.Equity:F0},{cross.MaxDd * 100:F2}"));
        }

        Console.WriteLine();
        Console.WriteLine($"===== THIRD MA as direction filter: A1={best.W1} A2={best.W2} " +
                          $"SL={best.SlPips} TP={best.TpPips} =====");
        Console.WriteLine($"{decided:N0} of {m:N0} entries have a winner. Rule \"MA3 above -> BUY\"; " +
                          "the reversed rule is 100% minus the shown rate.");
        Console.WriteLine($"Need > {matchRate:P2} to beat taking both legs. One sigma of a coin flip " +
                          $"is {sigma:F2} points, and {periods.Length} periods x 2 references are tested " +
                          "two-sided, so a believable hit needs a plateau, not a single spike.");
        Console.WriteLine($"Third MA grid: {periods.Length} values {Label(periods[0])}..{Label(periods[^1])}");

        Best(" MA3 vs entry price", rows.Select(r => (r.Period, r.Price)));
        Best(" MA3 vs the MA1/MA2 crossing level", rows.Select(r => (r.Period, r.Cross)));

        Console.WriteLine();
        Console.WriteLine($"Full curve: {csvPath}");
        Console.WriteLine($"Both legs: {bothLegs / 10.0:N0} pips, {opt.Deposit * best.Equity:N0} final, " +
                          $"{best.MaxDdPercent:F1}% max drawdown.");

        void Best(string title, IEnumerable<(int Period, Outcome O)> src)
        {
            var list = src.ToList();
            Console.WriteLine();
            Console.WriteLine($"{title}: range {list.Min(r => r.O.All):P2} .. {list.Max(r => r.O.All):P2}, " +
                              $"biggest deviation {list.Max(r => Math.Abs(r.O.All - 0.5)) * 100:F2} points " +
                              $"= {list.Max(r => Math.Abs(r.O.All - 0.5)) * 100 / sigma:F2} sigma");
            Console.WriteLine("    period      all      ..2019    2020..      pips       final    maxDD   rule");
            foreach (var (period, o) in list.OrderByDescending(r => Math.Abs(r.O.All - 0.5)).Take(8))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    {Label(period),-8}  {o.All,6:P2}   {o.In,6:P2}   {o.Out,6:P2}  {o.Pips,8:N0}  " +
                    $"{opt.Deposit * o.Equity,10:N0}  {o.MaxDd * 100,5:F1}%   " +
                    $"{(o.All >= 0.5 ? "above->BUY" : "above->SELL")}{(o.Alive ? "" : "  BLOWN UP")}"));
        }
    }

    private static double Sma(long[] prefix, int i, int window)
    {
        int lo = Math.Max(0, i - window + 1);
        return (double)(prefix[i + 1] - prefix[lo]) / (i - lo + 1);
    }

    private readonly record struct Outcome(double All, double In, double Out, double Pips, double Equity,
        double MaxDd, bool Alive);

    private static Outcome Rate(sbyte[] dirs, sbyte[] winner, int[] year, int splitYear, int tp, int sl,
        int m, int decided, double perTenth, double keep, short[] leg)
    {
        int hits = 0, inHits = 0, inCount = 0, outHits = 0, outCount = 0;
        long tenths = 0;
        for (int k = 0; k < m; k++)
        {
            bool hit = winner[k] != 0 && dirs[k] == winner[k];
            leg[k] = (short)(hit ? tp : -sl);
            tenths += leg[k];
            if (winner[k] == 0) continue;

            if (hit) hits++;
            if (year[k] < splitYear)
            {
                inCount++;
                if (hit) inHits++;
            }
            else
            {
                outCount++;
                if (hit) outHits++;
            }
        }

        bool alive = Sweep.RunEquity(leg, leg, m, perTenth, keep, out double equity, out double maxDd);
        return new Outcome((double)hits / decided,
            inCount == 0 ? 0 : (double)inHits / inCount,
            outCount == 0 ? 0 : (double)outHits / outCount,
            tenths / 10.0, equity, maxDd, alive);
    }

    private static sbyte[] CrossDirection(long[] prefix, int[] entry, int w1, int w2)
    {
        var dirs = new sbyte[entry.Length];
        for (int k = 0; k < entry.Length; k++)
            dirs[k] = (sbyte)(Sweep.FastAboveSlow(prefix, entry[k], w1, w2) ? 1 : -1);
        return dirs;
    }

    private static sbyte[] Trend(long[] prefix, int[] avg, int[] entry, int period)
    {
        var dirs = new sbyte[entry.Length];
        for (int k = 0; k < entry.Length; k++)
        {
            int i = entry[k];
            int lo = Math.Max(0, i - period + 1);
            double sma = (double)(prefix[i + 1] - prefix[lo]) / (i - lo + 1);
            dirs[k] = (sbyte)(avg[i] >= sma ? 1 : -1);
        }

        return dirs;
    }

    private static sbyte[] Momentum(int[] avg, int[] entry, int lag)
    {
        var dirs = new sbyte[entry.Length];
        for (int k = 0; k < entry.Length; k++)
        {
            int i = entry[k];
            dirs[k] = (sbyte)(avg[i] >= avg[Math.Max(0, i - lag)] ? 1 : -1);
        }

        return dirs;
    }

    private static sbyte[] RangePosition(History h, int[] entry, int window)
    {
        var dirs = new sbyte[entry.Length];
        for (int k = 0; k < entry.Length; k++)
        {
            int i = entry[k];
            int lo = Math.Max(0, i - window + 1);
            int high = int.MinValue;
            int low = int.MaxValue;
            for (int j = lo; j <= i; j++)
            {
                if (h.Hi[j] > high) high = h.Hi[j];
                if (h.Lo[j] < low) low = h.Lo[j];
            }

            dirs[k] = (sbyte)(2L * h.Avg[i] >= (long)high + low ? 1 : -1);
        }

        return dirs;
    }

    private static string Label(int minutes) => minutes switch
    {
        < 60 => $"{minutes}m",
        < 1440 => $"{minutes / 60}h",
        _ => $"{minutes / 1440.0:0.##}d",
    };
}
