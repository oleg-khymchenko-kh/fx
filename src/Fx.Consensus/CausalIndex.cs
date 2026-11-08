using System.Diagnostics;

namespace Fx.Consensus;

public static class CausalIndex
{
    public const int Version = 1;
    public const int QBase = 100000;
    public const int PipScale = 10;
    public const int AnchorDiv = 4;
    public const int ReturnAverageDiv = 256;
    public const int FloorWeeks = 10;
    public const int SampleEvery = 8;
    public const long WeekSeconds = 7 * 86400L;
    public const long WeekOrigin = 1704628800L;

    public static readonly int[] Scales = BuildScales();
    public static readonly int Lookback = Scales[^1] + Scales[^1] / AnchorDiv + 1;
    public static readonly int FloorLookbackWeeks = FloorWeeks + 1;

    public sealed class Series
    {
        public long[] Times = [];
        public double[][] Prices = [];
        public bool[] UsdBase = [];
        public double[] Pip = [];
        public bool Pips = true;
        public double[] Weights = [];
    }

    public sealed record Params(double Lambda = 0.1, double Power = 1.0)
    {
        public static readonly Params Default = new();
    }

    public sealed class FloorBook
    {
        public readonly Dictionary<long, float[]> Weeks = new();
    }

    public sealed record Stats(int Computed, int Weeks, int Returns, long StageAMs, long RecursionMs, long FloorMs)
    {
        public string Summary =>
            $"{Computed:N0} minutes over {Weeks} week(s), {Returns:N0} returns, " +
            $"floors {FloorMs} ms, stage A {StageAMs} ms, recursion {RecursionMs} ms";
    }

    private static int[] BuildScales()
    {
        var scales = new List<int> { 1 };
        while (true)
        {
            int s = scales[^1];
            int next = Math.Max(s + 1,
                (int)Math.Round(s * (s >= ConsensusIndex.LongFrom ? ConsensusIndex.LongFactor : 2.0)));
            if (next > ConsensusIndex.MaxScale) break;
            scales.Add(next);
        }
        return scales.ToArray();
    }

    public static long WeekOf(long unix)
    {
        long d = unix - WeekOrigin;
        long w = d / WeekSeconds;
        return d < 0 && d % WeekSeconds != 0 ? w - 1 : w;
    }

    public static double ValueOf(double q, bool pips) =>
        pips ? (q - QBase) / PipScale : Math.Log(q / QBase) * 10000.0;

    public static int Quantize(double value, bool pips) => (int)Math.Floor(
        (pips ? QBase + value * PipScale : QBase * Math.Exp(value / 10000.0)) + 0.5);

    private static int NearestScale(int length)
    {
        int best = 0;
        double bestDist = double.MaxValue;
        for (int k = 0; k < Scales.Length; k++)
        {
            double dist = Math.Abs(Math.Log(length / (double)Scales[k]));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = k;
            }
        }
        return best;
    }

    private static double[][] Prefix(double[][] prices)
    {
        var result = new double[prices.Length][];
        for (int i = 0; i < prices.Length; i++)
        {
            var p = prices[i];
            var s = new double[p.Length + 1];
            for (int t = 0; t < p.Length; t++) s[t + 1] = s[t] + p[t];
            result[i] = s;
        }
        return result;
    }

    private static void Moves(Series s, double[][] prefix, int a, int h, int t, double[] buf)
    {
        int len = 2 * h + 1;
        for (int i = 0; i < buf.Length; i++)
        {
            double start = (prefix[i][a + h + 1] - prefix[i][a - h]) / len;
            double end = s.Prices[i][t];
            double v = s.Pips ? (end - start) / s.Pip[i] : Math.Log(end / start) * 10000.0;
            buf[i] = s.UsdBase[i] ? v : -v;
        }
    }

    public static double[] Spread(Series s, int first, int second)
    {
        int n = s.Times.Length;
        var spread = new double[n];
        for (int t = 0; t < n; t++)
        {
            double a = s.Pips ? s.Prices[first][t] / s.Pip[first] : Math.Log(s.Prices[first][t]) * 10000.0;
            double b = s.Pips ? s.Prices[second][t] / s.Pip[second] : Math.Log(s.Prices[second][t]) * 10000.0;
            spread[t] = (s.UsdBase[first] ? a : -a) - (s.UsdBase[second] ? b : -b);
        }
        return spread;
    }

    public static bool HasWeeks(FloorBook book, long fromWeek, long toWeek)
    {
        for (long w = fromWeek; w <= toWeek; w++)
            if (!book.Weeks.ContainsKey(w)) return false;
        return true;
    }

    public static void FillFloors(Series s, FloorBook book, long fromWeek, long toWeek, bool fromOrigin,
        CancellationToken ct)
    {
        int n = s.Times.Length;
        if (n == 0) return;
        long lastWeek = WeekOf(s.Times[n - 1]);
        var prefix = Prefix(s.Prices);
        var weekStarts = new List<(long Week, int From, int To)>();
        int t0 = 0;
        while (t0 < n)
        {
            long w = WeekOf(s.Times[t0]);
            int t1 = t0;
            while (t1 < n && WeekOf(s.Times[t1]) == w) t1++;
            bool complete = fromOrigin || t0 >= Lookback;
            if (w >= fromWeek && w <= toWeek && w < lastWeek && complete && !book.Weeks.ContainsKey(w))
                weekStarts.Add((w, t0, t1));
            t0 = t1;
        }
        int pairs = s.Prices.Length;
        var results = new float[weekStarts.Count][];
        Parallel.For(0, weekStarts.Count, wi =>
        {
            ct.ThrowIfCancellationRequested();
            var (_, from, to) = weekStarts[wi];
            var samples = new List<float>[Scales.Length];
            for (int k = 0; k < Scales.Length; k++) samples[k] = new List<float>();
            var buf = new double[pairs];
            var dev = new double[pairs];
            var tmp = new double[pairs];
            for (int t = from; t < to; t++)
            {
                if (s.Times[t] / 60 % SampleEvery != 0) continue;
                for (int k = 0; k < Scales.Length; k++)
                {
                    int sc = Scales[k], h = sc / AnchorDiv, a = t - sc;
                    if (a - h < 0) break;
                    Moves(s, prefix, a, h, t, buf);
                    double med = ConsensusIndex.Median(buf, pairs, tmp);
                    for (int i = 0; i < pairs; i++) dev[i] = Math.Abs(buf[i] - med);
                    samples[k].Add((float)ConsensusIndex.Median(dev, pairs, tmp));
                }
            }
            var row = new float[Scales.Length];
            for (int k = 0; k < Scales.Length; k++)
            {
                var list = samples[k];
                if (list.Count == 0)
                {
                    row[k] = float.NaN;
                    continue;
                }
                list.Sort();
                int c = list.Count;
                row[k] = c % 2 == 1 ? list[c / 2] : 0.5f * (list[c / 2 - 1] + list[c / 2]);
            }
            results[wi] = row;
        });
        for (int wi = 0; wi < weekStarts.Count; wi++) book.Weeks[weekStarts[wi].Week] = results[wi];
    }

    public static double[] FloorsFor(FloorBook book, long week)
    {
        var floors = new double[Scales.Length];
        var values = new List<float>(FloorWeeks);
        for (int k = 0; k < Scales.Length; k++)
        {
            values.Clear();
            for (long w = week - FloorWeeks; w < week; w++)
                if (book.Weeks.TryGetValue(w, out var row) && !float.IsNaN(row[k])) values.Add(row[k]);
            if (values.Count == 0)
            {
                floors[k] = ConsensusIndex.MinFloor;
                continue;
            }
            values.Sort();
            int c = values.Count;
            double med = c % 2 == 1 ? values[c / 2] : 0.5 * (values[c / 2 - 1] + values[c / 2]);
            floors[k] = Math.Max(ConsensusIndex.MinFloor, ConsensusIndex.NoiseFactor * med);
        }
        return floors;
    }

    public static int[] Run(Series s, ConsensusIndex.Options o, int startIdx, int[] q, int knownFrom, int firstNew,
        FloorBook book, CancellationToken ct, out Stats stats, Action<double>? progress = null, Params? tuning = null,
        bool fromOrigin = true)
    {
        var par = tuning ?? Params.Default;
        int n = s.Times.Length;
        int pairs = s.Prices.Length;
        var w = s.Weights.Select(v => v / s.Weights.Sum()).ToArray();
        var swFloor = Stopwatch.StartNew();
        if (firstNew < n)
            FillFloors(s, book, WeekOf(s.Times[firstNew]) - FloorWeeks, WeekOf(s.Times[n - 1]) - 1, fromOrigin, ct);
        long floorMs = swFloor.ElapsedMilliseconds;
        var prefix = Prefix(s.Prices);
        int anchorMin = Math.Max(startIdx, knownFrom);
        var qPrefix = new double[n + 1];
        for (int t = knownFrom; t < firstNew; t++) qPrefix[t + 1] = qPrefix[t] + q[t];

        int[] retOffsets = new int[n + 1];
        int[] retStarts = [];
        int returns = 0;
        if (o.ReturnIntervals && pairs >= 2)
        {
            var (first, second) = ConsensusIndex.HeaviestPairs(w);
            var next = ConsensusIndex.NextReturns(Spread(s, first, second));
            var count = new int[n + 1];
            for (int t = 0; t < n; t++)
            {
                int r = next[t];
                if (r < firstNew || r - t > ConsensusIndex.MaxScale) continue;
                count[r]++;
            }
            for (int t = 0; t < n; t++) retOffsets[t + 1] = retOffsets[t] + count[t];
            retStarts = new int[retOffsets[n]];
            var fill = (int[])retOffsets.Clone();
            for (int t = 0; t < n; t++)
            {
                int r = next[t];
                if (r < firstNew || r - t > ConsensusIndex.MaxScale) continue;
                retStarts[fill[r]++] = t;
            }
            returns = retStarts.Length;
        }

        int scales = Scales.Length;
        long stageAMs = 0, recursionMs = 0;
        int weeks = 0;
        int computed = 0;
        var sw = new Stopwatch();
        int chunkFrom = firstNew;
        while (chunkFrom < n)
        {
            ct.ThrowIfCancellationRequested();
            long week = WeekOf(s.Times[chunkFrom]);
            int chunkTo = chunkFrom;
            while (chunkTo < n && WeekOf(s.Times[chunkTo]) == week) chunkTo++;
            var floors = FloorsFor(book, week);
            int len = chunkTo - chunkFrom;
            var m = new double[len * scales];
            var hs = new double[len * scales];
            int retBase = retOffsets[chunkFrom];
            int retLen = retOffsets[chunkTo] - retBase;
            var retM = new double[retLen];
            var retH = new double[retLen];
            int cf = chunkFrom;
            sw.Restart();
            Parallel.For(0, len, () => (new double[pairs], new double[pairs], new double[pairs]), (j, _, buf) =>
            {
                int t = cf + j;
                for (int k = 0; k < scales; k++)
                {
                    int sc = Scales[k], h = sc / AnchorDiv, a = t - sc;
                    if (a - h < anchorMin)
                    {
                        hs[j * scales + k] = -1;
                        continue;
                    }
                    Moves(s, prefix, a, h, t, buf.Item1);
                    ConsensusIndex.Cluster(buf.Item1, pairs, w, floors[k], buf.Item2, buf.Item3,
                        out double mv, out double hv);
                    m[j * scales + k] = mv;
                    hs[j * scales + k] = hv + ConsensusIndex.StrengthFloor;
                }
                for (int e = retOffsets[t]; e < retOffsets[t + 1]; e++)
                {
                    int a = retStarts[e], length = t - a;
                    int h = length >= ConsensusIndex.AverageFrom ? length / ReturnAverageDiv : 0;
                    if (a - h < anchorMin)
                    {
                        retH[e - retBase] = -1;
                        continue;
                    }
                    Moves(s, prefix, a, h, t, buf.Item1);
                    ConsensusIndex.Cluster(buf.Item1, pairs, w, floors[NearestScale(length)], buf.Item2, buf.Item3,
                        out double mv, out double hv);
                    retM[e - retBase] = mv;
                    retH[e - retBase] = hv + ConsensusIndex.StrengthFloor;
                }
                return buf;
            }, _ => { });
            stageAMs += sw.ElapsedMilliseconds;

            sw.Restart();
            for (int j = 0; j < len; j++)
            {
                int t = chunkFrom + j;
                if (t == startIdx)
                {
                    q[t] = QBase;
                    qPrefix[t + 1] = qPrefix[t] + q[t];
                    computed++;
                    continue;
                }
                double num = 0, den = 0;
                double baseline = double.NaN;
                for (int k = 0; k < scales; k++)
                {
                    double hv = hs[j * scales + k];
                    if (hv < 0) continue;
                    int sc = Scales[k], h = sc / AnchorDiv, a = t - sc;
                    double anchor = ValueOf((qPrefix[a + h + 1] - qPrefix[a - h]) / (2 * h + 1), s.Pips);
                    double prediction = anchor + m[j * scales + k];
                    if (k == 0) baseline = prediction;
                    double wv = par.Power == 1.0 ? hv : Math.Pow(hv, par.Power);
                    num += wv * prediction;
                    den += wv;
                }
                double rNum = 0, rDen = 0;
                int rCount = 0;
                for (int e = retOffsets[t]; e < retOffsets[t + 1]; e++)
                {
                    double hv = retH[e - retBase];
                    if (hv < 0) continue;
                    int a = retStarts[e], length = t - a;
                    int h = length >= ConsensusIndex.AverageFrom ? length / ReturnAverageDiv : 0;
                    double anchor = ValueOf((qPrefix[a + h + 1] - qPrefix[a - h]) / (2 * h + 1), s.Pips);
                    rNum += hv * (anchor + retM[e - retBase]);
                    rDen += hv;
                    rCount++;
                }
                if (rCount > 0 && o.ReturnStrength > 0)
                {
                    double hv = rDen / rCount;
                    double wv = o.ReturnStrength * (par.Power == 1.0 ? hv : Math.Pow(hv, par.Power));
                    num += wv * (rNum / rDen);
                    den += wv;
                }
                if (den <= 0 || double.IsNaN(baseline))
                    q[t] = den > 0 ? Quantize(num / den, s.Pips) : q[t - 1];
                else
                    q[t] = Quantize(baseline + par.Lambda * (num / den - baseline), s.Pips);
                qPrefix[t + 1] = qPrefix[t] + q[t];
                computed++;
            }
            recursionMs += sw.ElapsedMilliseconds;
            weeks++;
            progress?.Invoke((chunkTo - firstNew) / (double)Math.Max(1, n - firstNew));
            chunkFrom = chunkTo;
        }
        stats = new Stats(computed, weeks, returns, stageAMs, recursionMs, floorMs);
        return q;
    }
}
