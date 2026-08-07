using System.Diagnostics;
using System.Globalization;

namespace Fx.MaCross;

internal readonly record struct ReversalResult(
    int W1, int W2, int XPips, int SlPips, int TpPips, int RiskBp, double Equity, double MaxDd,
    long Tenths, int Entries, int Wins, int DblWin = 0, int DblLoss = 0) : IScored
{
    public double Score => Equity;
    public double RiskPercent => RiskBp / 100.0;
    public double ReturnPercent => (Equity - 1) * 100;
    public double MaxDdPercent => MaxDd * 100;
    public double Pips => Tenths / 10.0;
    public double WinRate => Entries == 0 ? 0 : (double)Wins / Entries;
    public int Mixed => Entries - DblWin - DblLoss;
    public double LegWinRate => Entries == 0 ? 0 : (2.0 * DblWin + Mixed) / (2.0 * Entries);
}

internal static class ReversalSweep
{
    public static int Run(Options opt, History h, long[] prefix, List<(int W1, int W2)> pairs,
        int[] slGrid, int[] tpGrid, int[] riskGrid,
        (string Label, int Start, int End)[] fitWindows, (int Start, int End)[] testWindows,
        int[] lastFitMonths)
    {
        int n = h.Count;
        var sw = Stopwatch.StartNew();
        var blocks = BlockIndex.Build(h, n);
        Console.WriteLine($"Block index ready in {sw.Elapsed.TotalSeconds:F1}s ({blocks.Blocks:N0} blocks)");

        var xGrid = opt.BuildStopGrid(opt.XMin, opt.XMax, opt.XStep);
        var distPips = slGrid.Concat(tpGrid).Distinct().Order().ToArray();
        var distPoints = distPips.Select(p => p * opt.PipPoints).ToArray();
        var slIdx = slGrid.Select(p => Array.IndexOf(distPips, p)).ToArray();
        var tpIdx = tpGrid.Select(p => Array.IndexOf(distPips, p)).ToArray();
        double keep = 1 - opt.MaxDdPercent / 100;
        int windowCount = fitWindows.Length;
        bool walk = testWindows.Length > 0;
        int stopCombos = 0;
        foreach (int sl in slGrid)
        foreach (int tp in tpGrid)
            if (opt.RrMax <= 0 || tp <= opt.RrMax * sl)
                stopCombos++;
        long runs = (long)pairs.Count * xGrid.Length * stopCombos * riskGrid.Length * windowCount;

        Console.WriteLine();
        Console.WriteLine("Reversal mode: a crossover anchors at that bar's average price. Pending orders");
        Console.WriteLine("wait at anchor +- X pips; the first touch enters at that exact level with SL");
        Console.WriteLine("and TP measured from it. Orders die at the next crossover. One entry at a");
        Console.WriteLine("time; a crossover during an open trade arms nothing.");
        Console.WriteLine(opt.ReversalBoth
            ? "Entry: a BUY and a SELL at once at the touched level; next entry after both close."
            : "Entry: one trade AGAINST the move.");
        Console.WriteLine($"X grid:       {xGrid.Length} values {xGrid[0]}..{xGrid[^1]} pips");
        if (opt.RrMax > 0) Console.WriteLine($"TP cap:       at most {opt.RrMax:0.##} x SL ({stopCombos} SL/TP combos kept)");
        if (walk)
        {
            Console.WriteLine($"Walk-forward: {windowCount} months ({fitWindows[0].Label}..{fitWindows[^1].Label}), " +
                              $"fit on the {opt.WalkForwardMonths} months before each, " +
                              $"at least {opt.MinEntries} entries per fit window");
        }

        Console.WriteLine($"Combinations: {runs:N0}");
        Console.WriteLine();
        Console.WriteLine("Running reversal sweep...");
        sw.Restart();

        int nextPair = -1;
        int done = 0;
        long entriesTotal = 0;
        long blownUp = 0;
        var tops = new TopList<ReversalResult>[opt.Threads][];
        var workers = new Task[opt.Threads];
        for (int t = 0; t < opt.Threads; t++)
        {
            int slot = t;
            workers[slot] = Task.Run(() =>
            {
                var wtops = new TopList<ReversalResult>[windowCount];
                for (int w = 0; w < windowCount; w++) wtops[w] = new TopList<ReversalResult>(opt.TopKeep);
                tops[slot] = wtops;
                var cross = new int[1 << 16];
                var events = new BandEvents();
                var legs = new short[1 << 16];
                var firstLegs = new short[1 << 16];
                long localEntries = 0;
                long localBlown = 0;
                int d = distPoints.Length;

                while (true)
                {
                    int p = Interlocked.Increment(ref nextPair);
                    if (p >= pairs.Count) break;
                    var (w1, w2) = pairs[p];
                    int c = Sweep.FindCrossovers(prefix, n, w1, w2, ref cross);
                    if (c < 2)
                    {
                        Interlocked.Increment(ref done);
                        continue;
                    }

                    foreach (int x in xGrid)
                    {
                        int xp = x * opt.PipPoints;
                        events.Count = 0;
                        for (int k = 0; k < c; k++)
                        {
                            int cb = cross[k];
                            int windowEnd = k + 1 < c ? cross[k + 1] : n - 1;
                            int anchor = h.Avg[cb];
                            int up = BandSweep.FirstHigh(h.Hi, blocks, n, cb + 1, anchor + xp);
                            int dn = BandSweep.FirstLow(h.Lo, blocks, n, cb + 1, anchor - xp);
                            if (up == dn) continue;
                            int fill = Math.Min(up, dn);
                            if (fill > windowEnd) continue;
                            if (up < dn) events.Add(fill, -1, anchor + xp, false);
                            else events.Add(fill, +1, anchor - xp, true);
                            events.Reach[events.Count - 1] = cb;
                        }

                        if (events.Count == 0) continue;
                        BandSweep.GatherHits(h, blocks, n, distPoints, events);

                        for (int ti = 0; ti < tpGrid.Length; ti++)
                        for (int si = 0; si < slGrid.Length; si++)
                        {
                            if (opt.RrMax > 0 && tpGrid[ti] > opt.RrMax * slGrid[si]) continue;
                            int slT = slGrid[si] * 10;
                            int tpT = tpGrid[ti] * 10;

                            for (int w = 0; w < windowCount; w++)
                            {
                                int winStart = fitWindows[w].Start;
                                int winEnd = fitWindows[w].End;
                                int entries = 0;
                                long tenths = 0;
                                int wins = 0;
                                int dblWin = 0;
                                int dblLoss = 0;
                                int nextAllowed = 0;
                                int k0 = LowerBoundIndex(events, winStart);
                                for (int k = k0; k < events.Count; k++)
                                {
                                    int fill = events.Index[k];
                                    if (fill >= winEnd) break;
                                    if (events.Reach[k] < nextAllowed) continue;
                                    int row = k * 2 * d;
                                    int exit;
                                    int pnl;
                                    int firstPnl;
                                    if (opt.ReversalBoth)
                                    {
                                        int upTpH = events.Hits[row + tpIdx[ti]];
                                        int dnSlH = events.Hits[row + d + slIdx[si]];
                                        int dnTpH = events.Hits[row + d + tpIdx[ti]];
                                        int upSlH = events.Hits[row + slIdx[si]];
                                        bool buyWin = upTpH < dnSlH;
                                        bool sellWin = dnTpH < upSlH;
                                        int buyExit = buyWin ? upTpH : dnSlH;
                                        int sellExit = sellWin ? dnTpH : upSlH;
                                        exit = Math.Max(buyExit, sellExit);
                                        if (exit >= n) break;
                                        int buyPnl = (buyWin ? tpT : -slT) - opt.SpreadTenths;
                                        int sellPnl = (sellWin ? tpT : -slT) - opt.SpreadTenths;
                                        pnl = buyPnl + sellPnl;
                                        firstPnl = buyExit == sellExit ? pnl
                                            : buyExit < sellExit ? buyPnl : sellPnl;
                                        if (buyWin && sellWin) dblWin++;
                                        else if (!buyWin && !sellWin) dblLoss++;
                                        wins += (buyWin ? 1 : 0) + (sellWin ? 1 : 0);
                                    }
                                    else
                                    {
                                        int tpHit, slHit;
                                        if (events.Dir[k] < 0)
                                        {
                                            tpHit = events.Hits[row + d + tpIdx[ti]];
                                            slHit = events.Hits[row + slIdx[si]];
                                        }
                                        else
                                        {
                                            tpHit = events.Hits[row + tpIdx[ti]];
                                            slHit = events.Hits[row + d + slIdx[si]];
                                        }

                                        exit = Math.Min(tpHit, slHit);
                                        if (exit >= n) break;
                                        bool win = tpHit < slHit;
                                        pnl = (win ? tpT : -slT) - opt.SpreadTenths;
                                        firstPnl = pnl;
                                        if (win) wins++;
                                    }

                                    if (entries == legs.Length)
                                    {
                                        Array.Resize(ref legs, legs.Length * 2);
                                        Array.Resize(ref firstLegs, firstLegs.Length * 2);
                                    }

                                    legs[entries] = (short)pnl;
                                    firstLegs[entries] = (short)firstPnl;
                                    entries++;
                                    tenths += pnl;
                                    nextAllowed = exit + 1;
                                }

                                localEntries += entries;
                                if (entries < opt.MinEntries) continue;

                                var best = default(ReversalResult);
                                bool any = false;
                                foreach (int riskBp in riskGrid)
                                {
                                    double perTenth = riskBp / 10000.0 / slT;
                                    if (!Sweep.RunEquity(firstLegs, legs, entries, perTenth, keep,
                                            out double eq, out double dd))
                                    {
                                        localBlown++;
                                        continue;
                                    }

                                    if (any && eq <= best.Equity) continue;
                                    any = true;
                                    best = new ReversalResult(w1, w2, x, slGrid[si], tpGrid[ti], riskBp,
                                        eq, dd, tenths, entries, wins, dblWin, dblLoss);
                                }

                                if (any) wtops[w].Offer(best);
                            }
                        }
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
            double share = (double)dn / pairs.Count;
            double eta = share > 0.001 ? sw.Elapsed.TotalSeconds * (1 - share) / share : 0;
            Console.WriteLine($"  {dn:N0}/{pairs.Count:N0} pairs ({share:P1})  " +
                              $"elapsed {sw.Elapsed.TotalSeconds:F0}s  eta {eta:F0}s");
        }

        if (all.IsFaulted)
        {
            Console.Error.WriteLine(all.Exception?.GetBaseException().ToString());
            return 1;
        }

        Console.WriteLine($"Reversal sweep done in {sw.Elapsed.TotalSeconds:F1}s " +
                          $"({entriesTotal:N0} simulated entries, {blownUp:N0} blown up)");

        var perWindow = new List<ReversalResult>[windowCount];
        for (int w = 0; w < windowCount; w++)
        {
            perWindow[w] = tops.Where(x => x is not null).Select(x => x[w]).SelectMany(x => x.Items)
                .OrderByDescending(r => r.Equity)
                .Take(opt.TopKeep)
                .ToList();
        }

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);

        if (walk)
            return WalkReport(opt, h, blocks, prefix, n, perWindow, fitWindows, testWindows, lastFitMonths, outDir);

        var bestList = perWindow[0];
        if (bestList.Count == 0)
        {
            Console.Error.WriteLine("Nothing passed the filters.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"==================== TOP {Math.Min(opt.TopPrint, bestList.Count)} ====================");
        string tail = opt.ReversalBoth ? "  legWin%      2w/mix/2l" : "   win%";
        Console.WriteLine("  #       A1       A2     X    SL    TP   risk      deposit        final       return   maxDD  entries        pips" + tail);
        for (int i = 0; i < Math.Min(opt.TopPrint, bestList.Count); i++)
        {
            var r = bestList[i];
            string ratePart = opt.ReversalBoth
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{r.LegWinRate,7:P1}  {r.DblWin,5}/{r.Mixed}/{r.DblLoss}")
                : string.Create(CultureInfo.InvariantCulture, $"{r.WinRate,6:P1}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}  {BandSweep.Label(r.W1),7}  {BandSweep.Label(r.W2),7}  {r.XPips,4}  {r.SlPips,4}  " +
                $"{r.TpPips,4}  {r.RiskPercent,4:F2}%  {opt.Deposit,11:N0}  {opt.Deposit * r.Equity,11:N0}  " +
                $"{r.ReturnPercent,10:N0}%  {r.MaxDdPercent,5:F1}%  {r.Entries,7:N0}  {r.Pips,10:N0}  {ratePart}"));
        }

        string csvPath = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-reversal-top.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("rank,a1Minutes,a2Minutes,xPips,stopLossPips,takeProfitPips,riskPercent,deposit," +
                        "finalEquity,returnPercent,maxDrawdownPercent,entries,pips,winRate,wins," +
                        "doubleWin,mixed,doubleLoss");
            for (int i = 0; i < bestList.Count; i++)
            {
                var r = bestList[i];
                double rate = opt.ReversalBoth ? r.LegWinRate : r.WinRate;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{r.W1},{r.W2},{r.XPips},{r.SlPips},{r.TpPips},{r.RiskPercent:F2},{opt.Deposit:F0}," +
                    $"{opt.Deposit * r.Equity:F0},{r.ReturnPercent:F1},{r.MaxDdPercent:F2},{r.Entries}," +
                    $"{r.Pips:F1},{rate:F4},{r.Wins},{r.DblWin},{r.Mixed},{r.DblLoss}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Top CSV:    {csvPath} ({bestList.Count} rows)");
        return 0;
    }

    private readonly record struct SimResult(double Money, long Tenths, int Entries, int Wins, int MirrorWins);

    private static int WalkReport(Options opt, History h, BlockIndex blocks, long[] prefix, int n,
        List<ReversalResult>[] perWindow, (string Label, int Start, int End)[] fitWindows,
        (int Start, int End)[] testWindows, int[] lastFitMonths, string outDir)
    {
        var rows = new List<(int W, int Rank, ReversalResult R, SimResult Month, SimResult LastFit)>();
        var picks = new List<(int W, int Rank, ReversalResult R)>();
        for (int w = 0; w < perWindow.Length; w++)
            for (int i = 0; i < Math.Min(opt.TopPrint, perWindow[w].Count); i++)
                picks.Add((w, i + 1, perWindow[w][i]));

        var cross = new int[1 << 16];
        var events = new BandEvents();
        foreach (var pairGroup in picks.GroupBy(p => (p.R.W1, p.R.W2)))
        {
            int c = Sweep.FindCrossovers(prefix, n, pairGroup.Key.W1, pairGroup.Key.W2, ref cross);
            foreach (var xGroup in pairGroup.GroupBy(p => p.R.XPips))
            {
                int xp = xGroup.Key * opt.PipPoints;
                events.Count = 0;
                for (int k = 0; k < c; k++)
                {
                    int cb = cross[k];
                    int windowEnd = k + 1 < c ? cross[k + 1] : n - 1;
                    int anchor = h.Avg[cb];
                    int up = BandSweep.FirstHigh(h.Hi, blocks, n, cb + 1, anchor + xp);
                    int dn = BandSweep.FirstLow(h.Lo, blocks, n, cb + 1, anchor - xp);
                    if (up == dn) continue;
                    int fill = Math.Min(up, dn);
                    if (fill > windowEnd) continue;
                    if (up < dn) events.Add(fill, -1, anchor + xp, false);
                    else events.Add(fill, +1, anchor - xp, true);
                    events.Reach[events.Count - 1] = cb;
                }

                foreach (var (w, rank, r) in xGroup)
                {
                    var month = Sim(opt, h, blocks, n, events, testWindows[w].Start, testWindows[w].End, r);
                    var lastFit = Sim(opt, h, blocks, n, events, lastFitMonths[w], fitWindows[w].End, r);
                    rows.Add((w, rank, r, month, lastFit));
                }
            }
        }

        rows.Sort((a, b) => a.W != b.W ? a.W - b.W : a.Rank - b.Rank);

        Console.WriteLine();
        Console.WriteLine("=========== WALK-FORWARD, top picks of every month ===========");
        Console.WriteLine("d/m/f = direct wins / mirror wins / both lose in the forecast month");
        foreach (var group in rows.GroupBy(row => row.W))
        {
            Console.WriteLine();
            Console.WriteLine($"---- {fitWindows[group.Key].Label} (fit {fitWindows[group.Key].Label} minus " +
                              $"{opt.WalkForwardMonths} months) ----");
            Console.WriteLine("  #      A1       A2     X    SL    TP   risk       fitYear$   prevMonth$       month$      d/m/f   real%    be%");
            foreach (var row in group)
            {
                var r = row.R;
                double be = 100.0 * (r.SlPips * 10 + opt.SpreadTenths) / (r.SlPips * 10 + r.TpPips * 10);
                string real = row.Month.Entries == 0
                    ? "-"
                    : string.Create(CultureInfo.InvariantCulture, $"{100.0 * row.Month.Wins / row.Month.Entries:F1}");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{row.Rank,3}  {BandSweep.Label(r.W1),6}  {BandSweep.Label(r.W2),7}  {r.XPips,4}  {r.SlPips,4}  " +
                    $"{r.TpPips,4}  {r.RiskPercent,4:F2}%  {opt.Deposit * (r.Equity - 1),13:N0}  " +
                    $"{row.LastFit.Money,11:N0}  {row.Month.Money,11:N0}  " +
                    $"{row.Month.Wins,3}/{row.Month.MirrorWins}/{row.Month.Entries - row.Month.Wins - row.Month.MirrorWins,-3}  " +
                    $"{real,6}  {be,5:F1}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("=========== TOTALS PER RANK (sum of month results, fresh deposit each month) ===========");
        Console.WriteLine("rank    months  positive       totalMoney      totalPips  entries  direct  mirror   fail");
        for (int rank = 1; rank <= opt.TopPrint; rank++)
        {
            var ofRank = rows.Where(r => r.Rank == rank).ToList();
            if (ofRank.Count == 0) continue;
            int e = ofRank.Sum(r => r.Month.Entries);
            int dw = ofRank.Sum(r => r.Month.Wins);
            int mw = ofRank.Sum(r => r.Month.MirrorWins);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{rank,4}  {ofRank.Count,8}  {ofRank.Count(r => r.Month.Money > 0),8}  " +
                $"{ofRank.Sum(r => r.Month.Money),15:N0}  {ofRank.Sum(r => r.Month.Tenths) / 10.0,13:N0}  " +
                $"{e,7}  {dw,6}  {mw,6}  {e - dw - mw,5}"));
        }

        string csvPath = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-reversal-walk.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("month,rank,a1Minutes,a2Minutes,xPips,stopLossPips,takeProfitPips,riskPercent," +
                        "fitYearMoney,fitEntries,fitWinRate,prevMonthMoney,monthMoney,monthPips,monthEntries," +
                        "monthDirectWins,monthMirrorWins,monthBothLose,monthDirectPct,breakEvenPct");
            foreach (var row in rows)
            {
                var r = row.R;
                double be = 100.0 * (r.SlPips * 10 + opt.SpreadTenths) / (r.SlPips * 10 + r.TpPips * 10);
                double real = row.Month.Entries == 0 ? 0 : 100.0 * row.Month.Wins / row.Month.Entries;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{fitWindows[row.W].Label},{row.Rank},{r.W1},{r.W2},{r.XPips},{r.SlPips},{r.TpPips}," +
                    $"{r.RiskPercent:F2},{opt.Deposit * (r.Equity - 1):F0},{r.Entries},{r.WinRate:F4}," +
                    $"{row.LastFit.Money:F0},{row.Month.Money:F0},{row.Month.Tenths / 10.0:F1},{row.Month.Entries}," +
                    $"{row.Month.Wins},{row.Month.MirrorWins}," +
                    $"{row.Month.Entries - row.Month.Wins - row.Month.MirrorWins},{real:F1},{be:F1}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Walk CSV:   {csvPath} ({rows.Count} rows)");
        return 0;
    }

    private static SimResult Sim(Options opt, History h, BlockIndex blocks, int n, BandEvents events,
        int from, int to, ReversalResult r)
    {
        int slT = r.SlPips * 10;
        int tpT = r.TpPips * 10;
        int slP = r.SlPips * opt.PipPoints;
        int tpP = r.TpPips * opt.PipPoints;
        double perTenth = r.RiskBp / 10000.0 / slT;
        double equity = 1;
        int entries = 0, wins = 0, mirrorWins = 0;
        long tenths = 0;
        int nextAllowed = 0;
        for (int k = LowerBoundIndex(events, from); k < events.Count; k++)
        {
            int fill = events.Index[k];
            if (fill >= to) break;
            if (events.Reach[k] < nextAllowed) continue;
            long level = events.Level[k];
            int tpHit, slHit, mirrorTp, mirrorSl;
            if (events.Dir[k] < 0)
            {
                tpHit = BandSweep.FirstLow(h.Lo, blocks, n, fill + 1, level - tpP);
                slHit = BandSweep.FirstHigh(h.Hi, blocks, n, fill + 1, level + slP);
                mirrorTp = BandSweep.FirstHigh(h.Hi, blocks, n, fill + 1, level + tpP);
                mirrorSl = BandSweep.FirstLow(h.Lo, blocks, n, fill + 1, level - slP);
            }
            else
            {
                tpHit = BandSweep.FirstHigh(h.Hi, blocks, n, fill + 1, level + tpP);
                slHit = BandSweep.FirstLow(h.Lo, blocks, n, fill + 1, level - slP);
                mirrorTp = BandSweep.FirstLow(h.Lo, blocks, n, fill + 1, level - tpP);
                mirrorSl = BandSweep.FirstHigh(h.Hi, blocks, n, fill + 1, level + slP);
            }

            int exit;
            int pnl;
            bool win = tpHit < slHit;
            bool mirrorWin = mirrorTp < mirrorSl;
            if (opt.ReversalBoth)
            {
                int directExit = win ? tpHit : slHit;
                int mirrorExit = mirrorWin ? mirrorTp : mirrorSl;
                exit = Math.Max(directExit, mirrorExit);
                if (exit >= n) break;
                pnl = (win ? tpT : -slT) + (mirrorWin ? tpT : -slT) - 2 * opt.SpreadTenths;
                if (win) wins++;
                if (mirrorWin) mirrorWins++;
            }
            else
            {
                exit = Math.Min(tpHit, slHit);
                if (exit >= n) break;
                pnl = (win ? tpT : -slT) - opt.SpreadTenths;
                if (win) wins++;
                else if (mirrorWin) mirrorWins++;
            }

            equity *= 1 + perTenth * pnl;
            entries++;
            tenths += pnl;
            nextAllowed = exit + 1;
        }

        return new SimResult(opt.Deposit * (equity - 1), tenths, entries, wins, mirrorWins);
    }

    private static int LowerBoundIndex(BandEvents events, int bar)
    {
        int lo = 0, hi = events.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (events.Index[mid] < bar) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
