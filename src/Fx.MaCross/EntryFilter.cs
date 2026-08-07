using System.Globalization;

namespace Fx.MaCross;

internal static class EntryFilter
{
    public static void Analyze(History h, long[] prefix, Options opt, Result best, List<Sweep.Trade> trades,
        int ma3Period, string csvPath)
    {
        int m = trades.Count;
        if (m == 0) return;

        var first = new short[m];
        var net = new short[m];
        var bothSl = new bool[m];
        for (int k = 0; k < m; k++)
        {
            first[k] = (short)trades[k].FirstTenths;
            net[k] = (short)trades[k].Tenths;
            bothSl[k] = !trades[k].BuyWin && !trades[k].SellWin;
        }

        double perTenth = best.RiskBp / 10000.0 / (best.SlPips * 10);
        double keep = 1 - opt.MaxDdPercent / 100;
        double pip = opt.PipPoints;

        var rangeSum = new long[h.Count + 1];
        for (int i = 0; i < h.Count; i++) rangeSum[i + 1] = rangeSum[i] + (h.Hi[i] - h.Lo[i]);

        var predictors = new List<(string Name, double[] Values)>
        {
            ($"|MA3({ma3Period / 1440}d) - cross level|, pips",
                Build(m, k => Math.Abs(Sma(prefix, trades[k].EntryIndex, ma3Period) - CrossLevel(prefix, trades, k, best)) / pip)),
            ("mean bar range over 1h, pips",
                Build(m, k => MeanRange(rangeSum, trades[k].EntryIndex, 60) / pip)),
            ("mean bar range over 1 day, pips",
                Build(m, k => MeanRange(rangeSum, trades[k].EntryIndex, 1440) / pip)),
            ("high-low range over 1 day, pips",
                Build(m, k => Span(h, trades[k].EntryIndex, 1440) / pip)),
            ("high-low range over 1 week, pips",
                Build(m, k => Span(h, trades[k].EntryIndex, 7200) / pip)),
        };

        Console.WriteLine();
        Console.WriteLine($"===== ENTRY FILTER: A1={best.W1} A2={best.W2} SL={best.SlPips} TP={best.TpPips} " +
                          $"risk={best.RiskPercent:F2}% =====");
        Console.WriteLine($"Both legs are kept. {m:N0} entries, of which {bothSl.Count(x => x):N0} " +
                          $"({(double)bothSl.Count(x => x) / m:P1}) end with both stops hit and cost " +
                          $"{2 * best.SlPips} pips each.");
        Console.WriteLine($"Baseline without any filter: {opt.Deposit * best.Equity:N0} final, " +
                          $"{best.MaxDdPercent:F1}% max drawdown.");

        using var csv = new StreamWriter(csvPath);
        csv.WriteLine("predictor,decile,from,to,entries,bothSlShare,pipsPerEntry");

        foreach (var (name, values) in predictors)
        {
            var order = Enumerable.Range(0, m).OrderBy(k => values[k]).ToArray();

            Console.WriteLine();
            Console.WriteLine($"--- {name} ---");
            Console.WriteLine("decile        from        to  entries  bothSL%  pips/entry");
            for (int d = 0; d < 10; d++)
            {
                int lo = d * m / 10;
                int hi = (d + 1) * m / 10;
                int count = hi - lo;
                int sl = 0;
                long tenths = 0;
                for (int j = lo; j < hi; j++)
                {
                    if (bothSl[order[j]]) sl++;
                    tenths += net[order[j]];
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{d + 1,5}  {values[order[lo]],10:F1}  {values[order[hi - 1]],8:F1}  {count,7:N0}  " +
                    $"{(double)sl / count,7:P1}  {tenths / 10.0 / count,10:F2}"));
                csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{name},{d + 1},{values[order[lo]]:F2},{values[order[hi - 1]]:F2},{count}," +
                    $"{(double)sl / count:F4},{tenths / 10.0 / count:F3}"));
            }

            Console.WriteLine("cut   keep deciles >= cut              keep deciles < cut");
            Console.WriteLine("      entries      final    maxDD      entries      final    maxDD");
            for (int cut = 2; cut <= 9; cut++)
            {
                double bound = values[order[(cut - 1) * m / 10]];
                var high = Money(values, k => values[k] >= bound);
                var low = Money(values, k => values[k] < bound);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{cut,4}  {high.Entries,7:N0}  {high.Final,9:N0}  {high.MaxDd,6:F1}%   " +
                    $"  {low.Entries,7:N0}  {low.Final,9:N0}  {low.MaxDd,6:F1}%"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Decile table: {csvPath}");
        Console.WriteLine("A filter only helps if bothSL% falls steadily across the deciles. " +
                          "One odd bucket is noise.");

        (int Entries, double Final, double MaxDd) Money(double[] values, Func<int, bool> accept)
        {
            var f = new short[m];
            var nt = new short[m];
            int count = 0;
            for (int k = 0; k < m; k++)
            {
                if (!accept(k)) continue;
                f[count] = first[k];
                nt[count] = net[k];
                count++;
            }

            Sweep.RunEquity(f, nt, count, perTenth, keep, out double equity, out double dd);
            return (count, opt.Deposit * equity, dd * 100);
        }
    }

    private static double[] Build(int m, Func<int, double> f)
    {
        var values = new double[m];
        for (int k = 0; k < m; k++) values[k] = f(k);
        return values;
    }

    private static double CrossLevel(long[] prefix, List<Sweep.Trade> trades, int k, Result best) =>
        (Sma(prefix, trades[k].EntryIndex, best.W1) + Sma(prefix, trades[k].EntryIndex, best.W2)) / 2;

    private static double Sma(long[] prefix, int i, int window)
    {
        int lo = Math.Max(0, i - window + 1);
        return (double)(prefix[i + 1] - prefix[lo]) / (i - lo + 1);
    }

    private static double MeanRange(long[] rangeSum, int i, int window)
    {
        int lo = Math.Max(0, i - window + 1);
        return (double)(rangeSum[i + 1] - rangeSum[lo]) / (i - lo + 1);
    }

    private static double Span(History h, int i, int window)
    {
        int lo = Math.Max(0, i - window + 1);
        int high = int.MinValue;
        int low = int.MaxValue;
        for (int j = lo; j <= i; j++)
        {
            if (h.Hi[j] > high) high = h.Hi[j];
            if (h.Lo[j] < low) low = h.Lo[j];
        }

        return high - low;
    }
}
