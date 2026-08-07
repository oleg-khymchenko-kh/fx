using System.Diagnostics;
using System.Globalization;

namespace Fx.MaCross;

internal readonly record struct ChainResult(
    int W1, int W2, int ChainLen, int ChainWins, int ChainStartBar, int ChainEndBar,
    int Crossings, int Wins) : IScored
{
    public double Score => ChainLen + ChainWins / 1e6;
    public int ChainLosses => ChainLen - ChainWins;
    public double WinRate => Crossings == 0 ? 0 : (double)Wins / Crossings;
    public double ChainShare => Crossings == 0 ? 0 : (double)ChainLen / Crossings;
}

internal static class ChainSweep
{
    public static int Run(Options opt, History h, BarrierTable table, long[] prefix, int[] distPips,
        List<(int W1, int W2)> pairs)
    {
        int n = h.Count;
        int slIdx = Array.IndexOf(distPips, opt.SlMin);
        int tpIdx = Array.IndexOf(distPips, opt.TpMin);
        var labels = BuildLabels(table, n, slIdx, tpIdx, out var exits, out long good, out long bad, out long open);

        Console.WriteLine();
        Console.WriteLine($"Per-minute outcome with SL {opt.SlMin} / TP {opt.TpMin} pips, entry at the bar average:");
        Console.WriteLine($"  some side wins:  {good,11:N0} ({(double)good / n:P1})");
        Console.WriteLine($"  both sides lose: {bad,11:N0} ({(double)bad / n:P1})");
        Console.WriteLine($"  still open:      {open,11:N0}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  random walk expects {2.0 * opt.SlMin / (opt.SlMin + opt.TpMin):P1} winning minutes"));
        Console.WriteLine();
        if (opt.ChainStrict)
        {
            Console.WriteLine("Chain rule: strict, every loss ends the chain.");
        }
        else
        {
            Console.WriteLine("Chain rule: a single loss stays inside the chain only when it has at least");
            Console.WriteLine("2 wins in a row right before it and at least 2 wins in a row right after it.");
        }

        if (opt.ChainOneTrade)
            Console.WriteLine("One decision at a time: crossovers during an open trade are skipped.");

        int[]? exPrefix = null;
        if (opt.SkipMonths.Count > 0)
        {
            exPrefix = BuildExcludedPrefix(h, n, opt.SkipMonths);
            Console.WriteLine("Skipped months: " +
                              string.Join(", ", opt.SkipMonths.Select(r => $"{MonthLabel(r.From)}..{MonthLabel(r.To)}")) +
                              $" ({exPrefix[n]:N0} bars dropped, chains break at every gap)");
        }

        Console.WriteLine($"Scanning {pairs.Count:N0} MA pairs, entry delay {opt.EntryDelay} bar(s)...");

        var sw = Stopwatch.StartNew();
        int nextPair = -1;
        int done = 0;
        var tops = new TopList<ChainResult>[opt.Threads];
        var workers = new Task[opt.Threads];
        for (int t = 0; t < opt.Threads; t++)
        {
            int slot = t;
            workers[slot] = Task.Run(() =>
            {
                var top = new TopList<ChainResult>(opt.TopKeep);
                tops[slot] = top;
                var cross = new int[1 << 16];
                var seqLab = new byte[1 << 16];
                var seqBar = new int[1 << 16];
                while (true)
                {
                    int p = Interlocked.Increment(ref nextPair);
                    if (p >= pairs.Count) break;
                    var (w1, w2) = pairs[p];
                    int c = Sweep.FindCrossovers(prefix, n, w1, w2, ref cross);
                    if (seqLab.Length < 2 * c + 2)
                    {
                        seqLab = new byte[2 * cross.Length + 2];
                        seqBar = new int[2 * cross.Length + 2];
                    }

                    int m = 0;
                    int wins = 0;
                    int resolved = 0;
                    int nextAllowed = 0;
                    for (int k = 0; k < c; k++)
                    {
                        int i = cross[k] + opt.EntryDelay;
                        if (i >= n) break;
                        if (exPrefix != null && exPrefix[i + 1] > exPrefix[i]) continue;
                        if (opt.ChainOneTrade && i < nextAllowed) continue;
                        if (exPrefix != null && m > 0 && exPrefix[i] > exPrefix[seqBar[m - 1] + 1])
                        {
                            seqLab[m] = 2;
                            seqBar[m] = i;
                            m++;
                        }

                        byte lab = labels[i];
                        seqLab[m] = lab;
                        seqBar[m] = i;
                        m++;
                        if (lab == 1) wins++;
                        if (lab != 2) resolved++;
                        if (opt.ChainOneTrade)
                        {
                            int exit = exits[i];
                            if (exit >= n) break;
                            nextAllowed = exit + 1;
                        }
                    }

                    var r = LongestChain(seqLab, seqBar, m, opt.ChainStrict);
                    top.Offer(new ChainResult(w1, w2, r.Len, r.Wins, r.StartBar, r.EndBar, resolved, wins));
                    Interlocked.Increment(ref done);
                }
            });
        }

        var all = Task.WhenAll(workers);
        while (!all.Wait(2000))
        {
            int d = Volatile.Read(ref done);
            double share = (double)d / pairs.Count;
            double eta = share > 0.001 ? sw.Elapsed.TotalSeconds * (1 - share) / share : 0;
            Console.WriteLine($"  {d:N0}/{pairs.Count:N0} pairs ({share:P1})  " +
                              $"elapsed {sw.Elapsed.TotalSeconds:F0}s  eta {eta:F0}s");
        }

        if (all.IsFaulted)
        {
            Console.Error.WriteLine(all.Exception?.GetBaseException().ToString());
            return 1;
        }

        Console.WriteLine($"Chain sweep done in {sw.Elapsed.TotalSeconds:F1}s");

        var best = tops.Where(x => x is not null).SelectMany(x => x.Items)
            .OrderByDescending(r => r.Score)
            .Take(opt.TopKeep)
            .ToList();

        if (best.Count == 0)
        {
            Console.Error.WriteLine("No MA pair produced a chain.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"==================== TOP {Math.Min(opt.TopPrint, best.Count)} CHAINS: SL {opt.SlMin}, TP {opt.TpMin} ====================");
        Console.WriteLine("  #       A1       A2  chain   wins  loss              from                to  crossings   win%  share");
        for (int i = 0; i < Math.Min(opt.TopPrint, best.Count); i++)
        {
            var r = best[i];
            string from = r.ChainLen == 0 ? "-" : $"{h.TimeUtc(r.ChainStartBar):yyyy-MM-dd HH:mm}";
            string to = r.ChainLen == 0 ? "-" : $"{h.TimeUtc(r.ChainEndBar):yyyy-MM-dd HH:mm}";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}  {BandSweep.Label(r.W1),7}  {BandSweep.Label(r.W2),7}  {r.ChainLen,5}  {r.ChainWins,5}  " +
                $"{r.ChainLosses,4}  {from,16}  {to,16}  {r.Crossings,9:N0}  {r.WinRate,5:P1}  {r.ChainShare,5:P2}"));
        }

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);
        string csvName = "-chain" + (opt.ChainStrict ? "-strict" : "") +
                         (opt.ChainOneTrade ? "-seq" : "") + "-top.csv";
        string csvPath = Path.Combine(outDir, opt.Symbol.ToLowerInvariant() + csvName);
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("rank,a1Minutes,a2Minutes,chainTrades,chainWins,chainLosses,chainFromUtc,chainToUtc," +
                        "crossings,wins,winRate,chainShare");
            for (int i = 0; i < best.Count; i++)
            {
                var r = best[i];
                string from = r.ChainLen == 0 ? "" : $"{h.TimeUtc(r.ChainStartBar):yyyy-MM-dd HH:mm}";
                string to = r.ChainLen == 0 ? "" : $"{h.TimeUtc(r.ChainEndBar):yyyy-MM-dd HH:mm}";
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{r.W1},{r.W2},{r.ChainLen},{r.ChainWins},{r.ChainLosses},{from},{to}," +
                    $"{r.Crossings},{r.Wins},{r.WinRate:F4},{r.ChainShare:F5}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Chain CSV:  {csvPath} ({best.Count} rows)");
        return 0;
    }

    private static byte[] BuildLabels(BarrierTable table, int n, int slIdx, int tpIdx,
        out int[] exits, out long good, out long bad, out long open)
    {
        var upTp = table.Up[tpIdx];
        var dnSl = table.Down[slIdx];
        var dnTp = table.Down[tpIdx];
        var upSl = table.Up[slIdx];
        var labels = new byte[n];
        exits = new int[n];
        long g = 0, b = 0, o = 0;
        for (int i = 0; i < n; i++)
        {
            if (upTp[i] < dnSl[i])
            {
                labels[i] = 1;
                exits[i] = upTp[i];
                g++;
            }
            else if (dnTp[i] < upSl[i])
            {
                labels[i] = 1;
                exits[i] = dnTp[i];
                g++;
            }
            else if (dnSl[i] < n && upSl[i] < n)
            {
                exits[i] = Math.Max(dnSl[i], upSl[i]);
                b++;
            }
            else
            {
                labels[i] = 2;
                exits[i] = n;
                o++;
            }
        }

        good = g;
        bad = b;
        open = o;
        return labels;
    }

    private static int[] BuildExcludedPrefix(History h, int n, List<(int From, int To)> ranges)
    {
        var excluded = new bool[n];
        foreach (var (from, to) in ranges)
        {
            int a = LowerBound(h.Ts, n, MonthUnix(from));
            int b = LowerBound(h.Ts, n, MonthUnix(to + 1));
            for (int i = a; i < b; i++) excluded[i] = true;
        }

        var prefix = new int[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + (excluded[i] ? 1 : 0);
        return prefix;
    }

    private static long MonthUnix(int key)
    {
        int year = (key - 1) / 12;
        int month = key - year * 12;
        return new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    private static string MonthLabel(int key)
    {
        int year = (key - 1) / 12;
        int month = key - year * 12;
        return $"{year:D4}-{month:D2}";
    }

    private static int LowerBound(long[] a, int n, long value)
    {
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] < value) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static (int Len, int Wins, int StartBar, int EndBar) LongestChain(byte[] lab, int[] bar, int m,
        bool strict)
    {
        int bestLen = 0, bestWins = 0, bestStart = 0, bestEnd = 0;
        int cur = 0, curWins = 0, curStart = 0;
        int prevWinLen = 0;
        int gap = 1 << 30;
        bool gapUnknown = false;
        int pos = 0;
        while (pos < m)
        {
            byte v = lab[pos];
            int runStart = pos;
            while (pos < m && lab[pos] == v) pos++;
            int len = pos - runStart;
            if (v != 1)
            {
                if (gap < 1 << 30) gap += len;
                gapUnknown |= v == 2;
                continue;
            }

            bool join = !strict && cur > 0 && gap == 1 && !gapUnknown && prevWinLen >= 2 && len >= 2;
            if (join)
            {
                cur += 1 + len;
                curWins += len;
            }
            else
            {
                cur = len;
                curWins = len;
                curStart = bar[runStart];
            }

            if (cur > bestLen || (cur == bestLen && curWins > bestWins))
            {
                bestLen = cur;
                bestWins = curWins;
                bestStart = curStart;
                bestEnd = bar[pos - 1];
            }

            prevWinLen = len;
            gap = 0;
            gapUnknown = false;
        }

        return (bestLen, bestWins, bestStart, bestEnd);
    }
}
