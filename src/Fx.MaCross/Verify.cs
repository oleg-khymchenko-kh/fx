namespace Fx.MaCross;

internal static class Verify
{
    public static bool Run(History h, BarrierTable table, long[] prefix, int[] distPoints, int[] distPips,
        int[] periods, int[] riskGrid, int spreadTenths, int delay, double keep, int samples, int seed)
    {
        int n = h.Count;
        var rnd = new Random(seed);
        int bad = 0;

        Console.WriteLine($"Checking {samples:N0} random barrier lookups against a naive forward scan...");
        for (int s = 0; s < samples && bad < 10; s++)
        {
            int i = rnd.Next(n);
            int k = rnd.Next(distPoints.Length);
            int d = distPoints[k];

            int expectedUp = n;
            int expectedDown = n;
            long up = (long)h.Avg[i] + d;
            long down = (long)h.Avg[i] - d;
            for (int j = i + 1; j < n; j++)
            {
                if (expectedUp == n && h.Hi[j] >= up) expectedUp = j;
                if (expectedDown == n && h.Lo[j] <= down) expectedDown = j;
                if (expectedUp != n && expectedDown != n) break;
            }

            if (table.Up[k][i] != expectedUp)
            {
                Console.Error.WriteLine($"  UP mismatch at i={i} d={d}: table={table.Up[k][i]} naive={expectedUp}");
                bad++;
            }

            if (table.Down[k][i] != expectedDown)
            {
                Console.Error.WriteLine($"  DOWN mismatch at i={i} d={d}: table={table.Down[k][i]} naive={expectedDown}");
                bad++;
            }
        }

        if (bad > 0)
        {
            Console.Error.WriteLine("Barrier table verification FAILED.");
            return false;
        }

        Console.WriteLine("  barrier table ok");

        Console.WriteLine("Checking full strategy runs against a naive simulation...");
        var cross = new int[1 << 16];
        var crossNaive = new int[1 << 16];
        var firstLeg = new short[1 << 16];
        var netLeg = new short[1 << 16];
        for (int t = 0; t < 5; t++)
        {
            int a = rnd.Next(periods.Length);
            int b = rnd.Next(periods.Length);
            while (b == a) b = rnd.Next(periods.Length);
            int w1 = Math.Min(periods[a], periods[b]);
            int w2 = Math.Max(periods[a], periods[b]);
            int si = rnd.Next(distPips.Length);
            int ti = rnd.Next(distPips.Length);

            int c = FindCrossoversNaive(h.Avg, n, w1, w2, ref crossNaive);
            int cFast = Sweep.FindCrossovers(prefix, n, w1, w2, ref cross);
            if (c != cFast)
            {
                Console.Error.WriteLine($"  crossover count mismatch for A1={w1} A2={w2}: naive={c} fast={cFast}");
                return false;
            }

            for (int k = 0; k < c; k++)
            {
                if (cross[k] == crossNaive[k]) continue;
                Console.Error.WriteLine($"  crossover #{k} mismatch for A1={w1} A2={w2}: " +
                                        $"naive={crossNaive[k]} fast={cross[k]}");
                return false;
            }

            int riskBp = riskGrid[rnd.Next(riskGrid.Length)];
            double perTenth = riskBp / 10000.0 / (distPips[si] * 10);

            var seq = Sweep.BuildSequence(cross, cFast, n, delay,
                table.Up[ti], table.Down[si], table.Down[ti], table.Up[si],
                distPips[ti] * 10, distPips[si] * 10, spreadTenths, ref firstLeg, ref netLeg);
            bool alive = Sweep.RunEquity(firstLeg, netLeg, seq.Entries, perTenth, keep,
                out double equity, out double maxDd);

            var naive = SimulateNaive(h, cross, cFast, delay, distPoints[si], distPoints[ti], distPips[si], distPips[ti],
                spreadTenths, perTenth, keep);

            if (seq.Entries != naive.Entries || seq.Tenths != naive.Tenths || seq.DblWin != naive.DblWin ||
                seq.Mixed != naive.Mixed || seq.DblLoss != naive.DblLoss || alive != naive.Alive ||
                Math.Abs(equity - naive.Equity) > 1e-9 * Math.Max(1, Math.Abs(equity)) ||
                Math.Abs(maxDd - naive.MaxDd) > 1e-9)
            {
                Console.Error.WriteLine($"  run mismatch A1={w1} A2={w2} SL={distPips[si]} TP={distPips[ti]} risk={riskBp / 100.0:F2}%");
                Console.Error.WriteLine($"    fast : entries={seq.Entries} tenths={seq.Tenths} win={seq.DblWin}/{seq.Mixed}/{seq.DblLoss} alive={alive} equity={equity:R} dd={maxDd:R}");
                Console.Error.WriteLine($"    naive: entries={naive.Entries} tenths={naive.Tenths} win={naive.DblWin}/{naive.Mixed}/{naive.DblLoss} alive={naive.Alive} equity={naive.Equity:R} dd={naive.MaxDd:R}");
                return false;
            }

            Console.WriteLine($"  A1={w1,6} A2={w2,6} SL={distPips[si],4} TP={distPips[ti],4} risk={riskBp / 100.0,5:F2}% -> " +
                              $"{(alive ? "alive" : "blown")} equity x{equity,7:F3}, dd {maxDd,6:P1}, " +
                              $"{seq.Entries,6:N0} entries, {c:N0} crossovers  ok");
        }

        Console.WriteLine("Verification passed.");
        return true;
    }

    private static int FindCrossoversNaive(int[] avg, int n, int w1, int w2, ref int[] buffer)
    {
        int count = 0;
        int sign = 0;
        long s1 = 0;
        long s2 = 0;
        for (int j = w2 - w1; j <= w2 - 1; j++) s1 += avg[j];
        for (int j = 0; j <= w2 - 1; j++) s2 += avg[j];

        for (int i = w2 - 1; i < n; i++)
        {
            if (i > w2 - 1)
            {
                s1 += avg[i] - avg[i - w1];
                s2 += avg[i] - avg[i - w2];
            }

            long diff = s1 * w2 - s2 * w1;
            if (diff == 0) continue;
            int s = diff > 0 ? 1 : -1;
            if (s == sign) continue;
            if (sign != 0)
            {
                if (count == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                buffer[count++] = i;
            }

            sign = s;
        }

        return count;
    }

    internal readonly record struct NaiveRun(
        int Entries, long Tenths, int DblWin, int Mixed, int DblLoss, bool Alive, double Equity, double MaxDd);

    internal static NaiveRun SimulateNaive(History h, int[] cross, int crossCount, int delay, int slPoints,
        int tpPoints, int slPips, int tpPips, int spreadTenths, double perTenth, double keep)
    {
        int n = h.Count;
        long tenths = 0;
        int entries = 0, dblWin = 0, mixed = 0, dblLoss = 0;
        int nextAllowed = 0;

        double equity = 1;
        double peak = 1;
        double worst = 0;
        bool alive = true;

        for (int k = 0; k < crossCount; k++)
        {
            int i = cross[k] + delay;
            if (i >= n) break;
            if (i < nextAllowed) continue;

            long price = h.Avg[i];
            int buyExit = -1, sellExit = -1;
            bool buyWin = false, sellWin = false;
            for (int j = i + 1; j < n; j++)
            {
                if (buyExit < 0)
                {
                    bool tp = h.Hi[j] >= price + tpPoints;
                    bool sl = h.Lo[j] <= price - slPoints;
                    if (tp || sl)
                    {
                        buyExit = j;
                        buyWin = tp && !sl;
                    }
                }

                if (sellExit < 0)
                {
                    bool tp = h.Lo[j] <= price - tpPoints;
                    bool sl = h.Hi[j] >= price + slPoints;
                    if (tp || sl)
                    {
                        sellExit = j;
                        sellWin = tp && !sl;
                    }
                }

                if (buyExit >= 0 && sellExit >= 0) break;
            }

            if (buyExit < 0 || sellExit < 0) break;

            int buyPnl = (buyWin ? tpPips : -slPips) * 10 - spreadTenths;
            int sellPnl = (sellWin ? tpPips : -slPips) * 10 - spreadTenths;
            int netPnl = buyPnl + sellPnl;
            int firstPnl = buyExit == sellExit ? netPnl : buyExit < sellExit ? buyPnl : sellPnl;

            tenths += netPnl;
            entries++;
            if (buyWin && sellWin) dblWin++;
            else if (buyWin || sellWin) mixed++;
            else dblLoss++;

            nextAllowed = Math.Max(buyExit, sellExit) + 1;

            if (!alive) continue;

            double mid = equity * (1 + perTenth * firstPnl);
            if (mid > peak) peak = mid;
            else
            {
                double dd = 1 - mid / peak;
                if (dd > worst) worst = dd;
                if (mid < peak * keep)
                {
                    alive = false;
                    equity = mid;
                    worst = dd;
                    continue;
                }
            }

            equity *= 1 + perTenth * netPnl;
            if (equity > peak) peak = equity;
            else
            {
                double dd = 1 - equity / peak;
                if (dd > worst) worst = dd;
                if (equity < peak * keep)
                {
                    alive = false;
                    worst = dd;
                }
            }
        }

        return new NaiveRun(entries, tenths, dblWin, mixed, dblLoss, alive, equity, worst);
    }
}

