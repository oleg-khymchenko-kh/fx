using System.Diagnostics;
using System.Globalization;

namespace Fx.MaCross;

internal readonly record struct SwingResult(
    int XPips, int YPips, int SlPips, int TpPips, int RiskBp, double Equity, double MaxDd,
    long Tenths, int Entries, int Wins, int MirrorWins) : IScored
{
    public double Score => Equity;
    public double RiskPercent => RiskBp / 100.0;
    public double ReturnPercent => (Equity - 1) * 100;
    public double MaxDdPercent => MaxDd * 100;
    public double Pips => Tenths / 10.0;
    public int Fails => Entries - Wins - MirrorWins;
    public double WinRate => Entries == 0 ? 0 : (double)Wins / Entries;
}

internal static class SwingSweep
{
    public static int Run(Options opt, History h, int[] slGrid, int[] tpGrid, int[] riskGrid)
    {
        int n = h.Count;
        var sw = Stopwatch.StartNew();
        var blocks = BlockIndex.Build(h, n);
        Console.WriteLine($"Block index ready in {sw.Elapsed.TotalSeconds:F1}s ({blocks.Blocks:N0} blocks)");

        var xGrid = opt.BuildStopGrid(opt.XMin, opt.XMax, opt.XStep);
        var yGrid = opt.BuildStopGrid(opt.YMin, opt.YMax, opt.YStep);
        var xyPairs = new List<(int X, int Y)>();
        foreach (int x in xGrid)
        foreach (int y in yGrid)
            if (x < y && (opt.XyGap <= 0 || y - x == opt.XyGap))
                xyPairs.Add((x, y));

        if (xyPairs.Count == 0)
        {
            Console.Error.WriteLine("No X < Y pair inside the given grids.");
            return 1;
        }

        int stopCombos = 0;
        foreach (int sl in slGrid)
        foreach (int tp in tpGrid)
            if (opt.RrMax <= 0 || tp <= opt.RrMax * sl)
                stopCombos++;
        long sequences = (long)xyPairs.Count * stopCombos;
        double keep = 1 - opt.MaxDdPercent / 100;

        Console.WriteLine();
        Console.WriteLine("Swing mode: an anchor sits at a bar's average price. The price must run at");
        Console.WriteLine("least X but at most Y pips to one side of the anchor (the largest excursion");
        Console.WriteLine("so far is P), then turn and run 2*P the other way, i.e. touch anchor-P after");
        Console.WriteLine("a move up or anchor+P after a move down. That touch enters ONE trade at that");
        Console.WriteLine("exact level, in the direction of the turn, SL and TP from the entry level.");
        Console.WriteLine("A bar beyond anchor+-Y moves the anchor to that bar and clears the extremes,");
        Console.WriteLine("after the trade closes its exit bar becomes the next anchor. The mirror");
        Console.WriteLine("column counts entries where the OPPOSITE trade would have won instead.");
        Console.WriteLine($"X grid:       {xGrid.Length} values {xGrid[0]}..{xGrid[^1]} pips");
        Console.WriteLine($"Y grid:       {yGrid.Length} values {yGrid[0]}..{yGrid[^1]} pips " +
                          $"({xyPairs.Count} pairs with X < Y)");
        Console.WriteLine($"StopLoss:     {slGrid.Length} values {opt.SlMin}..{opt.SlMax} step {opt.SlStep} pips");
        Console.WriteLine($"TakeProfit:   {tpGrid.Length} values {opt.TpMin}..{opt.TpMax} step {opt.TpStep} pips");
        if (opt.RrMax > 0)
            Console.WriteLine($"TP cap:       at most {opt.RrMax:0.##} x SL ({stopCombos} SL/TP combos kept)");
        Console.WriteLine($"Risk/trade:   {riskGrid.Length} values {riskGrid[0] / 100.0:F2}%..{riskGrid[^1] / 100.0:F2}% of equity");
        Console.WriteLine($"Deposit:      {opt.Deposit:N0}, dropped when equity falls " +
                          $"{opt.MaxDdPercent:F0}% below its own peak");
        Console.WriteLine($"Spread cost:  {opt.SpreadTenths / 10.0:F1} pips per trade");
        Console.WriteLine($"Filters:      at least {opt.MinEntries:N0} entries per result");
        Console.WriteLine($"Threads:      {opt.Threads}");
        Console.WriteLine($"Combinations: {sequences * riskGrid.Length:N0} " +
                          $"({sequences:N0} trade sequences x {riskGrid.Length} sizes)");
        Console.WriteLine();
        Console.WriteLine("Running swing sweep...");
        sw.Restart();

        int nextXy = -1;
        int done = 0;
        long entriesTotal = 0;
        long blownUp = 0;
        var tops = new TopList<SwingResult>[opt.Threads];
        var workers = new Task[opt.Threads];
        for (int t = 0; t < opt.Threads; t++)
        {
            int slot = t;
            workers[slot] = Task.Run(() =>
            {
                var top = new TopList<SwingResult>(opt.TopKeep);
                tops[slot] = top;
                var legs = new short[1 << 16];
                long localEntries = 0;
                long localBlown = 0;
                var hi = h.Hi;
                var lo = h.Lo;
                var avg = h.Avg;

                while (true)
                {
                    int p = Interlocked.Increment(ref nextXy);
                    if (p >= xyPairs.Count) break;
                    var (x, y) = xyPairs[p];
                    int xp = x * opt.PipPoints;
                    int yp = y * opt.PipPoints;

                    for (int ti = 0; ti < tpGrid.Length; ti++)
                    for (int si = 0; si < slGrid.Length; si++)
                    {
                        if (opt.RrMax > 0 && tpGrid[ti] > opt.RrMax * slGrid[si]) continue;
                        int slP = slGrid[si] * opt.PipPoints;
                        int tpP = tpGrid[ti] * opt.PipPoints;
                        int slT = slGrid[si] * 10;
                        int tpT = tpGrid[ti] * 10;
                        int entries = 0;
                        long tenths = 0;
                        int wins = 0;
                        int mirrorWins = 0;
                        int anchor = avg[0];
                        int upExt = 0, dnExt = 0;

                        for (int j = 1; j < n; j++)
                        {
                            int hj = hi[j], lj = lo[j];
                            bool sellTrig = upExt >= xp && lj <= anchor - upExt;
                            bool buyTrig = dnExt >= xp && hj >= anchor + dnExt;
                            if (sellTrig || buyTrig)
                            {
                                if (sellTrig && buyTrig)
                                {
                                    anchor = avg[j];
                                    upExt = dnExt = 0;
                                    continue;
                                }

                                long level = sellTrig ? anchor - upExt : anchor + dnExt;
                                int tpHit, slHit, mirrorTp, mirrorSl;
                                if (sellTrig)
                                {
                                    tpHit = BandSweep.FirstLow(lo, blocks, n, j + 1, level - tpP);
                                    slHit = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + slP);
                                    mirrorTp = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + tpP);
                                    mirrorSl = BandSweep.FirstLow(lo, blocks, n, j + 1, level - slP);
                                }
                                else
                                {
                                    tpHit = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + tpP);
                                    slHit = BandSweep.FirstLow(lo, blocks, n, j + 1, level - slP);
                                    mirrorTp = BandSweep.FirstLow(lo, blocks, n, j + 1, level - tpP);
                                    mirrorSl = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + slP);
                                }

                                int exit = Math.Min(tpHit, slHit);
                                if (exit >= n) break;
                                bool win = tpHit < slHit;
                                int pnl = (win ? tpT : -slT) - opt.SpreadTenths;
                                if (entries == legs.Length) Array.Resize(ref legs, legs.Length * 2);
                                legs[entries] = (short)pnl;
                                entries++;
                                tenths += pnl;
                                if (win) wins++;
                                else if (mirrorTp < mirrorSl) mirrorWins++;

                                anchor = avg[exit];
                                upExt = dnExt = 0;
                                j = exit;
                                continue;
                            }

                            if (hj > anchor + yp || lj < anchor - yp)
                            {
                                anchor = avg[j];
                                upExt = dnExt = 0;
                                continue;
                            }

                            if (hj - anchor > upExt) upExt = hj - anchor;
                            if (anchor - lj > dnExt) dnExt = anchor - lj;
                        }

                        localEntries += entries;
                        if (entries < opt.MinEntries) continue;

                        var best = default(SwingResult);
                        bool any = false;
                        foreach (int riskBp in riskGrid)
                        {
                            double perTenth = riskBp / 10000.0 / slT;
                            if (!Sweep.RunEquity(legs, legs, entries, perTenth, keep,
                                    out double eq, out double dd))
                            {
                                localBlown++;
                                continue;
                            }

                            if (any && eq <= best.Equity) continue;
                            any = true;
                            best = new SwingResult(x, y, slGrid[si], tpGrid[ti], riskBp,
                                eq, dd, tenths, entries, wins, mirrorWins);
                        }

                        if (any) top.Offer(best);
                    }

                    Interlocked.Increment(ref done);
                }

                Interlocked.Add(ref entriesTotal, localEntries);
                Interlocked.Add(ref blownUp, localBlown);
            });
        }

        var all = Task.WhenAll(workers);
        while (!all.Wait(5000))
        {
            int dn = Volatile.Read(ref done);
            double share = (double)dn / xyPairs.Count;
            double eta = share > 0.001 ? sw.Elapsed.TotalSeconds * (1 - share) / share : 0;
            Console.WriteLine($"  {dn:N0}/{xyPairs.Count:N0} X/Y pairs ({share:P1})  " +
                              $"elapsed {sw.Elapsed.TotalSeconds:F0}s  eta {eta:F0}s");
        }

        if (all.IsFaulted)
        {
            Console.Error.WriteLine(all.Exception?.GetBaseException().ToString());
            return 1;
        }

        Console.WriteLine($"Swing sweep done in {sw.Elapsed.TotalSeconds:F1}s " +
                          $"({entriesTotal:N0} simulated entries, {blownUp:N0} blown up)");

        var best2 = tops.Where(v => v is not null).SelectMany(v => v.Items)
            .OrderByDescending(r => r.Equity)
            .Take(opt.TopKeep)
            .ToList();

        if (best2.Count == 0)
        {
            Console.Error.WriteLine("Nothing passed the filters.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"==================== TOP {Math.Min(opt.TopPrint, best2.Count)} ====================");
        Console.WriteLine("  #     X     Y    SL    TP   risk      deposit        final       return   maxDD  entries        pips   win%        d/m/f");
        for (int i = 0; i < Math.Min(opt.TopPrint, best2.Count); i++)
        {
            var r = best2[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}  {r.XPips,4}  {r.YPips,4}  {r.SlPips,4}  {r.TpPips,4}  {r.RiskPercent,4:F2}%  " +
                $"{opt.Deposit,11:N0}  {opt.Deposit * r.Equity,11:N0}  {r.ReturnPercent,10:N0}%  " +
                $"{r.MaxDdPercent,5:F1}%  {r.Entries,7:N0}  {r.Pips,10:N0}  {r.WinRate,5:P1}  " +
                $"{r.Wins,5}/{r.MirrorWins}/{r.Fails}"));
        }

        PrintYearBreakdown(opt, h, blocks, n, best2[0]);

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);
        string csvPath = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-swing-top.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("rank,xPips,yPips,stopLossPips,takeProfitPips,riskPercent,deposit,finalEquity," +
                        "returnPercent,maxDrawdownPercent,entries,pips,winRate,directWins,mirrorWins,bothLose");
            for (int i = 0; i < best2.Count; i++)
            {
                var r = best2[i];
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{r.XPips},{r.YPips},{r.SlPips},{r.TpPips},{r.RiskPercent:F2},{opt.Deposit:F0}," +
                    $"{opt.Deposit * r.Equity:F0},{r.ReturnPercent:F1},{r.MaxDdPercent:F2},{r.Entries}," +
                    $"{r.Pips:F1},{r.WinRate:F4},{r.Wins},{r.MirrorWins},{r.Fails}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Top CSV:    {csvPath} ({best2.Count} rows)");
        return 0;
    }

    private static void PrintYearBreakdown(Options opt, History h, BlockIndex blocks, int n, SwingResult r)
    {
        var hi = h.Hi;
        var lo = h.Lo;
        var avg = h.Avg;
        int xp = r.XPips * opt.PipPoints;
        int yp = r.YPips * opt.PipPoints;
        int slP = r.SlPips * opt.PipPoints;
        int tpP = r.TpPips * opt.PipPoints;
        int slT = r.SlPips * 10;
        int tpT = r.TpPips * 10;
        var years = new SortedDictionary<int, (int Entries, long Tenths, int Wins, int Mirrors)>();

        int anchor = avg[0];
        int upExt = 0, dnExt = 0;
        for (int j = 1; j < n; j++)
        {
            int hj = hi[j], lj = lo[j];
            bool sellTrig = upExt >= xp && lj <= anchor - upExt;
            bool buyTrig = dnExt >= xp && hj >= anchor + dnExt;
            if (sellTrig || buyTrig)
            {
                if (sellTrig && buyTrig)
                {
                    anchor = avg[j];
                    upExt = dnExt = 0;
                    continue;
                }

                long level = sellTrig ? anchor - upExt : anchor + dnExt;
                int tpHit, slHit, mirrorTp, mirrorSl;
                if (sellTrig)
                {
                    tpHit = BandSweep.FirstLow(lo, blocks, n, j + 1, level - tpP);
                    slHit = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + slP);
                    mirrorTp = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + tpP);
                    mirrorSl = BandSweep.FirstLow(lo, blocks, n, j + 1, level - slP);
                }
                else
                {
                    tpHit = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + tpP);
                    slHit = BandSweep.FirstLow(lo, blocks, n, j + 1, level - slP);
                    mirrorTp = BandSweep.FirstLow(lo, blocks, n, j + 1, level - tpP);
                    mirrorSl = BandSweep.FirstHigh(hi, blocks, n, j + 1, level + slP);
                }

                int exit = Math.Min(tpHit, slHit);
                if (exit >= n) break;
                bool win = tpHit < slHit;
                int pnl = (win ? tpT : -slT) - opt.SpreadTenths;
                int year = h.TimeUtc(j).Year;
                years.TryGetValue(year, out var acc);
                years[year] = (acc.Entries + 1, acc.Tenths + pnl,
                    acc.Wins + (win ? 1 : 0),
                    acc.Mirrors + (!win && mirrorTp < mirrorSl ? 1 : 0));

                anchor = avg[exit];
                upExt = dnExt = 0;
                j = exit;
                continue;
            }

            if (hj > anchor + yp || lj < anchor - yp)
            {
                anchor = avg[j];
                upExt = dnExt = 0;
                continue;
            }

            if (hj - anchor > upExt) upExt = hj - anchor;
            if (anchor - lj > dnExt) dnExt = anchor - lj;
        }

        Console.WriteLine();
        Console.WriteLine($"============ BY YEAR: X={r.XPips} Y={r.YPips} SL={r.SlPips} TP={r.TpPips} " +
                          $"risk={r.RiskPercent:F2}% ============");
        Console.WriteLine("year     entries      pips   direct  mirror   fail   direct%");
        foreach (var (year, acc) in years)
        {
            int fails = acc.Entries - acc.Wins - acc.Mirrors;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{year,-7}  {acc.Entries,6:N0}  {acc.Tenths / 10.0,8:N0}  {acc.Wins,7}  {acc.Mirrors,6}  " +
                $"{fails,5}  {(acc.Entries == 0 ? 0 : 100.0 * acc.Wins / acc.Entries),7:F1}%"));
        }
    }
}
