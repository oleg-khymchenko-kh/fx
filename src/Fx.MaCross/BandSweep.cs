using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Fx.MaCross;

internal readonly record struct BandResult(
    int Period, int BandPips, int DistancePips, char Variant, int SlPips, int TpPips, double RiskBp,
    double Equity, double MaxDd, long Tenths, int Entries, int Wins, int MirrorWins) : IScored
{
    public double Score => Equity;
    public double RiskPercent => RiskBp / 100.0;
    public double ReturnPercent => (Equity - 1) * 100;
    public double MaxDdPercent => MaxDd * 100;
    public double Pips => Tenths / 10.0;
    public double WinRate => Entries == 0 ? 0 : (double)Wins / Entries;
}

internal sealed class BlockIndex
{
    public const int Shift = 8;
    public const int Size = 1 << Shift;

    public int[] Max = [];
    public int[] Min = [];
    public int Blocks;

    public static BlockIndex Build(History h, int n)
    {
        int blocks = (n + Size - 1) >> Shift;
        var index = new BlockIndex { Max = new int[blocks], Min = new int[blocks], Blocks = blocks };
        for (int b = 0; b < blocks; b++)
        {
            int start = b << Shift;
            int end = Math.Min(n, start + Size);
            int max = int.MinValue;
            int min = int.MaxValue;
            for (int j = start; j < end; j++)
            {
                if (h.Hi[j] > max) max = h.Hi[j];
                if (h.Lo[j] < min) min = h.Lo[j];
            }

            index.Max[b] = max;
            index.Min[b] = min;
        }

        return index;
    }
}

internal sealed class BandEvents
{
    public int[] Index = new int[1 << 14];
    public sbyte[] Dir = new sbyte[1 << 14];
    public int[] Reach = new int[1 << 14];
    public int[] Level = new int[1 << 14];
    public bool[] FromBelow = new bool[1 << 14];
    public int[] Hits = [];
    public int Count;

    public void Add(int index, sbyte dir, int level, bool fromBelow)
    {
        if (Count == Index.Length)
        {
            Array.Resize(ref Index, Count * 2);
            Array.Resize(ref Dir, Count * 2);
            Array.Resize(ref Reach, Count * 2);
            Array.Resize(ref Level, Count * 2);
            Array.Resize(ref FromBelow, Count * 2);
        }

        Index[Count] = index;
        Dir[Count] = dir;
        Level[Count] = level;
        FromBelow[Count] = fromBelow;
        Count++;
    }

    public void EnsureHits(int distances)
    {
        int needed = Count * 2 * distances;
        if (Hits.Length < needed) Hits = new int[Math.Max(needed, 1 << 16)];
    }
}

internal static class BandSweep
{
    public const string VariantHelp =
        "A: touch the lower band from below -> SELL, touch the upper band from above -> BUY (fade the return into the channel)\n" +
        "B: touch the upper band from below -> BUY,  touch the lower band from above -> SELL (follow the breakout)\n" +
        "C: touch the upper band from below -> SELL, touch the lower band from above -> BUY  (fade the breakout)\n" +
        "D: touch the lower band from below -> BUY,  touch the upper band from above -> SELL (follow the return into the channel)";

    public static void FindEvents(long[] prefix, History h, BlockIndex blocks, int n, int period, int bandPoints,
        int maxReach, BandEvents inner, BandEvents outer)
    {
        inner.Count = 0;
        outer.Count = 0;
        int start = period + 1;
        if (start >= n) return;

        var hi = h.Hi;
        var lo = h.Lo;

        for (int i = start; i < n; i++)
        {
            long sma = (prefix[i] - prefix[i - period]) / period;
            int upper = (int)(sma + bandPoints);
            int lower = (int)(sma - bandPoints);
            int wasHigh = hi[i - 1];
            int wasLow = lo[i - 1];

            if (wasHigh < lower)
            {
                if (hi[i] >= lower) inner.Add(i, -1, lower, true);
            }
            else if (wasLow > lower)
            {
                if (lo[i] <= lower) outer.Add(i, -1, lower, false);
            }

            if (wasHigh < upper)
            {
                if (hi[i] >= upper) outer.Add(i, +1, upper, true);
            }
            else if (wasLow > upper)
            {
                if (lo[i] <= upper) inner.Add(i, +1, upper, false);
            }
        }

        FillReach(hi, lo, blocks, inner, maxReach);
        FillReach(hi, lo, blocks, outer, maxReach);
    }

    private static void FillReach(int[] hi, int[] lo, BlockIndex blocks, BandEvents events, int maxReach)
    {
        for (int k = 0; k < events.Count; k++)
        {
            int i = events.Index[k];
            int level = events.Level[k];
            events.Reach[k] = events.FromBelow[k]
                ? ReachFromBelow(hi, lo, blocks, i, level, maxReach)
                : ReachFromAbove(hi, lo, blocks, i, level, maxReach);
        }
    }

    private static int ReachFromBelow(int[] hi, int[] lo, BlockIndex blocks, int i, long level, int cap)
    {
        long min = long.MaxValue;
        int b = (i - 1) >> BlockIndex.Shift;
        int stop = b << BlockIndex.Shift;
        for (int j = i - 1; j >= stop && j >= 0; j--)
        {
            if (hi[j] > level) return Result(level, min, cap);
            if (lo[j] < min)
            {
                min = lo[j];
                if (level - min >= cap) return cap;
            }
        }

        for (b--; b >= 0; b--)
        {
            if (blocks.Max[b] <= level)
            {
                if (blocks.Min[b] < min)
                {
                    min = blocks.Min[b];
                    if (level - min >= cap) return cap;
                }

                continue;
            }

            int s = b << BlockIndex.Shift;
            for (int j = s + BlockIndex.Size - 1; j >= s; j--)
            {
                if (hi[j] > level) return Result(level, min, cap);
                if (lo[j] < min)
                {
                    min = lo[j];
                    if (level - min >= cap) return cap;
                }
            }
        }

        return Result(level, min, cap);

        static int Result(long level, long min, int cap)
        {
            if (min == long.MaxValue) return 0;
            long reach = level - min;
            return reach <= 0 ? 0 : (int)Math.Min(reach, cap);
        }
    }

    private static int ReachFromAbove(int[] hi, int[] lo, BlockIndex blocks, int i, long level, int cap)
    {
        long max = long.MinValue;
        int b = (i - 1) >> BlockIndex.Shift;
        int stop = b << BlockIndex.Shift;
        for (int j = i - 1; j >= stop && j >= 0; j--)
        {
            if (lo[j] < level) return Result(level, max, cap);
            if (hi[j] > max)
            {
                max = hi[j];
                if (max - level >= cap) return cap;
            }
        }

        for (b--; b >= 0; b--)
        {
            if (blocks.Min[b] >= level)
            {
                if (blocks.Max[b] > max)
                {
                    max = blocks.Max[b];
                    if (max - level >= cap) return cap;
                }

                continue;
            }

            int s = b << BlockIndex.Shift;
            for (int j = s + BlockIndex.Size - 1; j >= s; j--)
            {
                if (lo[j] < level) return Result(level, max, cap);
                if (hi[j] > max)
                {
                    max = hi[j];
                    if (max - level >= cap) return cap;
                }
            }
        }

        return Result(level, max, cap);

        static int Result(long level, long max, int cap)
        {
            if (max == long.MinValue) return 0;
            long reach = max - level;
            return reach <= 0 ? 0 : (int)Math.Min(reach, cap);
        }
    }

    public static void GatherHits(History h, BlockIndex blocks, int n, int[] distPoints, BandEvents events)
    {
        int d = distPoints.Length;
        events.EnsureHits(d);
        var hits = events.Hits;
        var hi = h.Hi;
        var lo = h.Lo;

        for (int k = 0; k < events.Count; k++)
        {
            int i = events.Index[k];
            long level = events.Level[k];
            int row = k * 2 * d;

            int from = i + 1;
            for (int q = 0; q < d; q++)
            {
                from = FirstHigh(hi, blocks, n, from, level + distPoints[q]);
                hits[row + q] = from;
                if (from >= n)
                {
                    for (int r = q + 1; r < d; r++) hits[row + r] = n;
                    break;
                }
            }

            from = i + 1;
            for (int q = 0; q < d; q++)
            {
                from = FirstLow(lo, blocks, n, from, level - distPoints[q]);
                hits[row + d + q] = from;
                if (from >= n)
                {
                    for (int r = q + 1; r < d; r++) hits[row + d + r] = n;
                    break;
                }
            }
        }
    }

    public static int FirstHigh(int[] hi, BlockIndex blocks, int n, int from, long target)
    {
        if (from >= n) return n;
        int b = from >> BlockIndex.Shift;
        if (blocks.Max[b] >= target)
        {
            int end = Math.Min(n, (b + 1) << BlockIndex.Shift);
            for (int j = from; j < end; j++)
                if (hi[j] >= target)
                    return j;
        }

        for (b++; b < blocks.Blocks; b++)
        {
            if (blocks.Max[b] < target) continue;
            int s = b << BlockIndex.Shift;
            int e = Math.Min(n, s + BlockIndex.Size);
            for (int j = s; j < e; j++)
                if (hi[j] >= target)
                    return j;
        }

        return n;
    }

    public static int FirstLow(int[] lo, BlockIndex blocks, int n, int from, long target)
    {
        if (from >= n) return n;
        int b = from >> BlockIndex.Shift;
        if (blocks.Min[b] <= target)
        {
            int end = Math.Min(n, (b + 1) << BlockIndex.Shift);
            for (int j = from; j < end; j++)
                if (lo[j] <= target)
                    return j;
        }

        for (b++; b < blocks.Blocks; b++)
        {
            if (blocks.Min[b] > target) continue;
            int s = b << BlockIndex.Shift;
            int e = Math.Min(n, s + BlockIndex.Size);
            for (int j = s; j < e; j++)
                if (lo[j] <= target)
                    return j;
        }

        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (int Entries, long Tenths, int Wins, int MirrorWins) BuildSequence(
        BandEvents events, int n, int distances, bool flip, int minReach, int slIdx, int tpIdx,
        int tpTenths, int slTenths, int winStart, int winEnd, ref short[] legs)
    {
        var idx = events.Index;
        var dirs = events.Dir;
        var reach = events.Reach;
        var hits = events.Hits;
        int count = events.Count;
        int stride = 2 * distances;
        var buffer = legs;
        int entries = 0;
        int wins = 0;
        int mirrorWins = 0;
        long tenths = 0;
        int nextAllowed = 0;

        for (int k = winStart == 0 ? 0 : LowerBound(idx, count, winStart); k < count; k++)
        {
            int i = idx[k];
            if (i >= winEnd) break;
            if (reach[k] < minReach) continue;
            if (i < nextAllowed) continue;

            int row = k * stride;
            int side = flip ? -dirs[k] : dirs[k];
            int tpHit = side > 0 ? hits[row + tpIdx] : hits[row + distances + tpIdx];
            int slHit = side > 0 ? hits[row + distances + slIdx] : hits[row + slIdx];
            bool win = tpHit < slHit;
            int exit = win ? tpHit : slHit;
            if (exit >= n) break;

            if (entries == buffer.Length)
            {
                Array.Resize(ref legs, buffer.Length * 2);
                buffer = legs;
            }

            int mirrorTp = side > 0 ? hits[row + distances + tpIdx] : hits[row + tpIdx];
            int mirrorSl = side > 0 ? hits[row + slIdx] : hits[row + distances + slIdx];

            int pnl = win ? tpTenths : -slTenths;
            buffer[entries] = (short)pnl;
            tenths += pnl;
            entries++;
            if (win) wins++;
            else if (mirrorTp < mirrorSl) mirrorWins++;
            nextAllowed = exit + 1;
        }

        return (entries, tenths, wins, mirrorWins);
    }

    public static double SizeForDrawdown(short[] legs, int entries, double perTenthMax, double targetDd,
        out double equity, out double drawdown)
    {
        Sweep.RunEquity(legs, legs, entries, perTenthMax, 0, out equity, out drawdown);
        if (drawdown <= targetDd) return perTenthMax;

        double lo = 0;
        double hi = perTenthMax;
        for (int i = 0; i < 20; i++)
        {
            double mid = 0.5 * (lo + hi);
            Sweep.RunEquity(legs, legs, entries, mid, 0, out _, out double dd);
            if (dd > targetDd) hi = mid;
            else lo = mid;
        }

        Sweep.RunEquity(legs, legs, entries, lo, 0, out equity, out drawdown);
        return lo;
    }

    private static int LowerBound(int[] values, int count, int target)
    {
        int lo = 0;
        int hi = count;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (values[mid] < target) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    public static List<Sweep.Trade> Detailed(long[] prefix, History h, BlockIndex blocks, int n,
        BandResult best, int pipPoints, int[] distPips, int spreadTenths, double perTenth)
    {
        var inner = new BandEvents();
        var outer = new BandEvents();
        var distPoints = distPips.Select(p => p * pipPoints).ToArray();
        FindEvents(prefix, h, blocks, n, best.Period, best.BandPips * pipPoints,
            50 * pipPoints, inner, outer);
        var events = best.Variant is 'A' or 'D' ? inner : outer;
        GatherHits(h, blocks, n, distPoints, events);

        bool flip = best.Variant is 'C' or 'D';
        int minReach = best.DistancePips * pipPoints;
        int d = distPoints.Length;
        int slIdx = Array.IndexOf(distPips, best.SlPips);
        int tpIdx = Array.IndexOf(distPips, best.TpPips);
        int tpTenths = best.TpPips * 10 - spreadTenths;
        int slTenths = best.SlPips * 10 + spreadTenths;

        var trades = new List<Sweep.Trade>();
        long cumulative = 0;
        double equity = 1;
        int nextAllowed = 0;

        for (int k = 0; k < events.Count; k++)
        {
            if (events.Reach[k] < minReach) continue;
            int i = events.Index[k];
            if (i < nextAllowed) continue;

            int row = k * 2 * d;
            int side = flip ? -events.Dir[k] : events.Dir[k];
            int tpHit = side > 0 ? events.Hits[row + tpIdx] : events.Hits[row + d + tpIdx];
            int slHit = side > 0 ? events.Hits[row + d + slIdx] : events.Hits[row + slIdx];
            bool win = tpHit < slHit;
            int exit = win ? tpHit : slHit;
            if (exit >= n) break;

            int pnl = win ? tpTenths : -slTenths;
            cumulative += pnl;
            equity *= 1 + perTenth * pnl;
            trades.Add(new Sweep.Trade(i, exit, exit, events.Level[k], side > 0, win, win, pnl, pnl,
                cumulative, equity, equity));
            nextAllowed = exit + 1;
        }

        return trades;
    }

    public static List<BandResult>[] Run(History h, BlockIndex blocks, long[] prefix, Options opt,
        int[] periods, int[] bandPips, int[] distancePips, int[] slGrid, int[] tpGrid, int[] riskGrid,
        int[] distPips, (int Start, int End)[] windows, int[] windowMinEntries,
        out long entriesTotal, out long survived, out long blown)
    {
        int n = h.Count;
        var distPoints = distPips.Select(p => p * opt.PipPoints).ToArray();
        int maxReach = distancePips[^1] * opt.PipPoints;
        var slIdx = slGrid.Select(p => Array.IndexOf(distPips, p)).ToArray();
        var tpIdx = tpGrid.Select(p => Array.IndexOf(distPips, p)).ToArray();

        var units = new List<(int Period, int Band)>();
        foreach (int p in periods)
        foreach (int b in bandPips)
            units.Add((p, b));

        double keep = 1 - opt.MaxDdPercent / 100;
        int next = -1;
        int done = 0;
        long totalEntries = 0;
        long totalAlive = 0;
        long totalDead = 0;
        var tops = new TopList<BandResult>[opt.Threads][];
        var workers = new Task[opt.Threads];
        var sw = Stopwatch.StartNew();

        for (int t = 0; t < opt.Threads; t++)
        {
            int slot = t;
            workers[slot] = Task.Run(() =>
            {
                var top = new TopList<BandResult>[windows.Length];
                for (int w = 0; w < windows.Length; w++) top[w] = new TopList<BandResult>(opt.TopKeep);
                tops[slot] = top;
                var inner = new BandEvents();
                var outer = new BandEvents();
                var legs = new short[1 << 16];
                long entries = 0, alive = 0, dead = 0;

                while (true)
                {
                    int u = Interlocked.Increment(ref next);
                    if (u >= units.Count) break;
                    var (period, band) = units[u];
                    FindEvents(prefix, h, blocks, n, period, band * opt.PipPoints, maxReach, inner, outer);
                    GatherHits(h, blocks, n, distPoints, inner);
                    GatherHits(h, blocks, n, distPoints, outer);

                    foreach (char variant in opt.Variants)
                    {
                        var events = variant is 'A' or 'D' ? inner : outer;
                        bool flip = variant is 'C' or 'D';
                        if (events.Count < opt.MinCrossovers) continue;

                        foreach (int distance in distancePips)
                        {
                            int minReach = distance * opt.PipPoints;

                            for (int ti = 0; ti < tpGrid.Length; ti++)
                            for (int si = 0; si < slGrid.Length; si++)
                            for (int w = 0; w < windows.Length; w++)
                            {
                                var seq = BuildSequence(events, n, distPoints.Length, flip, minReach,
                                    slIdx[si], tpIdx[ti], tpGrid[ti] * 10 - opt.SpreadTenths,
                                    slGrid[si] * 10 + opt.SpreadTenths,
                                    windows[w].Start, windows[w].End, ref legs);
                                entries += seq.Entries;
                                if (seq.Entries < windowMinEntries[w]) continue;

                                if (opt.NormDdPercent > 0)
                                {
                                    double perTenth = SizeForDrawdown(legs, seq.Entries,
                                        riskGrid[^1] / 10000.0 / (slGrid[si] * 10), opt.NormDdPercent / 100.0,
                                        out double normEquity, out double normDd);
                                    alive++;
                                    top[w].Offer(new BandResult(period, band, distance, variant, slGrid[si],
                                        tpGrid[ti], perTenth * slGrid[si] * 10 * 10000.0, normEquity, normDd,
                                        seq.Tenths, seq.Entries, seq.Wins, seq.MirrorWins));
                                    continue;
                                }

                                var bestOfSizes = default(BandResult);
                                bool any = false;
                                foreach (int riskBp in riskGrid)
                                {
                                    double perTenth = riskBp / 10000.0 / (slGrid[si] * 10);
                                    if (!Sweep.RunEquity(legs, legs, seq.Entries, perTenth, keep,
                                            out double eq, out double dd))
                                    {
                                        dead++;
                                        continue;
                                    }

                                    alive++;
                                    if (any && eq <= bestOfSizes.Equity) continue;
                                    any = true;
                                    bestOfSizes = new BandResult(period, band, distance, variant, slGrid[si],
                                        tpGrid[ti], riskBp, eq, dd, seq.Tenths, seq.Entries, seq.Wins,
                                        seq.MirrorWins);
                                }

                                if (any) top[w].Offer(bestOfSizes);
                            }
                        }
                    }

                    Interlocked.Increment(ref done);
                }

                Interlocked.Add(ref totalEntries, entries);
                Interlocked.Add(ref totalAlive, alive);
                Interlocked.Add(ref totalDead, dead);
            });
        }

        var all = Task.WhenAll(workers);
        while (!all.Wait(5000))
        {
            int d = Volatile.Read(ref done);
            double share = (double)d / units.Count;
            double eta = share > 0.001 ? sw.Elapsed.TotalSeconds * (1 - share) / share : 0;
            Console.WriteLine($"  {d:N0}/{units.Count:N0} period/band pairs ({share:P1})  " +
                              $"elapsed {sw.Elapsed.TotalSeconds:F0}s  eta {eta:F0}s");
        }

        if (all.IsFaulted) throw all.Exception!.GetBaseException();

        entriesTotal = totalEntries;
        survived = totalAlive;
        blown = totalDead;
        var merged = new List<BandResult>[windows.Length];
        for (int w = 0; w < windows.Length; w++)
        {
            int slot = w;
            merged[w] = tops.Where(x => x is not null).SelectMany(x => x[slot].Items)
                .OrderByDescending(r => r.Equity)
                .Take(opt.TopKeep)
                .ToList();
        }

        return merged;
    }

    public static void PrintTop(Options opt, IReadOnlyList<BandResult> rows, int count, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"==================== {title} ====================");
        Console.WriteLine("  #  var   MA period  band  dist    SL    TP   risk        final       return   maxDD  entries   win%");
        for (int i = 0; i < Math.Min(count, rows.Count); i++)
        {
            var r = rows[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}    {r.Variant}  {Label(r.Period),10}  {r.BandPips,4}  {r.DistancePips,4}  {r.SlPips,4}  " +
                $"{r.TpPips,4}  {r.RiskPercent,4:F2}%  {opt.Deposit * r.Equity,11:N0}  {r.ReturnPercent,10:N0}%  " +
                $"{r.MaxDdPercent,5:F1}%  {r.Entries,7:N0}  {r.WinRate,5:P1}"));
        }
    }

    public static string Label(int minutes) => minutes switch
    {
        < 1440 => $"{minutes / 60.0:0.##}h",
        _ => $"{minutes / 1440.0:0.##}d",
    };
}

