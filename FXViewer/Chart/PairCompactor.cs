namespace FXViewer.Chart;

public readonly record struct MinuteSpan(long MinuteUnixSeconds, long Low, long High);

public sealed record CompactLayout(long[] GridSteps, long MaxDistance);

public static class PairCompactor
{
    public static CompactLayout Solve(IReadOnlyList<IReadOnlyList<MinuteSpan>> pairs, long gridStep)
    {
        int n = pairs.Count;
        if (n == 0) return new CompactLayout(Array.Empty<long>(), 0);
        var reach = ReachMatrix(pairs.Select(OneSpanPerMinute).ToArray());
        var best = new long[n];
        long feasible = MaxDistance(reach, best, gridStep);
        long infeasible = MaxOwnHeight(reach) - 1;
        while (feasible - infeasible > 1)
        {
            long limit = infeasible + (feasible - infeasible) / 2;
            if (StepsWithin(reach, limit, gridStep) is { } steps)
            {
                feasible = limit;
                best = steps;
            }
            else
            {
                infeasible = limit;
            }
        }
        return new CompactLayout(Tighten(reach, best, feasible, gridStep), feasible);
    }

    private static MinuteSpan[] OneSpanPerMinute(IReadOnlyList<MinuteSpan> spans)
    {
        var sorted = spans.ToArray();
        Array.Sort(sorted, (a, b) => a.MinuteUnixSeconds.CompareTo(b.MinuteUnixSeconds));
        var merged = new List<MinuteSpan>(sorted.Length);
        foreach (var span in sorted)
        {
            if (merged.Count == 0 || merged[^1].MinuteUnixSeconds != span.MinuteUnixSeconds)
            {
                merged.Add(span);
                continue;
            }
            var last = merged[^1];
            merged[^1] = last with
            {
                Low = Math.Min(last.Low, span.Low),
                High = Math.Max(last.High, span.High),
            };
        }
        return merged.ToArray();
    }

    private static long[,] ReachMatrix(MinuteSpan[][] pairs)
    {
        int n = pairs.Length;
        var reach = new long[n, n];
        for (int i = 0; i < n; i++)
        {
            reach[i, i] = pairs[i].Max(s => s.High - s.Low);
            for (int j = i + 1; j < n; j++)
                (reach[i, j], reach[j, i]) = CrossReach(pairs[i], pairs[j]);
        }
        return reach;
    }

    private static (long Up, long Down) CrossReach(MinuteSpan[] a, MinuteSpan[] b)
    {
        long up = long.MinValue;
        long down = long.MinValue;
        int ia = 0;
        int ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            long ta = a[ia].MinuteUnixSeconds;
            long tb = b[ib].MinuteUnixSeconds;
            if (ta != tb)
            {
                if (ta < tb) ia++;
                else ib++;
                continue;
            }
            up = Math.Max(up, a[ia].High - b[ib].Low);
            down = Math.Max(down, b[ib].High - a[ia].Low);
            ia++;
            ib++;
        }
        if (up != long.MinValue) return (up, down);
        return (a.Max(s => s.High) - b.Min(s => s.Low), b.Max(s => s.High) - a.Min(s => s.Low));
    }

    private static long MaxOwnHeight(long[,] reach)
    {
        long max = long.MinValue;
        for (int i = 0; i < reach.GetLength(0); i++) max = Math.Max(max, reach[i, i]);
        return max;
    }

    private static long MaxDistance(long[,] reach, long[] steps, long gridStep)
    {
        long max = long.MinValue;
        for (int i = 0; i < steps.Length; i++)
            for (int j = 0; j < steps.Length; j++)
                max = Math.Max(max, reach[i, j] + gridStep * (steps[i] - steps[j]));
        return max;
    }

    private static long PairDistanceSum(long[,] reach, long[] steps, long gridStep)
    {
        long sum = 0;
        for (int i = 0; i < steps.Length; i++)
            for (int j = i + 1; j < steps.Length; j++)
            {
                long lift = gridStep * (steps[i] - steps[j]);
                sum += Math.Max(reach[i, j] + lift, reach[j, i] - lift);
            }
        return sum;
    }

    private static long[]? StepsWithin(long[,] reach, long limit, long gridStep)
    {
        int n = reach.GetLength(0);
        for (int i = 0; i < n; i++)
            if (reach[i, i] > limit) return null;
        var steps = new long[n];
        for (int pass = 0; pass <= n; pass++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    long highest = steps[j] + FloorDiv(limit - reach[i, j], gridStep);
                    if (steps[i] <= highest) continue;
                    steps[i] = highest;
                    changed = true;
                }
            if (!changed) return steps;
        }
        return null;
    }

    private static long[] Tighten(long[,] reach, long[] steps, long limit, long gridStep)
    {
        int n = steps.Length;
        var trial = new long[n];
        long sum = PairDistanceSum(reach, steps, gridStep);
        while (true)
        {
            var best = steps;
            long bestSum = sum;
            for (int mask = 1; mask < 1 << (n - 1); mask++)
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    for (int i = 0; i < n; i++)
                        trial[i] = steps[i] + (i > 0 && ((mask >> (i - 1)) & 1) != 0 ? dir : 0);
                    if (MaxDistance(reach, trial, gridStep) > limit) continue;
                    long trialSum = PairDistanceSum(reach, trial, gridStep);
                    if (trialSum >= bestSum) continue;
                    bestSum = trialSum;
                    best = (long[])trial.Clone();
                }
            if (bestSum == sum) return steps;
            steps = best;
            sum = bestSum;
        }
    }

    private static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
