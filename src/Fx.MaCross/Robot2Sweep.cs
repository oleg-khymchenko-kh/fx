using System.Diagnostics;
using System.Globalization;

namespace Fx.MaCross;

internal readonly record struct Robot2Result(
    int AvgPeriod, int Diff1, int Diff2, int LossLimit, int ProfLimit, int Delta,
    long ProfitT, long PwsT, long WlsT, int MaxLossRun, int Wins, int Losses,
    int Cancelled, int Duplicates) : IScored
{
    public double ProfitPips => ProfitT / 10.0;
    public double PwsPips => PwsT / 10.0;
    public double WlsPips => WlsT / 10.0;
    public int Entries => Wins + Losses;
    public double WinRate => Entries == 0 ? 0 : (double)Wins / Entries;
    public double Score => Math.Min(5000, PwsPips) + 2 * WlsPips;
}

internal static class Robot2Sweep
{
    private struct Tx
    {
        public int Dir;
        public long ENum;
        public int Tp;
        public int Sl;
        public int OpenBar;
        public int ExitBar;
        public bool Win;
    }

    private sealed class Pass
    {
        public long ProfitT;
        public long PwsT;
        public long Run;
        public long WlsT;
        public int Wins;
        public int Losses;
        public int Cancelled;
        public int Duplicates;
        public int LossRun;
        public int MaxLossRun;
        public bool Aborted;
        public Action<int, int, bool, int, long, int>? OnClose;
    }

    private static readonly int[] ApGrid = { 1440, 2880, 4320, 5760, 7200, 8640, 10080, 11520, 12960, 14400 };
    private static readonly int[] D1Grid = { -50, -40, -30, -20, -10, 0, 10, 20, 30, 40, 50 };
    private static readonly int[] D2Grid = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
    private static readonly int[] LossGrid = { 20, 30, 40, 50, 60, 70, 80, 90, 100 };
    private static readonly int[] ProfGrid = { 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150 };
    private static readonly int[] DeltaGrid = { 0, 10, 20, 30, 40, 50 };

    public static int Run(Options opt, History h)
    {
        int n = h.Count;
        var prefix = new long[n + 1];
        long acc = 0;
        for (int i = 0; i < n; i++)
        {
            acc += h.Avg[i];
            prefix[i + 1] = acc;
        }

        if (opt.Replay is { } replayPath)
            return Replay(opt, h, prefix, n, replayPath);

        var lossGrid = LossGrid.Where(v => v >= opt.SlMin && v <= opt.SlMax).ToArray();
        if (lossGrid.Length == 0)
        {
            Console.Error.WriteLine("No legacy lossLimit value fits --sl-min/--sl-max.");
            return 1;
        }

        int stopCombos = 0;
        foreach (int l in lossGrid)
        foreach (int p in ProfGrid)
            if (opt.RrMax <= 0 || p <= opt.RrMax * l)
                stopCombos++;

        long combos = (long)ApGrid.Length * D1Grid.Length * D2Grid.Length *
                      lossGrid.Length * ProfGrid.Length * DeltaGrid.Length;
        long effective = (long)ApGrid.Length * D1Grid.Length * D2Grid.Length *
                         stopCombos * DeltaGrid.Length;
        long lossFilterT = (long)opt.LossFilter * 10;
        long profFilterT = (long)opt.ProfFilter * 10;

        Console.WriteLine();
        Console.WriteLine("Robot2 mode: the legacy 2015 strategy, see docs/legacy-robot2-strategy.md.");
        Console.WriteLine("SMA over bar midpoints, ending at the current bar. A side arms when the bar");
        Console.WriteLine("touches avg+-diffLevel1 (touch = level inside the bar or in the gap to the");
        Console.WriteLine("previous bar) and fires when the armed side touches avg+-diffLevel2: a limit");
        Console.WriteLine("order goes orderStartDelta pips back from that level, cancelled if the price");
        Console.WriteLine("runs profLimit away first. Fixed TP=profLimit, SL=lossLimit from the entry.");
        Console.WriteLine("Many trades can be open at once; a new one is dropped when an open trade of");
        Console.WriteLine("the same direction sits within 50 pips. Faithfully reproduced legacy quirks:");
        Console.WriteLine("the high level is checked first, so a bar covering both brackets is a win");
        Console.WriteLine("for buys and a loss for sells; re-arming drops the pending order; fills are");
        Console.WriteLine("checked before cancels on the same bar; a trade can close on its entry bar.");
        Console.WriteLine($"Grid:         avgPeriod {ApGrid[0]}..{ApGrid[^1]} step 1440, diffLevel1 -50..50 step 10,");
        Console.WriteLine($"              diffLevel2 0..100 step 10, lossLimit {lossGrid[0]}..{lossGrid[^1]} step 10,");
        Console.WriteLine("              profLimit 50..150 step 10, orderStartDelta 0..50 step 10");
        if (opt.RrMax > 0)
            Console.WriteLine($"TP cap:       at most {opt.RrMax:0.##} x SL ({stopCombos} of " +
                              $"{lossGrid.Length * ProfGrid.Length} SL/TP combos kept)");
        Console.WriteLine($"Combinations: {effective:N0}, every one is a full sequential pass");
        Console.WriteLine($"Spread cost:  {opt.SpreadTenths / 10.0:F1} pips per closed trade (legacy used 5.0)");
        Console.WriteLine($"Save filter:  profitWithSpread >= {opt.ProfFilter:N0} pips and worstLoss >= {opt.LossFilter:N0} pips");
        Console.WriteLine($"Early abort:  a pass dies as soon as worstLoss drops below {opt.LossFilter:N0} pips");
        Console.WriteLine($"Ranking:      score = min(5000, profitWithSpread) + 2 * worstLoss (the legacy GUI formula)");
        Console.WriteLine($"Threads:      {opt.Threads}");
        Console.WriteLine();
        Console.WriteLine("Running Robot2 sweep...");
        var sw = Stopwatch.StartNew();

        int d1Count = D1Grid.Length, d2Count = D2Grid.Length;
        int lossCount = lossGrid.Length, profCount = ProfGrid.Length, deltaCount = DeltaGrid.Length;
        long perAp = (long)d1Count * d2Count * lossCount * profCount * deltaCount;

        long nextCombo = -1;
        long done = 0;
        long aborted = 0;
        long saved = 0;
        var tops = new TopList<Robot2Result>[opt.Threads];
        var workers = new Task[opt.Threads];
        for (int t = 0; t < opt.Threads; t++)
        {
            int slot = t;
            workers[slot] = Task.Run(() =>
            {
                var top = new TopList<Robot2Result>(opt.TopKeep);
                tops[slot] = top;
                var pass = new Pass();
                var txs = new List<Tx>(64);
                long localAborted = 0;
                long localSaved = 0;

                while (true)
                {
                    long c = Interlocked.Increment(ref nextCombo);
                    if (c >= combos) break;
                    int ap = ApGrid[(int)(c / perAp)];
                    long rem = c % perAp;
                    int d1 = D1Grid[(int)(rem / (d2Count * lossCount * profCount * deltaCount))];
                    rem %= d2Count * lossCount * profCount * deltaCount;
                    int d2 = D2Grid[(int)(rem / (lossCount * profCount * deltaCount))];
                    rem %= lossCount * profCount * deltaCount;
                    int loss = lossGrid[(int)(rem / (profCount * deltaCount))];
                    rem %= profCount * deltaCount;
                    int prof = ProfGrid[(int)(rem / deltaCount)];
                    int delta = DeltaGrid[(int)(rem % deltaCount)];

                    Interlocked.Increment(ref done);
                    if (opt.RrMax > 0 && prof > opt.RrMax * loss) continue;

                    Simulate(h, prefix, n, opt.PipPoints, ap, d1, d2, loss, prof, delta,
                        opt.SpreadTenths, lossFilterT, pass, txs);

                    if (pass.Aborted)
                    {
                        localAborted++;
                        continue;
                    }

                    if (pass.PwsT >= profFilterT && pass.WlsT >= lossFilterT) localSaved++;
                    top.Offer(new Robot2Result(ap, d1, d2, loss, prof, delta, pass.ProfitT, pass.PwsT,
                        pass.WlsT, pass.MaxLossRun, pass.Wins, pass.Losses, pass.Cancelled,
                        pass.Duplicates));
                }

                Interlocked.Add(ref aborted, localAborted);
                Interlocked.Add(ref saved, localSaved);
            });
        }

        var all = Task.WhenAll(workers);
        while (!all.Wait(5000))
        {
            long dn = Volatile.Read(ref done);
            double share = (double)dn / combos;
            double eta = share > 0.001 ? sw.Elapsed.TotalSeconds * (1 - share) / share : 0;
            Console.WriteLine($"  {dn:N0}/{combos:N0} combos ({share:P1})  " +
                              $"elapsed {sw.Elapsed.TotalSeconds:F0}s  eta {eta:F0}s");
        }

        if (all.IsFaulted)
        {
            Console.Error.WriteLine(all.Exception?.GetBaseException().ToString());
            return 1;
        }

        Console.WriteLine($"Robot2 sweep done in {sw.Elapsed.TotalSeconds:F1}s: {combos:N0} combos, " +
                          $"{aborted:N0} aborted early, {saved:N0} passed the legacy save filter");

        var best = tops.Where(v => v is not null).SelectMany(v => v.Items)
            .OrderByDescending(r => r.Score)
            .Take(opt.TopKeep)
            .ToList();

        if (best.Count == 0)
        {
            Console.Error.WriteLine("Every combination aborted on the loss filter.");
            return 1;
        }

        var dmf = new (int Direct, int Mirror)[best.Count];
        Parallel.For(0, best.Count, new ParallelOptions { MaxDegreeOfParallelism = opt.Threads }, i =>
        {
            var (_, _, d, m) = SimMirror(opt, h, prefix, n, best[i], 0, n);
            dmf[i] = (d, m);
        });

        Console.WriteLine();
        Console.WriteLine($"==================== TOP {Math.Min(opt.TopPrint, best.Count)} (by legacy score) ====================");
        Console.WriteLine("  #     avg  diff1  diff2    SL    TP  delta       profit   withSpread    worstLoss  maxRun  entries   win%          d/m/f   cancel    dup      score");
        for (int i = 0; i < Math.Min(opt.TopPrint, best.Count); i++)
        {
            var r = best[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}  {BandSweep.Label(r.AvgPeriod),6}  {r.Diff1,5}  {r.Diff2,5}  {r.LossLimit,4}  " +
                $"{r.ProfLimit,4}  {r.Delta,5}  {r.ProfitPips,11:N0}  {r.PwsPips,11:N0}  {r.WlsPips,11:N0}  " +
                $"{r.MaxLossRun,6}  {r.Entries,7:N0}  {r.WinRate,5:P1}  {dmf[i].Direct,5}/{dmf[i].Mirror}/{r.Entries - dmf[i].Direct - dmf[i].Mirror,-4}  " +
                $"{r.Cancelled,7:N0}  {r.Duplicates,5:N0}  {r.Score,9:N0}"));
        }

        PrintYearBreakdown(opt, h, prefix, n, best[0]);

        if (opt.DealsFile is { } dealsValue)
        {
            string dealsPath = DealsExport.Resolve(opt, dealsValue);
            int written = WriteDeals(opt, h, prefix, n, best[0], dealsPath);
            Console.WriteLine();
            Console.WriteLine($"Deals file: {dealsPath} ({written:N0} trades, symbol {opt.Symbol})");
        }

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);
        string csvPath = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-robot2-top.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("rank,avgPeriodMinutes,diffLevel1,diffLevel2,lossLimit,profLimit,orderStartDelta," +
                        "profitPips,profitWithSpreadPips,worstLossPips,maxLossRun,entries,wins,losses," +
                        "directWins,mirrorWins,bothLose,winRate,cancelledOrders,duplicateTransactions," +
                        "score,passesLegacyFilter");
            for (int i = 0; i < best.Count; i++)
            {
                var r = best[i];
                bool passes = r.PwsT >= profFilterT && r.WlsT >= lossFilterT;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{r.AvgPeriod},{r.Diff1},{r.Diff2},{r.LossLimit},{r.ProfLimit},{r.Delta}," +
                    $"{r.ProfitPips:F1},{r.PwsPips:F1},{r.WlsPips:F1},{r.MaxLossRun},{r.Entries},{r.Wins}," +
                    $"{r.Losses},{dmf[i].Direct},{dmf[i].Mirror},{r.Entries - dmf[i].Direct - dmf[i].Mirror}," +
                    $"{r.WinRate:F4},{r.Cancelled},{r.Duplicates},{r.Score:F1},{(passes ? 1 : 0)}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Top CSV:    {csvPath} ({best.Count} rows)");
        return 0;
    }

    private static int NextBit(ulong[] m, int from, int limit)
    {
        if (from >= limit) return int.MaxValue;
        int wi = from >> 6;
        int lastWord = (limit - 1) >> 6;
        ulong word = m[wi] & (~0UL << (from & 63));
        while (true)
        {
            if (word != 0)
            {
                int b = (wi << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
                return b < limit ? b : int.MaxValue;
            }

            if (++wi > lastWord) return int.MaxValue;
            word = m[wi];
        }
    }

    private static bool Bit(ulong[] m, int b) => (m[b >> 6] >> (b & 63) & 1UL) != 0;

    private static void BuildMasks(History h, long[] prefix, int n, int pip, int from, int to,
        ulong[][][] masks, int threads)
    {
        var apTasks = new Task[ApGrid.Length];
        for (int a = 0; a < ApGrid.Length; a++)
        {
            int apIdx = a;
            apTasks[a] = Task.Run(() =>
            {
                int w = ApGrid[apIdx];
                var bits = masks[apIdx];
                for (int d = 0; d < 21; d++) Array.Clear(bits[d]);
                var hi = h.Hi;
                var lo = h.Lo;
                var dpW = new long[21];
                for (int d = 0; d < 21; d++) dpW[d] = (long)(d * 10 - 100) * pip * w;
                int start = Math.Max(Math.Max(1, w - 1), from);
                for (int i = start; i < to; i++)
                {
                    long s = prefix[i + 1] - prefix[i + 1 - w];
                    long tLoW = (long)Math.Min(lo[i], hi[i - 1]) * w;
                    long tHiW = (long)Math.Max(hi[i], lo[i - 1]) * w;
                    long lb = tLoW - s;
                    long hb = tHiW - s;
                    for (int d = 0; d < 21; d++)
                    {
                        if (dpW[d] < lb || dpW[d] > hb) continue;
                        bits[d][i >> 6] |= 1UL << (i & 63);
                    }
                }
            });
        }

        Task.WaitAll(apTasks);
    }

    private static void EventSimulate(History h, long[] prefix, int n, int pip, BlockIndex blocks,
        ulong[][] apMasks, int ap, int d1, int d2, int loss, int prof, int delta,
        int spreadT, long lossFilterT, Pass pass, List<Tx> txs, int from, int to)
    {
        var hi = h.Hi;
        var lo = h.Lo;
        int w = ap;
        long d2n = (long)d2 * pip * w;
        int lossP = loss * pip;
        int profP = prof * pip;
        int deltaP = delta * pip;
        long dupBand = 50L * pip * w;

        pass.ProfitT = 0;
        pass.PwsT = 0;
        pass.Run = 0;
        pass.WlsT = 0;
        pass.Wins = 0;
        pass.Losses = 0;
        pass.Cancelled = 0;
        pass.Duplicates = 0;
        pass.LossRun = 0;
        pass.MaxLossRun = 0;
        pass.Aborted = false;
        txs.Clear();

        if (w >= n) return;
        int start = Math.Max(Math.Max(1, w - 1), from);
        if (start >= to) return;

        var armTopM = apMasks[(d1 + 100) / 10];
        var armLowM = apMasks[(-d1 + 100) / 10];
        var fireTopM = apMasks[(d2 + 100) / 10];
        var fireLowM = apMasks[(-d2 + 100) / 10];

        int modeTop = 0, modeLow = 0;
        int topFill = int.MaxValue, topCancel = int.MaxValue, topEntryF = 0, topCancelC = 0, topSlF = 0;
        long topE = 0;
        int lowFill = int.MaxValue, lowCancel = int.MaxValue, lowEntryC = 0, lowCancelF = 0, lowSlC = 0;
        long lowE = 0;
        int nextArmTop = NextBit(armTopM, start, to);
        int nextArmLow = NextBit(armLowM, start, to);
        int nextFireTop = int.MaxValue, nextFireLow = int.MaxValue;
        int prev = start - 1;

        while (true)
        {
            if (nextArmTop <= prev) nextArmTop = NextBit(armTopM, prev + 1, to);
            if (nextArmLow <= prev) nextArmLow = NextBit(armLowM, prev + 1, to);
            if (modeTop == 1 && nextFireTop <= prev) nextFireTop = NextBit(fireTopM, prev + 1, to);
            if (modeLow == 1 && nextFireLow <= prev) nextFireLow = NextBit(fireLowM, prev + 1, to);

            int b = int.MaxValue;
            if (modeTop == 0) b = Math.Min(b, nextArmTop);
            else if (modeTop == 1) b = Math.Min(b, nextFireTop);
            else b = Math.Min(b, Math.Min(Math.Min(topFill, topCancel), nextArmTop));
            if (modeLow == 0) b = Math.Min(b, nextArmLow);
            else if (modeLow == 1) b = Math.Min(b, nextFireLow);
            else b = Math.Min(b, Math.Min(Math.Min(lowFill, lowCancel), nextArmLow));
            for (int k = 0; k < txs.Count; k++)
                if (txs[k].ExitBar < n && txs[k].ExitBar < b)
                    b = txs[k].ExitBar;

            if (b == int.MaxValue) return;
            prev = b;
            int hj = hi[b], lj = lo[b];

            if (b < to && Bit(armTopM, b) && modeTop != 1)
            {
                modeTop = 1;
                nextFireTop = NextBit(fireTopM, b, to);
            }

            if (b < to && Bit(armLowM, b) && modeLow != 1)
            {
                modeLow = 1;
                nextFireLow = NextBit(fireLowM, b, to);
            }

            if (modeTop == 1 && nextFireTop == b)
            {
                long s = prefix[b + 1] - prefix[b + 1 - w];
                topE = s + d2n - (long)deltaP * w;
                topEntryF = (int)(topE / w);
                topCancelC = (int)((topE + (long)profP * w + w - 1) / w);
                topSlF = (int)((topE - (long)lossP * w) / w);
                int f = BandSweep.FirstLow(lo, blocks, n, b, topEntryF);
                int c = BandSweep.FirstHigh(hi, blocks, n, b, topCancelC);
                topFill = f < to ? f : int.MaxValue;
                topCancel = c < to ? c : int.MaxValue;
                modeTop = 2;
            }

            if (modeLow == 1 && nextFireLow == b)
            {
                long s = prefix[b + 1] - prefix[b + 1 - w];
                lowE = s - d2n + (long)deltaP * w;
                lowEntryC = (int)((lowE + w - 1) / w);
                lowCancelF = (int)((lowE - (long)profP * w) / w);
                lowSlC = (int)((lowE + (long)lossP * w + w - 1) / w);
                int f = BandSweep.FirstHigh(hi, blocks, n, b, lowEntryC);
                int c = BandSweep.FirstLow(lo, blocks, n, b, lowCancelF);
                lowFill = f < to ? f : int.MaxValue;
                lowCancel = c < to ? c : int.MaxValue;
                modeLow = 2;
            }

            if (modeTop == 2)
            {
                if (topFill == b)
                {
                    modeTop = 0;
                    bool dup = false;
                    for (int k = 0; k < txs.Count; k++)
                    {
                        if (txs[k].Dir > 0 && Math.Abs(txs[k].ENum - topE) < dupBand)
                        {
                            dup = true;
                            break;
                        }
                    }

                    if (dup) pass.Duplicates++;
                    else
                    {
                        int tpBar = BandSweep.FirstHigh(hi, blocks, n, b, topCancelC);
                        int slBar = BandSweep.FirstLow(lo, blocks, n, b, topSlF);
                        bool win = tpBar <= slBar;
                        txs.Add(new Tx
                        {
                            Dir = 1, ENum = topE, OpenBar = b,
                            ExitBar = win ? tpBar : slBar, Win = win,
                        });
                    }
                }
                else if (topCancel == b)
                {
                    modeTop = 0;
                    pass.Cancelled++;
                }
            }

            if (modeLow == 2)
            {
                if (lowFill == b)
                {
                    modeLow = 0;
                    bool dup = false;
                    for (int k = 0; k < txs.Count; k++)
                    {
                        if (txs[k].Dir < 0 && Math.Abs(txs[k].ENum - lowE) < dupBand)
                        {
                            dup = true;
                            break;
                        }
                    }

                    if (dup) pass.Duplicates++;
                    else
                    {
                        int slBar = BandSweep.FirstHigh(hi, blocks, n, b, lowSlC);
                        int tpBar = BandSweep.FirstLow(lo, blocks, n, b, lowCancelF);
                        bool win = tpBar < slBar;
                        txs.Add(new Tx
                        {
                            Dir = -1, ENum = lowE, OpenBar = b,
                            ExitBar = win ? tpBar : slBar, Win = win,
                        });
                    }
                }
                else if (lowCancel == b)
                {
                    modeLow = 0;
                    pass.Cancelled++;
                }
            }

            for (int k = 0; k < txs.Count; k++)
            {
                var tx = txs[k];
                if (tx.ExitBar != b) continue;
                int pnlT = tx.Win ? profP : -lossP;
                int pwsT = pnlT - spreadT;
                pass.ProfitT += pnlT;
                pass.PwsT += pwsT;
                if (tx.Win) pass.Wins++;
                else pass.Losses++;
                if (pwsT < 0)
                {
                    pass.LossRun++;
                    if (pass.LossRun > pass.MaxLossRun) pass.MaxLossRun = pass.LossRun;
                }
                else
                {
                    pass.LossRun = 0;
                }

                pass.Run += pwsT;
                if (pass.Run > 0) pass.Run = 0;
                else if (pass.Run < pass.WlsT) pass.WlsT = pass.Run;
                pass.OnClose?.Invoke(tx.OpenBar, pwsT, tx.Win, tx.Dir, tx.ENum, b);
                txs.RemoveAt(k);
                k--;
                if (pass.WlsT < lossFilterT)
                {
                    pass.Aborted = true;
                    return;
                }
            }
        }
    }

    public static int WalkForward(Options opt, History h,
        (string Label, int Start, int End)[] fitWindows, (int Start, int End)[] testWindows)
    {
        int n = h.Count;
        var prefix = new long[n + 1];
        long acc = 0;
        for (int i = 0; i < n; i++)
        {
            acc += h.Avg[i];
            prefix[i + 1] = acc;
        }

        var lossGrid = LossGrid.Where(v => v >= opt.SlMin && v <= opt.SlMax).ToArray();
        if (lossGrid.Length == 0)
        {
            Console.Error.WriteLine("No legacy lossLimit value fits --sl-min/--sl-max.");
            return 1;
        }

        long combos = (long)ApGrid.Length * D1Grid.Length * D2Grid.Length *
                      lossGrid.Length * ProfGrid.Length * DeltaGrid.Length;
        long lossFilterT = (long)opt.LossFilter * 10;
        int d1Count = D1Grid.Length, d2Count = D2Grid.Length;
        int lossCount = lossGrid.Length, profCount = ProfGrid.Length, deltaCount = DeltaGrid.Length;
        long perAp = (long)d1Count * d2Count * lossCount * profCount * deltaCount;

        Console.WriteLine();
        Console.WriteLine($"Robot2 walk-forward: {fitWindows.Length} months ({fitWindows[0].Label}..{fitWindows[^1].Label}),");
        Console.WriteLine($"fit the full legacy grid ({combos:N0} combos) on the {opt.WalkForwardMonths} months before each,");
        Console.WriteLine($"rank by the legacy score, take the top {opt.TopPrint}, run each on the month as-is.");
        Console.WriteLine("Entries only inside a window; trades open at its end run on until they close.");
        Console.WriteLine($"Fit filter:   at least {opt.MinEntries} entries per fit window, " +
                          $"early abort below {opt.LossFilter:N0} pips");
        Console.WriteLine("d/m/f:        direct wins / direct lost but the OPPOSITE trade at the same entry");
        Console.WriteLine("              would have won / both lose. Mirror uses the same high-first bar rule.");
        Console.WriteLine($"Spread cost:  {opt.SpreadTenths / 10.0:F1} pips per closed trade");
        Console.WriteLine($"Threads:      {opt.Threads}");
        var swTotal = Stopwatch.StartNew();

        var blocks = BlockIndex.Build(h, n);
        int words = (n >> 6) + 1;
        var masks = new ulong[ApGrid.Length][][];
        for (int a = 0; a < ApGrid.Length; a++)
        {
            masks[a] = new ulong[21][];
            for (int d = 0; d < 21; d++) masks[a][d] = new ulong[words];
        }

        BuildMasks(h, prefix, n, opt.PipPoints, fitWindows[0].Start, fitWindows[0].End, masks, opt.Threads);
        {
            var seqPass = new Pass();
            var evtPass = new Pass();
            var seqTxs = new List<Tx>(64);
            var evtTxs = new List<Tx>(64);
            int checkedCombos = 0;
            for (long c = 0; c < combos; c += 997)
            {
                int ap = ApGrid[(int)(c / perAp)];
                long rem = c % perAp;
                int d1 = D1Grid[(int)(rem / (d2Count * lossCount * profCount * deltaCount))];
                rem %= d2Count * lossCount * profCount * deltaCount;
                int d2 = D2Grid[(int)(rem / (lossCount * profCount * deltaCount))];
                rem %= lossCount * profCount * deltaCount;
                int loss = lossGrid[(int)(rem / (profCount * deltaCount))];
                rem %= profCount * deltaCount;
                int prof = ProfGrid[(int)(rem / deltaCount)];
                int delta = DeltaGrid[(int)(rem % deltaCount)];
                if (opt.RrMax > 0 && prof > opt.RrMax * loss) continue;
                Simulate(h, prefix, n, opt.PipPoints, ap, d1, d2, loss, prof, delta,
                    opt.SpreadTenths, lossFilterT, seqPass, seqTxs, fitWindows[0].Start, fitWindows[0].End);
                EventSimulate(h, prefix, n, opt.PipPoints, blocks, masks[(int)(c / perAp)], ap, d1, d2,
                    loss, prof, delta, opt.SpreadTenths, lossFilterT, evtPass, evtTxs,
                    fitWindows[0].Start, fitWindows[0].End);
                checkedCombos++;
                if (seqPass.Aborted != evtPass.Aborted ||
                    (!seqPass.Aborted && (seqPass.ProfitT != evtPass.ProfitT || seqPass.PwsT != evtPass.PwsT ||
                                          seqPass.WlsT != evtPass.WlsT || seqPass.Wins != evtPass.Wins ||
                                          seqPass.Losses != evtPass.Losses ||
                                          seqPass.Cancelled != evtPass.Cancelled ||
                                          seqPass.Duplicates != evtPass.Duplicates ||
                                          seqPass.MaxLossRun != evtPass.MaxLossRun)))
                {
                    Console.Error.WriteLine($"ENGINE MISMATCH at ap={ap} d1={d1} d2={d2} loss={loss} " +
                                            $"prof={prof} delta={delta}: sequential " +
                                            $"pws={seqPass.PwsT} wls={seqPass.WlsT} n={seqPass.Wins + seqPass.Losses} " +
                                            $"abort={seqPass.Aborted} vs event pws={evtPass.PwsT} wls={evtPass.WlsT} " +
                                            $"n={evtPass.Wins + evtPass.Losses} abort={evtPass.Aborted}");
                    return 1;
                }
            }

            Console.WriteLine($"Engine check: {checkedCombos} sampled combos, event engine matches the " +
                              $"sequential one exactly ({swTotal.Elapsed.TotalSeconds:F0}s)");
        }

        var rows = new List<(int W, int Rank, Robot2Result Fit, long TestPws, int TestN, int TestD, int TestM)>();
        for (int wi = 0; wi < fitWindows.Length; wi++)
        {
            var (label, fitStart, fitEnd) = fitWindows[wi];
            var sw = Stopwatch.StartNew();
            if (wi > 0) BuildMasks(h, prefix, n, opt.PipPoints, fitStart, fitEnd, masks, opt.Threads);
            long nextCombo = -1;
            long aborted = 0;
            var tops = new TopList<Robot2Result>[opt.Threads];
            var workers = new Task[opt.Threads];
            for (int t = 0; t < opt.Threads; t++)
            {
                int slot = t;
                workers[slot] = Task.Run(() =>
                {
                    var top = new TopList<Robot2Result>(opt.TopPrint);
                    tops[slot] = top;
                    var pass = new Pass();
                    var txs = new List<Tx>(64);
                    long localAborted = 0;

                    while (true)
                    {
                        long c = Interlocked.Increment(ref nextCombo);
                        if (c >= combos) break;
                        int ap = ApGrid[(int)(c / perAp)];
                        long rem = c % perAp;
                        int d1 = D1Grid[(int)(rem / (d2Count * lossCount * profCount * deltaCount))];
                        rem %= d2Count * lossCount * profCount * deltaCount;
                        int d2 = D2Grid[(int)(rem / (lossCount * profCount * deltaCount))];
                        rem %= lossCount * profCount * deltaCount;
                        int loss = lossGrid[(int)(rem / (profCount * deltaCount))];
                        rem %= profCount * deltaCount;
                        int prof = ProfGrid[(int)(rem / deltaCount)];
                        int delta = DeltaGrid[(int)(rem % deltaCount)];
                        if (opt.RrMax > 0 && prof > opt.RrMax * loss) continue;

                        EventSimulate(h, prefix, n, opt.PipPoints, blocks, masks[(int)(c / perAp)],
                            ap, d1, d2, loss, prof, delta,
                            opt.SpreadTenths, lossFilterT, pass, txs, fitStart, fitEnd);
                        if (pass.Aborted)
                        {
                            localAborted++;
                            continue;
                        }

                        if (pass.Wins + pass.Losses < opt.MinEntries) continue;
                        top.Offer(new Robot2Result(ap, d1, d2, loss, prof, delta, pass.ProfitT, pass.PwsT,
                            pass.WlsT, pass.MaxLossRun, pass.Wins, pass.Losses, pass.Cancelled,
                            pass.Duplicates));
                    }

                    Interlocked.Add(ref aborted, localAborted);
                });
            }

            Task.WaitAll(workers);
            var best = tops.Where(v => v is not null).SelectMany(v => v.Items)
                .OrderByDescending(r => r.Score)
                .Take(opt.TopPrint)
                .ToList();
            Console.WriteLine($"  {label}: fit swept in {sw.Elapsed.TotalSeconds:F0}s, " +
                              $"{aborted:N0} aborted, {best.Count} picks");

            for (int r = 0; r < best.Count; r++)
            {
                var (testPws, testN, testD, testM) = SimMirror(opt, h, prefix, n, best[r],
                    testWindows[wi].Start, testWindows[wi].End);
                rows.Add((wi, r + 1, best[r], testPws, testN, testD, testM));
            }
        }

        Console.WriteLine($"Walk-forward done in {swTotal.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine();
        Console.WriteLine("=========== WALK-FORWARD, top picks of every month ===========");
        foreach (var group in rows.GroupBy(row => row.W))
        {
            Console.WriteLine();
            Console.WriteLine($"---- {fitWindows[group.Key].Label} (fit = {opt.WalkForwardMonths} months before) ----");
            Console.WriteLine("  #     avg  diff1  diff2    SL    TP  delta    fitPips  fitWorst  fitN    monthPips  monthN      d/m/f   real%    be%");
            foreach (var row in group)
            {
                var f = row.Fit;
                double be = 100.0 * (f.LossLimit * 10 + opt.SpreadTenths) / (f.LossLimit * 10 + f.ProfLimit * 10);
                string real = row.TestN == 0 ? "-"
                    : string.Create(CultureInfo.InvariantCulture, $"{100.0 * row.TestD / row.TestN:F1}");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{row.Rank,3}  {BandSweep.Label(f.AvgPeriod),6}  {f.Diff1,5}  {f.Diff2,5}  {f.LossLimit,4}  " +
                    $"{f.ProfLimit,4}  {f.Delta,5}  {f.PwsPips,9:N0}  {f.WlsPips,8:N0}  {f.Entries,4}  " +
                    $"{row.TestPws / 10.0,11:N0}  {row.TestN,6}  {row.TestD,4}/{row.TestM}/{row.TestN - row.TestD - row.TestM,-3}  " +
                    $"{real,6}  {be,5:F1}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("=========== TOTALS PER RANK (sum of month results) ===========");
        Console.WriteLine("rank    months  positive      totalPips  entries  direct  mirror   fail   real%");
        for (int rank = 1; rank <= opt.TopPrint; rank++)
        {
            var ofRank = rows.Where(r => r.Rank == rank).ToList();
            if (ofRank.Count == 0) continue;
            int e = ofRank.Sum(r => r.TestN);
            int dw = ofRank.Sum(r => r.TestD);
            int mw = ofRank.Sum(r => r.TestM);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{rank,4}  {ofRank.Count,8}  {ofRank.Count(r => r.TestPws > 0),8}  " +
                $"{ofRank.Sum(r => r.TestPws) / 10.0,13:N0}  {e,7}  {dw,6}  {mw,6}  {e - dw - mw,5}  " +
                $"{(e == 0 ? 0 : 100.0 * dw / e),6:F1}"));
        }

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);
        string csvPath = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-robot2-walk.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("month,rank,avgPeriodMinutes,diffLevel1,diffLevel2,lossLimit,profLimit,orderStartDelta," +
                        "fitPips,fitWorstLoss,fitEntries,fitWinRate,monthPips,monthEntries,monthDirectWins," +
                        "monthMirrorWins,monthBothLose,monthDirectPct,breakEvenPct");
            foreach (var row in rows)
            {
                var f = row.Fit;
                double be = 100.0 * (f.LossLimit * 10 + opt.SpreadTenths) / (f.LossLimit * 10 + f.ProfLimit * 10);
                double real = row.TestN == 0 ? 0 : 100.0 * row.TestD / row.TestN;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{fitWindows[row.W].Label},{row.Rank},{f.AvgPeriod},{f.Diff1},{f.Diff2},{f.LossLimit}," +
                    $"{f.ProfLimit},{f.Delta},{f.PwsPips:F1},{f.WlsPips:F1},{f.Entries},{f.WinRate:F4}," +
                    $"{row.TestPws / 10.0:F1},{row.TestN},{row.TestD},{row.TestM}," +
                    $"{row.TestN - row.TestD - row.TestM},{real:F1},{be:F1}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Walk CSV:   {csvPath} ({rows.Count} rows)");
        return 0;
    }

    private static bool MirrorWins(History h, int n, int w, int openBar, int dir, long eNum,
        int profP, int lossP)
    {
        var hi = h.Hi;
        var lo = h.Lo;
        if (dir > 0)
        {
            long tpF = (eNum - (long)profP * w) / w;
            long slC = (eNum + (long)lossP * w + w - 1) / w;
            for (int k = openBar; k < n; k++)
            {
                if (hi[k] >= slC) return false;
                if (lo[k] <= tpF) return true;
            }

            return false;
        }

        long tpC = (eNum + (long)profP * w + w - 1) / w;
        long slF = (eNum - (long)lossP * w) / w;
        for (int k = openBar; k < n; k++)
        {
            if (hi[k] >= tpC) return true;
            if (lo[k] <= slF) return false;
        }

        return false;
    }

    private static (long Pws, int N, int Direct, int Mirror) SimMirror(Options opt, History h,
        long[] prefix, int n, Robot2Result r, int testStart, int testEnd)
    {
        int w = r.AvgPeriod;
        int profP = r.ProfLimit * opt.PipPoints;
        int lossP = r.LossLimit * opt.PipPoints;
        long pws = 0;
        int entries = 0, direct = 0, mirror = 0;
        var pass = new Pass
        {
            OnClose = (openBar, pwsT, win, dir, eNum, _) =>
            {
                entries++;
                pws += pwsT;
                if (win) direct++;
                else if (MirrorWins(h, n, w, openBar, dir, eNum, profP, lossP)) mirror++;
            },
        };
        Simulate(h, prefix, n, opt.PipPoints, r.AvgPeriod, r.Diff1, r.Diff2, r.LossLimit, r.ProfLimit,
            r.Delta, opt.SpreadTenths, long.MinValue, pass, new List<Tx>(64), testStart, testEnd);
        return (pws, entries, direct, mirror);
    }

    private static int Replay(Options opt, History h, long[] prefix, int n, string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"Replay file not found: {csvPath}");
            return 1;
        }

        var lines = File.ReadAllLines(csvPath);
        var header = lines[0].Split(';', ',').ToList();
        int cAp = header.IndexOf("avgPeriodMinutes");
        int cD1 = header.IndexOf("diffLevel1");
        int cD2 = header.IndexOf("diffLevel2");
        int cLoss = header.IndexOf("lossLimit");
        int cProf = header.IndexOf("profLimit");
        int cDelta = header.IndexOf("orderStartDelta");
        int cPws = header.IndexOf("profitWithSpreadPips");
        int cWls = header.IndexOf("worstLossPips");
        if (cAp < 0 || cD1 < 0 || cD2 < 0 || cLoss < 0 || cProf < 0 || cDelta < 0)
        {
            Console.Error.WriteLine("Replay file is not a robot2 top CSV.");
            return 1;
        }

        var fit = new List<(int Ap, int D1, int D2, int Loss, int Prof, int Delta, double Pws, double Wls)>();
        for (int i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split(';', ',');
            fit.Add((int.Parse(f[cAp]), int.Parse(f[cD1]), int.Parse(f[cD2]), int.Parse(f[cLoss]),
                int.Parse(f[cProf]), int.Parse(f[cDelta]),
                double.Parse(f[cPws], CultureInfo.InvariantCulture),
                double.Parse(f[cWls], CultureInfo.InvariantCulture)));
        }

        Console.WriteLine();
        Console.WriteLine($"Robot2 replay: {fit.Count} combinations from {csvPath}");
        Console.WriteLine($"re-run as-is on the loaded range {h.TimeUtc(0):yyyy-MM-dd}..{h.TimeUtc(n - 1):yyyy-MM-dd}, " +
                          "no re-optimisation, no early abort");
        Console.WriteLine($"Spread cost:  {opt.SpreadTenths / 10.0:F1} pips per closed trade");
        var sw = Stopwatch.StartNew();

        var test = new Robot2Result[fit.Count];
        int next = -1;
        var workers = new Task[opt.Threads];
        for (int t = 0; t < opt.Threads; t++)
        {
            workers[t] = Task.Run(() =>
            {
                var pass = new Pass();
                var txs = new List<Tx>(64);
                while (true)
                {
                    int k = Interlocked.Increment(ref next);
                    if (k >= fit.Count) break;
                    var f = fit[k];
                    Simulate(h, prefix, n, opt.PipPoints, f.Ap, f.D1, f.D2, f.Loss, f.Prof, f.Delta,
                        opt.SpreadTenths, long.MinValue, pass, txs);
                    test[k] = new Robot2Result(f.Ap, f.D1, f.D2, f.Loss, f.Prof, f.Delta, pass.ProfitT,
                        pass.PwsT, pass.WlsT, pass.MaxLossRun, pass.Wins, pass.Losses, pass.Cancelled,
                        pass.Duplicates);
                }
            });
        }

        Task.WaitAll(workers);
        Console.WriteLine($"Replay done in {sw.Elapsed.TotalSeconds:F1}s");

        Console.WriteLine();
        Console.WriteLine("==================== FIT vs TEST (first rows = best fit rank) ====================");
        Console.WriteLine("  #     avg  diff1  diff2    SL    TP  delta      fitPips   fitWorst     testPips  testWorst  testN  test win%");
        for (int i = 0; i < Math.Min(opt.TopPrint, fit.Count); i++)
        {
            var f = fit[i];
            var r = test[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}  {BandSweep.Label(f.Ap),6}  {f.D1,5}  {f.D2,5}  {f.Loss,4}  {f.Prof,4}  {f.Delta,5}  " +
                $"{f.Pws,11:N0}  {f.Wls,9:N0}  {r.PwsPips,11:N0}  {r.WlsPips,9:N0}  {r.Entries,5:N0}  {r.WinRate,8:P1}"));
        }

        var pws = test.Select(r => r.PwsPips).OrderBy(v => v).ToArray();
        int positive = test.Count(r => r.PwsT > 0);
        double totalEntries = test.Sum(r => (double)r.Entries);
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Out of sample: {positive}/{fit.Count} profitable ({(double)positive / fit.Count:P1}), " +
            $"median {pws[pws.Length / 2]:N0} pips, best {pws[^1]:N0}, worst {pws[0]:N0}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Average test trades per combo: {totalEntries / fit.Count:N0}"));

        PrintYearBreakdown(opt, h, prefix, n, test[0]);

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);
        string outCsv = Path.Combine(outDir, $"{opt.Symbol.ToLowerInvariant()}-robot2-replay.csv");
        using (var w = new StreamWriter(outCsv))
        {
            w.WriteLine("fitRank,avgPeriodMinutes,diffLevel1,diffLevel2,lossLimit,profLimit,orderStartDelta," +
                        "fitPips,fitWorstLoss,testPips,testWorstLoss,testEntries,testWins,testLosses," +
                        "testWinRate,testMaxLossRun,testCancelled,testDuplicates");
            for (int i = 0; i < fit.Count; i++)
            {
                var f = fit[i];
                var r = test[i];
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{f.Ap},{f.D1},{f.D2},{f.Loss},{f.Prof},{f.Delta},{f.Pws:F1},{f.Wls:F1}," +
                    $"{r.PwsPips:F1},{r.WlsPips:F1},{r.Entries},{r.Wins},{r.Losses},{r.WinRate:F4}," +
                    $"{r.MaxLossRun},{r.Cancelled},{r.Duplicates}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Replay CSV: {outCsv} ({fit.Count} rows)");
        return 0;
    }

    private static void Simulate(History h, long[] prefix, int n, int pip,
        int ap, int d1, int d2, int loss, int prof, int delta,
        int spreadT, long lossFilterT, Pass pass, List<Tx> txs)
        => Simulate(h, prefix, n, pip, ap, d1, d2, loss, prof, delta, spreadT, lossFilterT, pass, txs, 0, n);

    private static void Simulate(History h, long[] prefix, int n, int pip,
        int ap, int d1, int d2, int loss, int prof, int delta,
        int spreadT, long lossFilterT, Pass pass, List<Tx> txs, int from, int to)
    {
        var hi = h.Hi;
        var lo = h.Lo;
        int w = ap;
        long d1n = (long)d1 * pip * w;
        long d2n = (long)d2 * pip * w;
        int lossP = loss * pip;
        int profP = prof * pip;
        int deltaP = delta * pip;
        long dupBand = 50L * pip * w;

        pass.ProfitT = 0;
        pass.PwsT = 0;
        pass.Run = 0;
        pass.WlsT = 0;
        pass.Wins = 0;
        pass.Losses = 0;
        pass.Cancelled = 0;
        pass.Duplicates = 0;
        pass.LossRun = 0;
        pass.MaxLossRun = 0;
        pass.Aborted = false;
        txs.Clear();

        bool armedTop = false, armedLow = false;
        bool topAlive = false, lowAlive = false;
        int topEntry = 0, topCancel = 0, topSl = 0;
        long topENum = 0;
        int lowEntry = 0, lowCancel = 0, lowSl = 0;
        long lowENum = 0;

        if (w >= n) return;
        for (int i = Math.Max(Math.Max(1, w - 1), from); i < to; i++)
        {
            long s = prefix[i + 1] - prefix[i + 1 - w];
            int hj = hi[i], lj = lo[i];
            long tLo = Math.Min(lj, hi[i - 1]);
            long tHi = Math.Max(hj, lo[i - 1]);
            long tLoW = tLo * w, tHiW = tHi * w;

            if (tLoW - d1n <= s && s <= tHiW - d1n)
            {
                armedTop = true;
                topAlive = false;
            }

            if (tLoW + d1n <= s && s <= tHiW + d1n)
            {
                armedLow = true;
                lowAlive = false;
            }

            if (armedTop && tLoW - d2n <= s && s <= tHiW - d2n)
            {
                armedTop = false;
                topENum = s + d2n - (long)deltaP * w;
                topEntry = (int)(topENum / w);
                topCancel = (int)((topENum + (long)profP * w + w - 1) / w);
                topSl = (int)((topENum - (long)lossP * w) / w);
                topAlive = true;
            }

            if (armedLow && tLoW + d2n <= s && s <= tHiW + d2n)
            {
                armedLow = false;
                lowENum = s - d2n + (long)deltaP * w;
                lowEntry = (int)((lowENum + w - 1) / w);
                lowCancel = (int)((lowENum - (long)profP * w) / w);
                lowSl = (int)((lowENum + (long)lossP * w + w - 1) / w);
                lowAlive = true;
            }

            if (topAlive)
            {
                if (lj <= topEntry)
                {
                    topAlive = false;
                    bool dup = false;
                    for (int k = 0; k < txs.Count; k++)
                    {
                        if (txs[k].Dir > 0 && Math.Abs(txs[k].ENum - topENum) < dupBand)
                        {
                            dup = true;
                            break;
                        }
                    }

                    if (dup) pass.Duplicates++;
                    else txs.Add(new Tx { Dir = 1, ENum = topENum, Tp = topCancel, Sl = topSl, OpenBar = i });
                }
                else if (hj >= topCancel)
                {
                    topAlive = false;
                    pass.Cancelled++;
                }
            }

            if (lowAlive)
            {
                if (hj >= lowEntry)
                {
                    lowAlive = false;
                    bool dup = false;
                    for (int k = 0; k < txs.Count; k++)
                    {
                        if (txs[k].Dir < 0 && Math.Abs(txs[k].ENum - lowENum) < dupBand)
                        {
                            dup = true;
                            break;
                        }
                    }

                    if (dup) pass.Duplicates++;
                    else txs.Add(new Tx { Dir = -1, ENum = lowENum, Tp = lowCancel, Sl = lowSl, OpenBar = i });
                }
                else if (lj <= lowCancel)
                {
                    lowAlive = false;
                    pass.Cancelled++;
                }
            }

            for (int k = 0; k < txs.Count; k++)
            {
                var tx = txs[k];
                bool closed;
                bool win;
                if (tx.Dir > 0)
                {
                    if (hj >= tx.Tp) { closed = true; win = true; }
                    else if (lj <= tx.Sl) { closed = true; win = false; }
                    else { closed = false; win = false; }
                }
                else
                {
                    if (hj >= tx.Sl) { closed = true; win = false; }
                    else if (lj <= tx.Tp) { closed = true; win = true; }
                    else { closed = false; win = false; }
                }

                if (!closed) continue;
                int pnlT = win ? profP : -lossP;
                int pwsT = pnlT - spreadT;
                pass.ProfitT += pnlT;
                pass.PwsT += pwsT;
                if (win) pass.Wins++;
                else pass.Losses++;
                if (pwsT < 0)
                {
                    pass.LossRun++;
                    if (pass.LossRun > pass.MaxLossRun) pass.MaxLossRun = pass.LossRun;
                }
                else
                {
                    pass.LossRun = 0;
                }

                pass.Run += pwsT;
                if (pass.Run > 0) pass.Run = 0;
                else if (pass.Run < pass.WlsT) pass.WlsT = pass.Run;
                pass.OnClose?.Invoke(tx.OpenBar, pwsT, win, tx.Dir, tx.ENum, i);
                txs.RemoveAt(k);
                k--;
                if (pass.WlsT < lossFilterT)
                {
                    pass.Aborted = true;
                    return;
                }
            }
        }

        for (int i = to; i < n && txs.Count > 0; i++)
        {
            int hj = hi[i], lj = lo[i];
            for (int k = 0; k < txs.Count; k++)
            {
                var tx = txs[k];
                bool closed;
                bool win;
                if (tx.Dir > 0)
                {
                    if (hj >= tx.Tp) { closed = true; win = true; }
                    else if (lj <= tx.Sl) { closed = true; win = false; }
                    else { closed = false; win = false; }
                }
                else
                {
                    if (hj >= tx.Sl) { closed = true; win = false; }
                    else if (lj <= tx.Tp) { closed = true; win = true; }
                    else { closed = false; win = false; }
                }

                if (!closed) continue;
                int pnlT = win ? profP : -lossP;
                int pwsT = pnlT - spreadT;
                pass.ProfitT += pnlT;
                pass.PwsT += pwsT;
                if (win) pass.Wins++;
                else pass.Losses++;
                if (pwsT < 0)
                {
                    pass.LossRun++;
                    if (pass.LossRun > pass.MaxLossRun) pass.MaxLossRun = pass.LossRun;
                }
                else
                {
                    pass.LossRun = 0;
                }

                pass.Run += pwsT;
                if (pass.Run > 0) pass.Run = 0;
                else if (pass.Run < pass.WlsT) pass.WlsT = pass.Run;
                pass.OnClose?.Invoke(tx.OpenBar, pwsT, win, tx.Dir, tx.ENum, i);
                txs.RemoveAt(k);
                k--;
                if (pass.WlsT < lossFilterT)
                {
                    pass.Aborted = true;
                    return;
                }
            }
        }
    }

    private static int WriteDeals(Options opt, History h, long[] prefix, int n, Robot2Result r, string path)
    {
        int w = r.AvgPeriod;
        int profP = r.ProfLimit * opt.PipPoints;
        int lossP = r.LossLimit * opt.PipPoints;
        var model = new DealsFileModel
        {
            Account = 0,
            ExportedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        var pass = new Pass
        {
            OnClose = (openBar, pwsT, win, dir, eNum, exitBar) =>
            {
                double entry = eNum / (double)w / 100000.0;
                int movePoints = dir > 0
                    ? win ? profP : -lossP
                    : win ? -profP : lossP;
                model.Deals.Add(new DealRecord
                {
                    PositionId = model.Deals.Count + 1,
                    Symbol = opt.Symbol,
                    Side = dir > 0 ? "Buy" : "Sell",
                    Lots = 1,
                    OpenTimeUnix = h.Ts[openBar],
                    OpenPrice = Math.Round(entry, 5),
                    CloseTimeUnix = h.Ts[exitBar],
                    ClosePrice = Math.Round(entry + movePoints / 100000.0, 5),
                    Profit = pwsT,
                    Pips = pwsT / 10.0,
                });
            },
        };
        Simulate(h, prefix, n, opt.PipPoints, r.AvgPeriod, r.Diff1, r.Diff2, r.LossLimit, r.ProfLimit,
            r.Delta, opt.SpreadTenths, long.MinValue, pass, new List<Tx>(64));

        model.Deals.Sort((a, b) => a.OpenTimeUnix.CompareTo(b.OpenTimeUnix));
        for (int i = 0; i < model.Deals.Count; i++) model.Deals[i].PositionId = i + 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(model,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
        return model.Deals.Count;
    }

    private static void PrintYearBreakdown(Options opt, History h, long[] prefix, int n, Robot2Result r)
    {
        var years = new SortedDictionary<int, (int Entries, long PwsT, int Wins, int Mirrors)>();
        int w = r.AvgPeriod;
        int profP = r.ProfLimit * opt.PipPoints;
        int lossP = r.LossLimit * opt.PipPoints;
        var pass = new Pass
        {
            OnClose = (openBar, pwsT, win, dir, eNum, _) =>
            {
                int year = h.TimeUtc(openBar).Year;
                years.TryGetValue(year, out var acc);
                bool mirror = !win && MirrorWins(h, n, w, openBar, dir, eNum, profP, lossP);
                years[year] = (acc.Entries + 1, acc.PwsT + pwsT, acc.Wins + (win ? 1 : 0),
                    acc.Mirrors + (mirror ? 1 : 0));
            },
        };
        Simulate(h, prefix, n, opt.PipPoints, r.AvgPeriod, r.Diff1, r.Diff2, r.LossLimit, r.ProfLimit,
            r.Delta, opt.SpreadTenths, long.MinValue, pass, new List<Tx>(64));

        Console.WriteLine();
        Console.WriteLine($"============ BY YEAR (entry year, spread included): avg={BandSweep.Label(r.AvgPeriod)} " +
                          $"diff1={r.Diff1} diff2={r.Diff2} SL={r.LossLimit} TP={r.ProfLimit} delta={r.Delta} ============");
        Console.WriteLine("d/m/f = direct wins / direct lost but the opposite trade would have won / both lose");
        Console.WriteLine("year     entries      pips        d/m/f   direct%");
        foreach (var (year, acc) in years)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{year,-7}  {acc.Entries,6:N0}  {acc.PwsT / 10.0,8:N0}  {acc.Wins,5}/{acc.Mirrors}/{acc.Entries - acc.Wins - acc.Mirrors,-4}  " +
                $"{(acc.Entries == 0 ? 0 : 100.0 * acc.Wins / acc.Entries),6:F1}%"));
        }
    }
}
