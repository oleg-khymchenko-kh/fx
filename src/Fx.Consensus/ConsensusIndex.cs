using System.Buffers;
using System.Diagnostics;

namespace Fx.Consensus;

public static class ConsensusIndex
{
    public const int MaxScale = 65536;
    public const int RecentMinutes = 72000;
    public const int WrittenMinutes = RecentMinutes + MaxScale;
    public const int SolvedMinutes = RecentMinutes + 2 * MaxScale;
    public const double RecencyFloor = 0.1;
    public const int LongFrom = 1440;
    public const double LongFactor = 1.4142;
    public const int OverlapShort = 4;
    public const int OverlapLong = 16;
    public const int AverageFrom = 256;
    public const int AverageDiv = 256;
    public const double RelativeTolerance = 0.15;
    public const double NoiseFactor = 0.5;
    public const double MinFloor = 0.5;
    public const int MeanShiftRounds = 12;
    public const double StrengthFloor = 1e-6;
    public const double Anchor = 1e3;
    public const double CgTolerance = 1e-8;
    public const int CgMaxIterations = 20000;
    private const int MinFloorBlock = 8;
    private const int BlockMinutes = 16384;
    private const int MaxBlocks = 64;
    private const int MaxHalf = MaxScale / AverageDiv;

    public static double PairWeight(string pair, bool heavy = false) => pair.ToUpperInvariant() switch
    {
        "EURUSD" => heavy ? 10 : 6,
        "GBPUSD" => heavy ? 8 : 5,
        "USDCHF" => 4,
        "USDCAD" => 3,
        "NZDUSD" => 2,
        "AUDUSD" => 1,
        _ => 1,
    };

    public sealed record Options(bool ReturnIntervals = false, double ReturnStrength = 1.0)
    {
        public static readonly Options Default = new();
        public static readonly Options Anchored = new(true, 1.0);
    }

    public sealed class Frame
    {
        public int[] Scales = [];
        public int[] Steps = [];
        public int[] Halves = [];
        public int[] Counts = [];
        public int[] Offsets = [];
        public int Total;
        public int[] ExtraA = [];
        public int[] ExtraB = [];
        public int[] ExtraHalf = [];
        public int[] ExtraScale = [];
        public int[] ExtraSortedB = [];
        public int[] ExtraByB = [];
        public int ExtraCount => ExtraA.Length;
        public int All => Total + ExtraA.Length;
    }

    public sealed record Result(double[] Index, int Minutes, int Blocks, int Intervals, int Returns, int CgSteps,
        long StageAMs, long StageBMs, long MatVecMs)
    {
        public string Summary =>
            $"{Minutes:N0} minutes in {Blocks} block(s), {Intervals:N0} intervals ({Returns:N0} returns), " +
            $"stage A {StageAMs} ms, stage B {CgSteps} CG steps {StageBMs} ms (matvec {MatVecMs} ms)";
    }

    private sealed class Totals
    {
        public int Blocks, Intervals, Returns, CgSteps;
        public long StageAMs, StageBMs, MatVecMs;
    }

    public static Frame BuildFrame(int minutes)
    {
        var scales = new List<int> { 1 };
        while (true)
        {
            int s = scales[^1];
            int next = Math.Max(s + 1, (int)Math.Round(s * (s >= LongFrom ? LongFactor : 2.0)));
            if (next > MaxScale || next >= minutes) break;
            scales.Add(next);
        }
        var f = new Frame
        {
            Scales = scales.ToArray(),
            Steps = new int[scales.Count],
            Halves = new int[scales.Count],
            Counts = new int[scales.Count],
            Offsets = new int[scales.Count],
        };
        int total = 0;
        for (int k = 0; k < scales.Count; k++)
        {
            int s = scales[k];
            f.Steps[k] = Math.Max(1, s / (s >= LongFrom ? OverlapLong : OverlapShort));
            f.Halves[k] = s >= AverageFrom ? s / AverageDiv : 0;
            int span = minutes - s - 2 * f.Halves[k];
            f.Counts[k] = span > 0 ? (span + f.Steps[k] - 1) / f.Steps[k] : 0;
            f.Offsets[k] = total;
            total += f.Counts[k];
        }
        f.Total = total;
        return f;
    }

    public static Result Compute(double[][] x, double[] pairWeights, CancellationToken ct,
        Action<double>? progress = null, Options? options = null)
    {
        int minutes = x[0].Length;
        var w = Normalized(pairWeights);
        var o = options ?? Options.Default;
        var u = new double[minutes];
        var totals = new Totals();
        int end = Math.Min(minutes, SolvedMinutes);
        int written = 0;
        while (true)
        {
            int from = Math.Max(0, end - SolvedMinutes);
            var block = SolveBlock(x, from, end, w, o, ct, totals);
            int useFrom = written == 0 ? from : Math.Max(from, end - WrittenMinutes);
            double shift = written == 0 ? -block[0] : u[useFrom] - block[useFrom - from];
            for (int t = useFrom; t < end; t++) u[t] = block[t - from] + shift;
            written = end;
            progress?.Invoke(end / (double)minutes);
            if (end >= minutes) break;
            end = Math.Min(minutes, end + RecentMinutes);
        }
        return new Result(u, minutes, totals.Blocks, totals.Intervals, totals.Returns, totals.CgSteps,
            totals.StageAMs, totals.StageBMs, totals.MatVecMs);
    }

    public static Result Continue(double[][] x, double[] pairWeights, int from, double knownAtFrom,
        CancellationToken ct, Options? options = null)
    {
        int minutes = x[0].Length;
        var w = Normalized(pairWeights);
        var o = options ?? Options.Default;
        var totals = new Totals();
        int solveFrom = Math.Max(0, from - MaxScale);
        var block = SolveBlock(x, solveFrom, minutes, w, o, ct, totals);
        var u = new double[minutes - from];
        double shift = knownAtFrom - block[from - solveFrom];
        for (int t = from; t < minutes; t++) u[t - from] = block[t - solveFrom] + shift;
        return new Result(u, minutes - from, totals.Blocks, totals.Intervals, totals.Returns, totals.CgSteps,
            totals.StageAMs, totals.StageBMs, totals.MatVecMs);
    }

    private static double[] Normalized(double[] weights)
    {
        double sum = weights.Sum();
        return weights.Select(v => v / sum).ToArray();
    }

    private static double[] SolveBlock(double[][] x, int from, int to, double[] w, Options o, CancellationToken ct,
        Totals totals)
    {
        int n = x.Length;
        int minutes = to - from;
        var xb = new double[n][];
        for (int i = 0; i < n; i++)
        {
            xb[i] = new double[minutes];
            Array.Copy(x[i], from, xb[i], 0, minutes);
        }
        var frame = BuildFrame(minutes);
        if (o.ReturnIntervals) AddReturnIntervals(frame, xb, w);
        var sw = Stopwatch.StartNew();
        var (m, h) = StageA(xb, w, frame, o, ct);
        totals.StageAMs += sw.ElapsedMilliseconds;
        sw.Restart();
        var u = StageB(frame, m, h, minutes, MedianIndex(xb), ct, out int steps, out long matVecMs);
        totals.StageBMs += sw.ElapsedMilliseconds;
        totals.MatVecMs += matVecMs;
        totals.CgSteps += steps;
        totals.Intervals += frame.All;
        totals.Returns += frame.ExtraCount;
        totals.Blocks++;
        return u;
    }

    public static (int First, int Second) HeaviestPairs(double[] w)
    {
        int first = 0;
        for (int i = 1; i < w.Length; i++)
            if (w[i] > w[first]) first = i;
        int second = -1;
        for (int i = 0; i < w.Length; i++)
            if (i != first && (second < 0 || w[i] > w[second])) second = i;
        return (first, second);
    }

    public static int[] NextReturns(double[] spread)
    {
        int n = spread.Length;
        var nextLow = new int[n];
        var nextHigh = new int[n];
        var stack = new int[n];
        int top = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            while (top > 0 && spread[stack[top - 1]] > spread[i]) top--;
            nextLow[i] = top > 0 ? stack[top - 1] : -1;
            stack[top++] = i;
        }
        top = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            while (top > 0 && spread[stack[top - 1]] < spread[i]) top--;
            nextHigh[i] = top > 0 ? stack[top - 1] : -1;
            stack[top++] = i;
        }
        var result = new int[n];
        for (int t = 0; t < n; t++)
        {
            if (t == n - 1) result[t] = -1;
            else if (spread[t + 1] > spread[t]) result[t] = nextLow[t];
            else if (spread[t + 1] < spread[t]) result[t] = nextHigh[t];
            else result[t] = t + 1;
        }
        return result;
    }

    public static int NearestScale(Frame f, int length)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        for (int k = 0; k < f.Scales.Length; k++)
        {
            if (f.Counts[k] == 0) continue;
            double dist = Math.Abs(Math.Log(length / (double)f.Scales[k]));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = k;
            }
        }
        return best;
    }

    public static void AddReturnIntervals(Frame f, double[][] x, double[] w)
    {
        int n = x.Length;
        if (n < 2) return;
        int minutes = x[0].Length;
        var (first, second) = HeaviestPairs(w);
        var spread = new double[minutes];
        for (int t = 0; t < minutes; t++) spread[t] = x[first][t] - x[second][t];
        var returns = NextReturns(spread);
        var a = new List<int>();
        var b = new List<int>();
        var halves = new List<int>();
        var scales = new List<int>();
        for (int t = 0; t < minutes; t++)
        {
            int r = returns[t];
            if (r < 0) continue;
            int length = r - t;
            if (length > MaxScale) continue;
            int half = length >= AverageFrom ? length / AverageDiv : 0;
            if (t - half < 0 || r + half > minutes - 1) continue;
            int k = NearestScale(f, length);
            if (k < 0) continue;
            a.Add(t);
            b.Add(r);
            halves.Add(half);
            scales.Add(k);
        }
        f.ExtraA = a.ToArray();
        f.ExtraB = b.ToArray();
        f.ExtraHalf = halves.ToArray();
        f.ExtraScale = scales.ToArray();
        var order = Enumerable.Range(0, f.ExtraB.Length).ToArray();
        Array.Sort(f.ExtraB.ToArray(), order);
        f.ExtraByB = order;
        f.ExtraSortedB = order.Select(i => f.ExtraB[i]).ToArray();
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public static double[] MedianIndex(double[][] x)
    {
        int n = x.Length;
        int minutes = x[0].Length;
        var u = new double[minutes];
        var steps = new double[n];
        for (int t = 1; t < minutes; t++)
        {
            for (int i = 0; i < n; i++) steps[i] = x[i][t] - x[i][t - 1];
            Array.Sort(steps);
            u[t] = u[t - 1] + (n % 2 == 1 ? steps[n / 2] : 0.5 * (steps[n / 2 - 1] + steps[n / 2]));
        }
        return u;
    }

    private static double BoxMean(double[] series, int center, int half)
    {
        if (half == 0) return series[center];
        double sum = 0;
        for (int t = center - half; t <= center + half; t++) sum += series[t];
        return sum / (2 * half + 1);
    }

    private static void Moves(double[][] x, Frame f, int k, int j, double[] buf)
    {
        int half = f.Halves[k];
        int a = half + j * f.Steps[k];
        MovesAt(x, a, a + f.Scales[k], half, buf);
    }

    public static void MovesAt(double[][] x, int a, int b, int half, double[] buf)
    {
        for (int i = 0; i < x.Length; i++) buf[i] = BoxMean(x[i], b, half) - BoxMean(x[i], a, half);
    }

    internal static double Median(double[] values, int n, double[] tmp)
    {
        Array.Copy(values, tmp, n);
        Array.Sort(tmp, 0, n);
        return n % 2 == 1 ? tmp[n / 2] : 0.5 * (tmp[n / 2 - 1] + tmp[n / 2]);
    }

    private static double Kernel(double z)
    {
        double t = Math.Abs(z) - 1.0;
        if (t <= 0) return 1.0;
        if (t > 2.5) return 0.0;
        return Math.Exp(-8.0 * t * t);
    }

    public sealed class FloorTable
    {
        public double[][] Centers = [];
        public double[][] Values = [];
    }

    public static FloorTable ComputeFloors(double[][] x, Frame f, CancellationToken ct)
    {
        int n = x.Length;
        var table = new FloorTable
        {
            Centers = new double[f.Scales.Length][],
            Values = new double[f.Scales.Length][],
        };
        for (int k = 0; k < f.Scales.Length; k++)
        {
            ct.ThrowIfCancellationRequested();
            int count = f.Counts[k];
            if (count == 0) continue;
            int scale = k;
            var mad = new float[count];
            Parallel.For(0, count, () => (new double[n], new double[n], new double[n]), (j, _, buf) =>
            {
                Moves(x, f, scale, j, buf.Item1);
                double med = Median(buf.Item1, n, buf.Item3);
                for (int i = 0; i < n; i++) buf.Item2[i] = Math.Abs(buf.Item1[i] - med);
                mad[j] = (float)Median(buf.Item2, n, buf.Item3);
                return buf;
            }, _ => { });
            (table.Centers[k], table.Values[k]) = FloorBlocks(mad, count, f.Steps[k]);
        }
        return table;
    }

    public static double FloorAt(Frame f, FloorTable table, int k, int a) =>
        Interpolate((a - f.Halves[k]) / (double)f.Steps[k], table.Centers[k], table.Values[k]);

    public static (double Consensus, double Strength) IntervalConsensus(double[] moves, double[] w, double floor)
    {
        int n = moves.Length;
        Cluster(moves, n, w, floor, new double[n], new double[n], out double m, out double h);
        return (m, h);
    }

    private static double Recency(int end, int minutes, int decay) => Math.Clamp(
        1.0 - (minutes - 1 - end - RecentMinutes) / (double)decay * (1.0 - RecencyFloor), RecencyFloor, 1.0);

    private static (float[] Consensus, float[] Strength) StageA(double[][] x, double[] w, Frame f, Options o,
        CancellationToken ct)
    {
        int n = x.Length;
        int minutes = x[0].Length;
        int decay = f.Scales[^1];
        var consensus = new float[f.All];
        var strength = new float[f.All];
        var table = ComputeFloors(x, f, ct);
        for (int k = 0; k < f.Scales.Length; k++)
        {
            ct.ThrowIfCancellationRequested();
            int count = f.Counts[k];
            if (count == 0) continue;
            int offset = f.Offsets[k], step = f.Steps[k], half = f.Halves[k], s = f.Scales[k];
            int scale = k;
            var centers = table.Centers[k];
            var floors = table.Values[k];
            Parallel.For(0, count, () => (new double[n], new double[n], new double[n]), (j, _, buf) =>
            {
                Moves(x, f, scale, j, buf.Item1);
                double floor = Interpolate(j, centers, floors);
                Cluster(buf.Item1, n, w, floor, buf.Item2, buf.Item3, out double m, out double h);
                consensus[offset + j] = (float)m;
                strength[offset + j] = (float)((h + StrengthFloor) * Recency(half + j * step + s, minutes, decay));
                return buf;
            }, _ => { });
        }
        int extras = f.ExtraCount;
        if (extras > 0)
        {
            ct.ThrowIfCancellationRequested();
            int total = f.Total;
            Parallel.For(0, extras, () => (new double[n], new double[n], new double[n]), (i, _, buf) =>
            {
                int a = f.ExtraA[i], b = f.ExtraB[i], half = f.ExtraHalf[i], k = f.ExtraScale[i];
                MovesAt(x, a, b, half, buf.Item1);
                Cluster(buf.Item1, n, w, FloorAt(f, table, k, a), buf.Item2, buf.Item3, out double m, out double h);
                consensus[total + i] = (float)m;
                strength[total + i] = (float)((h + StrengthFloor) * Recency(b, minutes, decay) * o.ReturnStrength);
                return buf;
            }, _ => { });
        }
        return (consensus, strength);
    }

    private static (double[] Centers, double[] Floors) FloorBlocks(float[] mad, int count, int step)
    {
        int block = Math.Max(MinFloorBlock, RecentMinutes / step);
        int blocks = Math.Max(1, count / block);
        var centers = new double[blocks];
        var floors = new double[blocks];
        var tmp = new float[count];
        for (int b = 0; b < blocks; b++)
        {
            int from = (int)Math.Round(count * (double)b / blocks, MidpointRounding.ToEven);
            int to = (int)Math.Round(count * (double)(b + 1) / blocks, MidpointRounding.ToEven);
            int len = to - from;
            Array.Copy(mad, from, tmp, 0, len);
            Array.Sort(tmp, 0, len);
            double med = len % 2 == 1 ? tmp[len / 2] : 0.5 * (tmp[len / 2 - 1] + tmp[len / 2]);
            centers[b] = (from + to - 1) / 2.0;
            floors[b] = Math.Max(MinFloor, NoiseFactor * med);
        }
        return (centers, floors);
    }

    private static double Interpolate(double at, double[] centers, double[] values)
    {
        int last = centers.Length - 1;
        if (at <= centers[0]) return values[0];
        if (at >= centers[last]) return values[last];
        int i = 0;
        while (centers[i + 1] < at) i++;
        double t = (at - centers[i]) / (centers[i + 1] - centers[i]);
        return values[i] + t * (values[i + 1] - values[i]);
    }

    internal static void Cluster(double[] s, int n, double[] w, double floor, double[] modes, double[] weights,
        out double consensus, out double strength)
    {
        for (int j = 0; j < n; j++)
        {
            double m = s[j];
            for (int round = 0; round < MeanShiftRounds; round++)
            {
                double c = Math.Max(floor, RelativeTolerance * Math.Abs(m));
                double num = 0, den = 0;
                for (int i = 0; i < n; i++)
                {
                    double kk = w[i] * Kernel((s[i] - m) / c);
                    num += kk * s[i];
                    den += kk;
                }
                if (den <= 0) break;
                m = num / den;
            }
            double cf = Math.Max(floor, RelativeTolerance * Math.Abs(m));
            double total = 0;
            for (int i = 0; i < n; i++) total += w[i] * Kernel((s[i] - m) / cf);
            modes[j] = m;
            weights[j] = total;
        }
        int best = 0;
        for (int j = 1; j < n; j++)
            if (weights[j] > weights[best]) best = j;
        double m1 = modes[best];
        double c1 = Math.Max(floor, RelativeTolerance * Math.Abs(m1));
        double w2 = 0;
        for (int j = 0; j < n; j++)
            if (Math.Abs(modes[j] - m1) > c1 && weights[j] > w2) w2 = weights[j];
        consensus = m1;
        strength = Math.Max(0, weights[best] - w2);
    }

    private static void Scatter(double[] y, int center, int half, double value)
    {
        if (half == 0)
        {
            y[center] += value;
            return;
        }
        double v = value / (2 * half + 1);
        for (int t = center - half; t <= center + half; t++) y[t] += v;
    }

    private static int Blocks(int n) => Math.Clamp(n / BlockMinutes, 1, MaxBlocks);

    private static (int From, int To) Block(int n, int blocks, int bi) =>
        ((int)((long)n * bi / blocks), (int)((long)n * (bi + 1) / blocks));

    private static int ScaleOf(Frame f, int e)
    {
        int k = 0;
        while (k + 1 < f.Scales.Length && e >= f.Offsets[k + 1]) k++;
        return k;
    }

    private static void PrefixSum(double[] u, double[] prefix, double[] blockSums)
    {
        int n = u.Length;
        int blocks = Blocks(n);
        Parallel.For(0, blocks, bi =>
        {
            var (from, to) = Block(n, blocks, bi);
            double s = 0;
            for (int t = from; t < to; t++) s += u[t];
            blockSums[bi] = s;
        });
        double running = 0;
        for (int bi = 0; bi < blocks; bi++)
        {
            double s = blockSums[bi];
            blockSums[bi] = running;
            running += s;
        }
        prefix[0] = 0;
        Parallel.For(0, blocks, bi =>
        {
            var (from, to) = Block(n, blocks, bi);
            double acc = blockSums[bi];
            for (int t = from; t < to; t++)
            {
                acc += u[t];
                prefix[t + 1] = acc;
            }
        });
    }

    private static void Apply(Frame f, float[] h, double[] u, double[] prefix, double[] blockSums, double[] work,
        double[] y)
    {
        int minutes = u.Length;
        PrefixSum(u, prefix, blockSums);
        int total = f.Total;
        int blocks = Blocks(total);
        Parallel.For(0, blocks, bi =>
        {
            var (from, to) = Block(total, blocks, bi);
            int k = ScaleOf(f, from);
            for (int e = from; e < to; e++)
            {
                while (e >= f.Offsets[k] + f.Counts[k]) k++;
                int half = f.Halves[k];
                int len = 2 * half + 1;
                int a = half + (e - f.Offsets[k]) * f.Steps[k];
                int b = a + f.Scales[k];
                double move = (prefix[b + half + 1] - prefix[b - half]) - (prefix[a + half + 1] - prefix[a - half]);
                work[e] = h[e] * move / ((double)len * len);
            }
        });
        int extras = f.ExtraCount;
        if (extras > 0)
        {
            int extraBlocks = Blocks(extras);
            Parallel.For(0, extraBlocks, bi =>
            {
                var (from, to) = Block(extras, extraBlocks, bi);
                for (int i = from; i < to; i++)
                {
                    int half = f.ExtraHalf[i];
                    int len = 2 * half + 1;
                    int a = f.ExtraA[i], b = f.ExtraB[i];
                    double move = (prefix[b + half + 1] - prefix[b - half]) - (prefix[a + half + 1] - prefix[a - half]);
                    work[total + i] = h[total + i] * move / ((double)len * len);
                }
            });
        }
        int nodeBlocks = Blocks(minutes);
        Parallel.For(0, nodeBlocks, bi =>
        {
            var (from, to) = Block(minutes, nodeBlocks, bi);
            int n = to - from;
            var d = ArrayPool<double>.Shared.Rent(n + 1);
            Array.Clear(d, 0, n + 1);
            for (int k = 0; k < f.Scales.Length; k++)
            {
                if (f.Counts[k] == 0) continue;
                AddWindows(d, work, f, k, from, to, 0, -1.0);
                AddWindows(d, work, f, k, from, to, f.Scales[k], 1.0);
            }
            if (extras > 0) AddExtraWindows(d, work, f, from, to);
            double acc = 0;
            for (int t = 0; t < n; t++)
            {
                acc += d[t];
                y[from + t] = acc;
            }
            ArrayPool<double>.Shared.Return(d);
        });
        y[0] += Anchor * u[0];
    }

    private static void AddWindows(double[] d, double[] work, Frame f, int k, int from, int to, int shift,
        double sign)
    {
        int step = f.Steps[k], span = 2 * f.Halves[k], offset = f.Offsets[k], count = f.Counts[k];
        int jLo = Math.Max(0, (int)Math.Ceiling((from - shift - span) / (double)step));
        int jHi = Math.Min(count - 1, (int)Math.Floor((to - 1 - shift) / (double)step));
        for (int j = jLo; j <= jHi; j++)
        {
            double v = sign * work[offset + j];
            int start = j * step + shift;
            int w0 = Math.Max(from, start) - from;
            int w1 = Math.Min(to - 1, start + span) - from;
            d[w0] += v;
            d[w1 + 1] -= v;
        }
    }

    private static void AddExtraWindows(double[] d, double[] work, Frame f, int from, int to)
    {
        int total = f.Total;
        int lo = LowerBound(f.ExtraA, from - MaxHalf);
        int hi = LowerBound(f.ExtraA, to + MaxHalf);
        for (int i = lo; i < hi; i++) AddWindow(d, from, to, f.ExtraA[i], f.ExtraHalf[i], -work[total + i]);
        lo = LowerBound(f.ExtraSortedB, from - MaxHalf);
        hi = LowerBound(f.ExtraSortedB, to + MaxHalf);
        for (int p = lo; p < hi; p++)
        {
            int i = f.ExtraByB[p];
            AddWindow(d, from, to, f.ExtraB[i], f.ExtraHalf[i], work[total + i]);
        }
    }

    private static void AddWindow(double[] d, int from, int to, int center, int half, double v)
    {
        int w0 = Math.Max(from, center - half) - from;
        int w1 = Math.Min(to - 1, center + half) - from;
        if (w0 > w1) return;
        d[w0] += v;
        d[w1 + 1] -= v;
    }

    private static double[] StageB(Frame f, float[] m, float[] h, int minutes, double[] u0, CancellationToken ct,
        out int steps, out long matVecMs)
    {
        var matVec = new Stopwatch();
        var diag = new double[minutes];
        var rhs = new double[minutes];
        for (int k = 0; k < f.Scales.Length; k++)
        {
            int half = f.Halves[k], step = f.Steps[k], s = f.Scales[k], offset = f.Offsets[k];
            double len = 2 * half + 1;
            for (int j = 0; j < f.Counts[k]; j++)
            {
                int a = half + j * step;
                int b = a + s;
                double hv = h[offset + j];
                Scatter(diag, b, half, hv / len);
                Scatter(diag, a, half, hv / len);
                double fm = hv * m[offset + j];
                Scatter(rhs, b, half, fm);
                Scatter(rhs, a, half, -fm);
            }
        }
        for (int i = 0; i < f.ExtraCount; i++)
        {
            int half = f.ExtraHalf[i];
            double len = 2 * half + 1;
            int a = f.ExtraA[i], b = f.ExtraB[i];
            double hv = h[f.Total + i];
            Scatter(diag, b, half, hv / len);
            Scatter(diag, a, half, hv / len);
            double fm = hv * m[f.Total + i];
            Scatter(rhs, b, half, fm);
            Scatter(rhs, a, half, -fm);
        }
        diag[0] += Anchor;

        var u = (double[])u0.Clone();
        var r = new double[minutes];
        var z = new double[minutes];
        var p = new double[minutes];
        var q = new double[minutes];
        var prefix = new double[minutes + 1];
        var work = new double[f.All];
        int blocks = Blocks(minutes);
        var blockSums = new double[blocks];
        var partA = new double[blocks];
        var partB = new double[blocks];

        Apply(f, h, u, prefix, blockSums, work, q);
        Parallel.For(0, blocks, bi =>
        {
            var (from, to) = Block(minutes, blocks, bi);
            double a = 0, b = 0;
            for (int t = from; t < to; t++)
            {
                r[t] = rhs[t] - q[t];
                z[t] = r[t] / diag[t];
                p[t] = z[t];
                a += r[t] * z[t];
                b += rhs[t] * rhs[t];
            }
            partA[bi] = a;
            partB[bi] = b;
        });
        double rz = partA.Sum();
        double limit = CgTolerance * Math.Sqrt(partB.Sum());
        double rNorm2 = 0;
        for (int t = 0; t < minutes; t++) rNorm2 += r[t] * r[t];

        steps = 0;
        while (steps < CgMaxIterations)
        {
            if (Math.Sqrt(rNorm2) <= limit) break;
            ct.ThrowIfCancellationRequested();
            matVec.Start();
            Apply(f, h, p, prefix, blockSums, work, q);
            matVec.Stop();
            Parallel.For(0, blocks, bi =>
            {
                var (from, to) = Block(minutes, blocks, bi);
                double a = 0;
                for (int t = from; t < to; t++) a += p[t] * q[t];
                partA[bi] = a;
            });
            double pq = partA.Sum();
            if (pq <= 0) break;
            double alpha = rz / pq;
            Parallel.For(0, blocks, bi =>
            {
                var (from, to) = Block(minutes, blocks, bi);
                double a = 0, b = 0;
                for (int t = from; t < to; t++)
                {
                    u[t] += alpha * p[t];
                    r[t] -= alpha * q[t];
                    z[t] = r[t] / diag[t];
                    a += r[t] * z[t];
                    b += r[t] * r[t];
                }
                partA[bi] = a;
                partB[bi] = b;
            });
            double rzNew = partA.Sum();
            rNorm2 = partB.Sum();
            double beta = rzNew / rz;
            rz = rzNew;
            Parallel.For(0, blocks, bi =>
            {
                var (from, to) = Block(minutes, blocks, bi);
                for (int t = from; t < to; t++) p[t] = z[t] + beta * p[t];
            });
            steps++;
        }
        double shift = u[0];
        for (int t = 0; t < minutes; t++) u[t] -= shift;
        matVecMs = matVec.ElapsedMilliseconds;
        return u;
    }
}
