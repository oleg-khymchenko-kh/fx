using System.Diagnostics;
using System.Globalization;

namespace Fx.MaCross;

internal readonly record struct ComebackResult(
    int XPips, int YPips, int SlPips, int TpPips, int RiskBp, double Equity, double MaxDd,
    long Tenths, int Entries, int Wins, int DblWin, int DblLoss) : IScored
{
    public double Score => Equity;
    public double RiskPercent => RiskBp / 100.0;
    public double ReturnPercent => (Equity - 1) * 100;
    public double MaxDdPercent => MaxDd * 100;
    public double Pips => Tenths / 10.0;
    public int Mixed => Entries - DblWin - DblLoss;
    public double LegWinRate => Entries == 0 ? 0 : (2.0 * DblWin + Mixed) / (2.0 * Entries);
}

internal static class ComebackSweep
{
    public static int Run(Options opt, History h, int[] slGrid, int[] tpGrid, int[] riskGrid,
        (string Label, int Start, int End)[] windows)
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
        int windowCount = windows.Length;
        long sequences = (long)xyPairs.Count * stopCombos * windowCount;
        double keep = 1 - opt.MaxDdPercent / 100;
        double averageBars = windows.Average(w => (double)(w.End - w.Start));
        var windowMinEntries = windows
            .Select(w => Math.Max(1, (int)Math.Round(opt.MinEntries * (w.End - w.Start) / averageBars)))
            .ToArray();

        Console.WriteLine();
        Console.WriteLine("Comeback mode: an anchor sits at a bar's average price. The price must touch");
        Console.WriteLine("anchor+X and anchor-X (either order) while staying inside anchor+-Y, X < Y.");
        Console.WriteLine("When a LATER bar comes back through the anchor price, a BUY and a SELL open");
        Console.WriteLine("there at once, same SL and TP each, measured from the anchor. When both");
        Console.WriteLine("close, the exit bar becomes the next anchor. A bar beyond anchor+-Y moves");
        Console.WriteLine("the anchor to that bar and clears the touches.");
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
        Console.WriteLine($"Spread cost:  {opt.SpreadTenths / 10.0:F1} pips per leg");
        if (windowCount > 1)
        {
            Console.WriteLine($"Windows:      {windowCount} ({windows[0].Label}..{windows[^1].Label}), " +
                              "each picked on its own, each starting from a fresh deposit and a fresh anchor,");
            Console.WriteLine("              entries only inside the window, the last exits may run past its end");
            Console.WriteLine($"Min entries:  {opt.MinEntries:N0} per average window, scaled by window length " +
                              $"({windowMinEntries.Min()}..{windowMinEntries.Max()})");
        }
        else
        {
            Console.WriteLine($"Filters:      at least {opt.MinEntries:N0} entries per result");
        }

        Console.WriteLine($"Threads:      {opt.Threads}");
        Console.WriteLine($"Combinations: {sequences * riskGrid.Length:N0} " +
                          $"({sequences:N0} trade sequences x {riskGrid.Length} sizes)");
        Console.WriteLine();
        Console.WriteLine("Running comeback sweep...");
        sw.Restart();

        int nextXy = -1;
        int done = 0;
        long entriesTotal = 0;
        long blownUp = 0;
        var tops = new TopList<ComebackResult>[opt.Threads][];
        var workers = new Task[opt.Threads];
        for (int t = 0; t < opt.Threads; t++)
        {
            int slot = t;
            workers[slot] = Task.Run(() =>
            {
                var wtops = new TopList<ComebackResult>[windowCount];
                for (int w = 0; w < windowCount; w++) wtops[w] = new TopList<ComebackResult>(opt.TopKeep);
                tops[slot] = wtops;
                var legs = new short[1 << 16];
                var firstLegs = new short[1 << 16];
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

                        for (int w = 0; w < windowCount; w++)
                        {
                            int winStart = windows[w].Start;
                            int winEnd = windows[w].End;
                            int entries = 0;
                            long tenths = 0;
                            int wins = 0;
                            int dblWin = 0;
                            int dblLoss = 0;
                            int anchor = avg[winStart];
                            int upX = anchor + xp, dnX = anchor - xp;
                            int upY = anchor + yp, dnY = anchor - yp;
                            bool touchedUp = false, touchedDn = false;

                            for (int j = winStart + 1; j < winEnd; j++)
                            {
                                int hj = hi[j], lj = lo[j];
                                if (touchedUp && touchedDn && lj <= anchor && hj >= anchor)
                                {
                                    int buyTp = BandSweep.FirstHigh(hi, blocks, n, j + 1, anchor + tpP);
                                    int buySl = BandSweep.FirstLow(lo, blocks, n, j + 1, anchor - slP);
                                    int sellTp = BandSweep.FirstLow(lo, blocks, n, j + 1, anchor - tpP);
                                    int sellSl = BandSweep.FirstHigh(hi, blocks, n, j + 1, anchor + slP);
                                    bool buyWin = buyTp < buySl;
                                    bool sellWin = sellTp < sellSl;
                                    int buyExit = buyWin ? buyTp : buySl;
                                    int sellExit = sellWin ? sellTp : sellSl;
                                    int exit = Math.Max(buyExit, sellExit);
                                    if (exit >= n) break;
                                    int buyPnl = (buyWin ? tpT : -slT) - opt.SpreadTenths;
                                    int sellPnl = (sellWin ? tpT : -slT) - opt.SpreadTenths;
                                    int pnl = buyPnl + sellPnl;
                                    if (entries == legs.Length)
                                    {
                                        Array.Resize(ref legs, legs.Length * 2);
                                        Array.Resize(ref firstLegs, firstLegs.Length * 2);
                                    }

                                    legs[entries] = (short)pnl;
                                    firstLegs[entries] = (short)(buyExit == sellExit ? pnl
                                        : buyExit < sellExit ? buyPnl : sellPnl);
                                    entries++;
                                    tenths += pnl;
                                    wins += (buyWin ? 1 : 0) + (sellWin ? 1 : 0);
                                    if (buyWin && sellWin) dblWin++;
                                    else if (!buyWin && !sellWin) dblLoss++;

                                    anchor = avg[exit];
                                    upX = anchor + xp; dnX = anchor - xp;
                                    upY = anchor + yp; dnY = anchor - yp;
                                    touchedUp = touchedDn = false;
                                    j = exit;
                                    continue;
                                }

                                if (hj > upY || lj < dnY)
                                {
                                    anchor = avg[j];
                                    upX = anchor + xp; dnX = anchor - xp;
                                    upY = anchor + yp; dnY = anchor - yp;
                                    touchedUp = touchedDn = false;
                                    continue;
                                }

                                if (hj >= upX) touchedUp = true;
                                if (lj <= dnX) touchedDn = true;
                            }

                            localEntries += entries;
                            if (entries < windowMinEntries[w]) continue;

                            var best = default(ComebackResult);
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
                                best = new ComebackResult(x, y, slGrid[si], tpGrid[ti], riskBp,
                                    eq, dd, tenths, entries, wins, dblWin, dblLoss);
                            }

                            if (any) wtops[w].Offer(best);
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

        Console.WriteLine($"Comeback sweep done in {sw.Elapsed.TotalSeconds:F1}s " +
                          $"({entriesTotal:N0} simulated entries, {blownUp:N0} blown up)");

        var perWindow = new List<ComebackResult>[windowCount];
        for (int w = 0; w < windowCount; w++)
        {
            perWindow[w] = tops.Where(v => v is not null).Select(v => v[w]).SelectMany(v => v.Items)
                .OrderByDescending(r => r.Equity)
                .Take(opt.TopKeep)
                .ToList();
        }

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);

        if (windowCount > 1)
            return WriteWindows(opt, windows, perWindow, outDir);

        var best2 = perWindow[0];
        if (best2.Count == 0)
        {
            Console.Error.WriteLine("Nothing passed the filters.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"==================== TOP {Math.Min(opt.TopPrint, best2.Count)} ====================");
        Console.WriteLine("  #     X     Y    SL    TP   risk      deposit        final       return   maxDD  entries        pips  legWin%      2w/mix/2l");
        for (int i = 0; i < Math.Min(opt.TopPrint, best2.Count); i++)
        {
            var r = best2[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}  {r.XPips,4}  {r.YPips,4}  {r.SlPips,4}  {r.TpPips,4}  {r.RiskPercent,4:F2}%  " +
                $"{opt.Deposit,11:N0}  {opt.Deposit * r.Equity,11:N0}  {r.ReturnPercent,10:N0}%  " +
                $"{r.MaxDdPercent,5:F1}%  {r.Entries,7:N0}  {r.Pips,10:N0}  {r.LegWinRate,7:P1}  " +
                $"{r.DblWin,5}/{r.Mixed}/{r.DblLoss}"));
        }

        string csvPath = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-comeback-top.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("rank,xPips,yPips,stopLossPips,takeProfitPips,riskPercent,deposit,finalEquity," +
                        "returnPercent,maxDrawdownPercent,entries,pips,legWinRate,wins,doubleWin,mixed,doubleLoss");
            for (int i = 0; i < best2.Count; i++)
            {
                var r = best2[i];
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{r.XPips},{r.YPips},{r.SlPips},{r.TpPips},{r.RiskPercent:F2},{opt.Deposit:F0}," +
                    $"{opt.Deposit * r.Equity:F0},{r.ReturnPercent:F1},{r.MaxDdPercent:F2},{r.Entries}," +
                    $"{r.Pips:F1},{r.LegWinRate:F4},{r.Wins},{r.DblWin},{r.Mixed},{r.DblLoss}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Top CSV:    {csvPath} ({best2.Count} rows)");
        return 0;
    }

    private static int WriteWindows(Options opt, (string Label, int Start, int End)[] windows,
        List<ComebackResult>[] perWindow, string outDir)
    {
        Console.WriteLine();
        Console.WriteLine("==================== BEST COMBINATION OF EVERY WINDOW ====================");
        Console.WriteLine("window     X     Y    SL    TP   risk        start          end     return   maxDD  entries      pips  legWin%      2w/mix/2l");

        int found = 0;
        double sumReturn = 0;
        for (int w = 0; w < windows.Length; w++)
        {
            var rows = perWindow[w];
            if (rows.Count == 0)
            {
                Console.WriteLine($"{windows[w].Label,-7}  nothing passed the filters");
                continue;
            }

            var r = rows[0];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{windows[w].Label,-7} {r.XPips,4}  {r.YPips,4}  {r.SlPips,4}  {r.TpPips,4}  {r.RiskPercent,4:F2}%  " +
                $"{opt.Deposit,11:N0}  {opt.Deposit * r.Equity,11:N0}  {r.ReturnPercent,9:N1}%  " +
                $"{r.MaxDdPercent,5:F1}%  {r.Entries,7:N0}  {r.Pips,8:N0}  {r.LegWinRate,7:P1}  " +
                $"{r.DblWin,5}/{r.Mixed}/{r.DblLoss}"));
            sumReturn += r.ReturnPercent;
            found++;
        }

        Console.WriteLine();
        Console.WriteLine("Every window starts again from the full deposit and is optimised on its own data only.");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Average window: {sumReturn / Math.Max(1, found):N1}%."));

        string suffix = opt.PerMonth ? "-per-month.csv" : "-per-year.csv";
        string csvPath = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-comeback{suffix}");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("window,rank,xPips,yPips,stopLossPips,takeProfitPips,riskPercent,deposit,finalEquity," +
                        "returnPercent,maxDrawdownPercent,entries,pips,legWinRate,wins,doubleWin,mixed,doubleLoss");
            for (int y = 0; y < windows.Length; y++)
            {
                var rows = perWindow[y];
                for (int i = 0; i < Math.Min(opt.TopKeep, rows.Count); i++)
                {
                    var r = rows[i];
                    w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{windows[y].Label},{i + 1},{r.XPips},{r.YPips},{r.SlPips},{r.TpPips},{r.RiskPercent:F2}," +
                        $"{opt.Deposit:F0},{opt.Deposit * r.Equity:F0},{r.ReturnPercent:F1},{r.MaxDdPercent:F2}," +
                        $"{r.Entries},{r.Pips:F1},{r.LegWinRate:F4},{r.Wins},{r.DblWin},{r.Mixed},{r.DblLoss}"));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Window CSV: {csvPath} (top {opt.TopKeep} of every window)");
        return 0;
    }
}
