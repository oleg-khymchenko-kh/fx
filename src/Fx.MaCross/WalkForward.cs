using System.Globalization;

namespace Fx.MaCross;

internal sealed record WalkRow(
    string Month, string FitFrom, string FitTo, string MonthFrom, string MonthTo, int Rank, BandResult Fit,
    double FitMoney, double FitPlusMonthMoney, double MonthMoney, double LastFitMonthMoney, double FitDd,
    int FitEntries, int MonthEntries, int MonthWins, int MonthMirrorWins);

internal static class WalkForward
{
    public static int Run(Options opt, History h, BlockIndex blocks, int n, int[] distPips,
        (string Label, int Start, int End)[] fits, (int Start, int End)[] tests, int[] lastFitMonths,
        List<BandResult>[] perWindow, string outDir)
    {
        var distPoints = distPips.Select(p => p * opt.PipPoints).ToArray();
        int maxReach = Math.Max(1, opt.DistMax) * opt.PipPoints;
        var prefix = BuildPrefix(h, n);
        var inner = new BandEvents();
        var outer = new BandEvents();
        var legs = new short[1 << 16];
        var rows = new List<WalkRow>();

        int dropped = 0;
        int considered = 0;
        for (int w = 0; w < fits.Length; w++)
        {
            var windowRows = new List<WalkRow>();
            var picks = perWindow[w].Take(opt.PrevMonthPositive ? opt.TopKeep : opt.TopPrint).ToList();
            var (fitStart, fitEnd) = (fits[w].Start, fits[w].End);
            int testEnd = tests[w].End;

            foreach (var group in picks.Select((r, i) => (Rank: i + 1, Result: r))
                         .GroupBy(x => (x.Result.Period, x.Result.BandPips)))
            {
                BandSweep.FindEvents(prefix, h, blocks, n, group.Key.Period,
                    group.Key.BandPips * opt.PipPoints, maxReach, inner, outer);
                BandSweep.GatherHits(h, blocks, n, distPoints, inner);
                BandSweep.GatherHits(h, blocks, n, distPoints, outer);

                foreach (var (rank, r) in group)
                {
                    var events = r.Variant is 'A' or 'D' ? inner : outer;
                    bool flip = r.Variant is 'C' or 'D';
                    int slIdx = Array.IndexOf(distPips, r.SlPips);
                    int tpIdx = Array.IndexOf(distPips, r.TpPips);
                    int tpTenths = r.TpPips * 10 - opt.SpreadTenths;
                    int slTenths = r.SlPips * 10 + opt.SpreadTenths;
                    int minReach = r.DistancePips * opt.PipPoints;
                    double perTenth = r.RiskBp / 10000.0 / (r.SlPips * 10);

                    var fitSeq = BandSweep.BuildSequence(events, n, distPoints.Length, flip, minReach,
                        slIdx, tpIdx, tpTenths, slTenths, fitStart, fitEnd, ref legs);
                    Sweep.RunEquity(legs, legs, fitSeq.Entries, perTenth, 0, out double fitEq, out double fitDd);

                    var bothSeq = BandSweep.BuildSequence(events, n, distPoints.Length, flip, minReach,
                        slIdx, tpIdx, tpTenths, slTenths, fitStart, testEnd, ref legs);
                    Sweep.RunEquity(legs, legs, bothSeq.Entries, perTenth, 0, out double bothEq, out _);

                    var preSeq = BandSweep.BuildSequence(events, n, distPoints.Length, flip, minReach,
                        slIdx, tpIdx, tpTenths, slTenths, fitStart, lastFitMonths[w], ref legs);
                    Sweep.RunEquity(legs, legs, preSeq.Entries, perTenth, 0, out double preEq, out _);

                    windowRows.Add(new WalkRow(
                        fits[w].Label,
                        $"{h.TimeUtc(fitStart):yyyy-MM-dd}",
                        $"{h.TimeUtc(fitEnd - 1):yyyy-MM-dd}",
                        $"{h.TimeUtc(tests[w].Start):yyyy-MM-dd}",
                        $"{h.TimeUtc(Math.Min(testEnd, n) - 1):yyyy-MM-dd}",
                        rank, r,
                        opt.Deposit * (fitEq - 1),
                        opt.Deposit * (bothEq - 1),
                        opt.Deposit * (bothEq - fitEq),
                        opt.Deposit * (fitEq - preEq),
                        fitDd * 100,
                        fitSeq.Entries,
                        bothSeq.Entries - fitSeq.Entries,
                        bothSeq.Wins - fitSeq.Wins,
                        bothSeq.MirrorWins - fitSeq.MirrorWins));
                }
            }

            considered += windowRows.Count;
            var kept = windowRows.Where(r => !opt.PrevMonthPositive || r.LastFitMonthMoney > 0)
                .OrderByDescending(r => r.FitMoney)
                .Take(opt.TopPrint)
                .ToList();
            dropped += windowRows.Count - windowRows.Count(r => r.LastFitMonthMoney > 0);
            for (int i = 0; i < kept.Count; i++) rows.Add(kept[i] with { Rank = i + 1 });
        }

        if (opt.PrevMonthPositive)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"\nFilter: only candidates whose last fitting month made money. {considered:N0} candidates looked at, {dropped:N0} dropped ({(double)dropped / considered:P1})."));

        Print(opt, rows);
        string path = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-walk-forward.csv");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("month,fitFrom,fitTo,monthFrom,monthTo,rank,variant,maMinutes,bandPips," +
                             "distancePips,stopLossPips,takeProfitPips,riskPercent,drawdownFitYear," +
                             "moneyFitYear,moneyLastFitMonth,moneyFitYearPlusMonth,moneyMonth," +
                             "entriesFitYear,entriesMonth,winsMonth,mirrorWinsMonth,bothLoseMonth");
            foreach (var r in rows)
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{r.Month},{r.FitFrom},{r.FitTo},{r.MonthFrom},{r.MonthTo},{r.Rank},{r.Fit.Variant},{r.Fit.Period}," +
                    $"{r.Fit.BandPips},{r.Fit.DistancePips},{r.Fit.SlPips},{r.Fit.TpPips}," +
                    $"{r.Fit.RiskPercent:F4},{r.FitDd:F2},{r.FitMoney:F0},{r.LastFitMonthMoney:F0}," +
                    $"{r.FitPlusMonthMoney:F0},{r.MonthMoney:F0}," +
                    $"{r.FitEntries},{r.MonthEntries},{r.MonthWins},{r.MonthMirrorWins}," +
                    $"{r.MonthEntries - r.MonthWins - r.MonthMirrorWins}"));
        }

        Console.WriteLine();
        Console.WriteLine($"Walk-forward CSV: {path} ({rows.Count} rows)");
        return 0;
    }

    private static void Print(Options opt, List<WalkRow> rows)
    {
        foreach (var month in rows.GroupBy(r => r.Month))
        {
            var first = month.First();
            Console.WriteLine();
            Console.WriteLine($"======== {month.Key}: picked on {first.FitFrom}..{first.FitTo}, " +
                              $"applied to {first.MonthFrom}..{first.MonthTo} ========");
            Console.WriteLine("  #  var   MA period  band  dist    SL    TP    risk   ddYear        $ year  $ prev month   $ year+month      $ month  entries   direct / mirror / both");
            foreach (var r in month.OrderBy(x => x.Rank))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{r.Rank,3}    {r.Fit.Variant}  {BandSweep.Label(r.Fit.Period),10}  {r.Fit.BandPips,4}  " +
                    $"{r.Fit.DistancePips,4}  {r.Fit.SlPips,4}  {r.Fit.TpPips,4}  {r.Fit.RiskPercent,5:F3}%  " +
                    $"{r.FitDd,5:F1}%  {r.FitMoney,12:N0}  {r.LastFitMonthMoney,12:N0}  " +
                    $"{r.FitPlusMonthMoney,12:N0}  {r.MonthMoney,11:N0}  " +
                    $"{r.FitEntries,7:N0}  {r.MonthWins,8} / {r.MonthMirrorWins} / " +
                    $"{r.MonthEntries - r.MonthWins - r.MonthMirrorWins}"));

            double sum = month.Sum(x => x.MonthMoney);
            int up = month.Count(x => x.MonthMoney > 0);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"     month total {sum,12:N0} over {month.Count()} picks, {up} of them positive, " +
                $"average {sum / month.Count(),10:N0}"));
        }

        Console.WriteLine();
        Console.WriteLine("==================== WALK-FORWARD SUMMARY ====================");
        Console.WriteLine("month     picks  positive   best month   worst month  average month   equal-weight $");
        double running = opt.Deposit;
        foreach (var month in rows.GroupBy(r => r.Month))
        {
            var list = month.ToList();
            double average = list.Average(x => x.MonthMoney);
            running *= 1 + average / opt.Deposit;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{month.Key,-7}  {list.Count,5}  {list.Count(x => x.MonthMoney > 0),8}  " +
                $"{list.Max(x => x.MonthMoney),11:N0}  {list.Min(x => x.MonthMoney),12:N0}  " +
                $"{average,13:N0}  {running,14:N0}"));
        }

        int total = rows.Count;
        int positive = rows.Count(x => x.MonthMoney > 0);
        Console.WriteLine();
        Console.WriteLine($"Out of {total} month applications, {positive} made money ({(double)positive / total:P1}).");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Equal-weight portfolio of the {opt.TopPrint} picks, rebalanced every month: {opt.Deposit:N0} -> {running:N0}."));
        Console.WriteLine("Each pick is measured on a month the optimiser had not seen when it chose the parameters.");
    }

    private static long[] BuildPrefix(History h, int n)
    {
        var prefix = new long[n + 1];
        long acc = 0;
        for (int i = 0; i < n; i++)
        {
            acc += h.Avg[i];
            prefix[i + 1] = acc;
        }

        return prefix;
    }
}
