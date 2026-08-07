namespace Fx.MaCross;

internal static class BandVerify
{
    public static bool Run(History h, BlockIndex blocks, long[] prefix, Options opt, int[] distPips, int samples)
    {
        int n = h.Count;
        var rnd = new Random(9001);
        var distPoints = distPips.Select(p => p * opt.PipPoints).ToArray();

        Console.WriteLine($"Checking {samples:N0} random band events against naive scans...");

        var inner = new BandEvents();
        var outer = new BandEvents();
        int maxReach = opt.DistMax * opt.PipPoints;

        for (int trial = 0; trial < 4; trial++)
        {
            int period = new[] { 60, 240, 1440, 7200 }[trial];
            int band = new[] { 0, 10, 25, 50 }[trial] * opt.PipPoints;
            BandSweep.FindEvents(prefix, h, blocks, n, period, band, maxReach, inner, outer);

            foreach (var events in new[] { inner, outer })
            {
                if (events.Count == 0) continue;
                BandSweep.GatherHits(h, blocks, n, distPoints, events);

                for (int s = 0; s < samples / 8; s++)
                {
                    int k = rnd.Next(events.Count);
                    int i = events.Index[k];
                    long level = events.Level[k];

                    long sma = (prefix[i] - prefix[i - period]) / period;
                    if (level != sma + band && level != sma - band)
                    {
                        Console.Error.WriteLine($"  band level mismatch at bar {i}: stored={level} sma={sma} band={band}");
                        return false;
                    }

                    if (events.FromBelow[k] ? h.Hi[i] < level : h.Lo[i] > level)
                    {
                        Console.Error.WriteLine($"  bar {i} does not reach the level {level} it claims to touch");
                        return false;
                    }

                    if (events.FromBelow[k] ? h.Hi[i - 1] >= level : h.Lo[i - 1] <= level)
                    {
                        Console.Error.WriteLine(
                            $"  pending order at {level} on bar {i} was not on the fillable side of the market: " +
                            $"previous bar hi={h.Hi[i - 1]} lo={h.Lo[i - 1]} fromBelow={events.FromBelow[k]}");
                        return false;
                    }

                    for (int q = 0; q < distPoints.Length; q++)
                    {
                        int up = n, down = n;
                        for (int j = i + 1; j < n; j++)
                        {
                            if (up == n && h.Hi[j] >= level + distPoints[q]) up = j;
                            if (down == n && h.Lo[j] <= level - distPoints[q]) down = j;
                            if (up != n && down != n) break;
                        }

                        int row = k * 2 * distPoints.Length;
                        if (events.Hits[row + q] != up || events.Hits[row + distPoints.Length + q] != down)
                        {
                            Console.Error.WriteLine(
                                $"  hit mismatch at bar {i} dist {distPips[q]}: up table={events.Hits[row + q]} " +
                                $"naive={up}, down table={events.Hits[row + distPoints.Length + q]} naive={down}");
                            return false;
                        }
                    }

                    int naiveReach = NaiveReach(h, i, level, events.FromBelow[k], maxReach);
                    if (events.Reach[k] != naiveReach)
                    {
                        Console.Error.WriteLine($"  reach mismatch at bar {i} level {level} " +
                                                $"fromBelow={events.FromBelow[k]}: table={events.Reach[k]} naive={naiveReach}");
                        return false;
                    }
                }
            }
        }

        Console.WriteLine("  band events, barrier hits and travelled distance ok");
        return true;
    }

    private static int NaiveReach(History h, int i, long level, bool fromBelow, int cap)
    {
        if (fromBelow)
        {
            long min = long.MaxValue;
            for (int j = i - 1; j >= 0; j--)
            {
                if (h.Hi[j] > level) break;
                if (h.Lo[j] < min) min = h.Lo[j];
            }

            if (min == long.MaxValue) return 0;
            long reach = level - min;
            return reach <= 0 ? 0 : (int)Math.Min(reach, cap);
        }

        long max = long.MinValue;
        for (int j = i - 1; j >= 0; j--)
        {
            if (h.Lo[j] < level) break;
            if (h.Hi[j] > max) max = h.Hi[j];
        }

        if (max == long.MinValue) return 0;
        long up = max - level;
        return up <= 0 ? 0 : (int)Math.Min(up, cap);
    }
}

