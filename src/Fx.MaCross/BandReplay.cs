using System.Globalization;

namespace Fx.MaCross;

internal static class BandReplay
{
    private sealed record Row(int Rank, char Variant, int Period, int Band, int Distance, int Sl, int Tp,
        int RiskBp, double InReturn, double InWinRate, int InEntries);

    public static int Run(Options opt, History h, long[] prefix, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Replay file not found: {path}");
            return 1;
        }

        var rows = new List<Row>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var c = line.Split(';', ',');
            if (c.Length < 15) continue;
            rows.Add(new Row(
                int.Parse(c[0], CultureInfo.InvariantCulture),
                c[1][0],
                int.Parse(c[2], CultureInfo.InvariantCulture),
                int.Parse(c[3], CultureInfo.InvariantCulture),
                int.Parse(c[4], CultureInfo.InvariantCulture),
                int.Parse(c[5], CultureInfo.InvariantCulture),
                int.Parse(c[6], CultureInfo.InvariantCulture),
                (int)Math.Round(double.Parse(c[7], CultureInfo.InvariantCulture) * 100),
                double.Parse(c[10], CultureInfo.InvariantCulture),
                double.Parse(c[14], CultureInfo.InvariantCulture) * 100,
                int.Parse(c[12], CultureInfo.InvariantCulture)));
        }

        if (rows.Count == 0)
        {
            Console.Error.WriteLine("Replay file has no rows.");
            return 1;
        }

        int n = h.Count;
        var blocks = BlockIndex.Build(h, n);
        var distPips = rows.SelectMany(r => new[] { r.Sl, r.Tp }).Distinct().Order().ToArray();
        var distPoints = distPips.Select(p => p * opt.PipPoints).ToArray();
        int maxReach = Math.Max(1, rows.Max(r => r.Distance)) * opt.PipPoints;
        double keep = 1 - opt.MaxDdPercent / 100;

        Console.WriteLine();
        Console.WriteLine($"Replaying {rows.Count:N0} combinations on {h.TimeUtc(0):yyyy-MM-dd} .. " +
                          $"{h.TimeUtc(n - 1):yyyy-MM-dd}, spread {opt.SpreadTenths / 10.0:F1} pips per trade");

        var results = new List<(Row Row, double Return, double WinRate, double MaxDd, int Entries, bool Alive)>();
        var legs = new short[1 << 16];
        var inner = new BandEvents();
        var outer = new BandEvents();

        foreach (var group in rows.GroupBy(r => (r.Period, r.Band)))
        {
            if (group.Key.Period >= n) continue;
            FindEvents(prefix, h, blocks, n, group.Key.Period, group.Key.Band * opt.PipPoints,
                maxReach, inner, outer);
            BandSweep.GatherHits(h, blocks, n, distPoints, inner);
            BandSweep.GatherHits(h, blocks, n, distPoints, outer);

            foreach (var r in group)
            {
                var events = r.Variant is 'A' or 'D' ? inner : outer;
                bool flip = r.Variant is 'C' or 'D';
                var seq = BandSweep.BuildSequence(events, n, distPoints.Length, flip,
                    r.Distance * opt.PipPoints, Array.IndexOf(distPips, r.Sl), Array.IndexOf(distPips, r.Tp),
                    r.Tp * 10 - opt.SpreadTenths, r.Sl * 10 + opt.SpreadTenths, 0, n, ref legs);
                if (seq.Entries == 0) continue;

                double perTenth = r.RiskBp / 10000.0 / (r.Sl * 10);
                bool alive = Sweep.RunEquity(legs, legs, seq.Entries, perTenth, keep,
                    out double equity, out double dd);
                results.Add((r, (equity - 1) * 100, 100.0 * seq.Wins / seq.Entries, dd * 100,
                    seq.Entries, alive));
            }
        }

        Console.WriteLine();
        Console.WriteLine("  #  var   MA period  band  dist    SL    TP    in-sample        out-of-sample");
        Console.WriteLine("                                                return  win%    return  win%  break-even  entries  maxDD");
        foreach (var (r, ret, win, dd, entries, alive) in results.Take(20))
        {
            double breakEven = 100.0 * (r.Sl * 10 + opt.SpreadTenths) /
                               (r.Sl * 10 + opt.SpreadTenths + r.Tp * 10 - opt.SpreadTenths);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Rank,3}    {r.Variant}  {BandSweep.Label(r.Period),10}  {r.Band,4}  {r.Distance,4}  {r.Sl,4}  " +
                $"{r.Tp,4}  {r.InReturn,9:N0}%  {r.InWinRate,5:F1}  {ret,9:N0}%  {win,5:F1}  {breakEven,9:F1}  " +
                $"{entries,7:N0}  {dd,5:F1}%{(alive ? "" : "  BLOWN")}"));
        }

        int profitable = results.Count(x => x.Return > 0);
        int aboveBreakEven = results.Count(x =>
            x.WinRate > 100.0 * (x.Row.Sl * 10 + opt.SpreadTenths) /
            (x.Row.Sl * 10 + opt.SpreadTenths + x.Row.Tp * 10 - opt.SpreadTenths));
        var ordered = results.Select(x => x.Return).Order().ToList();

        Console.WriteLine();
        Console.WriteLine($"Out of {results.Count:N0} combinations picked on the first half:");
        Console.WriteLine($"  profitable out of sample:      {profitable:N0} ({(double)profitable / results.Count:P1})");
        Console.WriteLine($"  win rate above break-even:     {aboveBreakEven:N0} ({(double)aboveBreakEven / results.Count:P1})");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  return: median {ordered[ordered.Count / 2],9:N0}%, best {ordered[^1]:N0}%, worst {ordered[0]:N0}%"));
        Console.WriteLine($"  survived the {opt.MaxDdPercent:F0}% drawdown rule: {results.Count(x => x.Alive):N0}");
        return 0;
    }

    private static void FindEvents(long[] prefix, History h, BlockIndex blocks, int n, int period,
        int bandPoints, int maxReach, BandEvents inner, BandEvents outer) =>
        BandSweep.FindEvents(prefix, h, blocks, n, period, bandPoints, maxReach, inner, outer);
}
