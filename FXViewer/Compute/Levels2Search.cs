using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer.Compute;

public sealed record Levels2FindTarget(
    string Name, string Source, string Pair, int PipPoints, LevelSizeTable Left, LevelSizeTable Right,
    SeriesTransform? Transform = null, int PriceMul = 1, int PriceDigits = 5,
    IReadOnlyList<Candle>? TemplateMinutes = null);

public sealed record Levels2LevelRule(
    double ValuePoints, bool Required, double TolUpPips, double TolDownPips, int Sequence,
    double MinLeftPips, double MinRightPips);

public sealed record Levels2SearchTarget(Levels2FindTarget Target, IReadOnlyList<Candle> Minutes);

public static class Levels2Search
{
    private const double ZoomStep = 1.15;
    private const int MinShiftPips = 20;
    private const int MaxShiftPips = 150;
    private const int Channels = 2;
    private const int KernelStepsPerPip = 1;
    private const int ExhaustiveShifts = 48;

    private sealed class Marks
    {
        public int Count;
        public double[] Row = new double[64];
        public int[] Channel = new int[64];
        public double[] Weight = new double[64];
        public int[] Zoomed = new int[64];
        public int[] Approach = new int[64];
        public int[] Reversal = new int[64];
        public int[] Time = new int[64];
        public int[] Value = new int[64];

        public void Clear() => Count = 0;

        public void Add(double row, int channel, double weight, int approach, int reversal, int time, int value)
        {
            if (Count == Row.Length)
            {
                Array.Resize(ref Row, Count * 2);
                Array.Resize(ref Channel, Count * 2);
                Array.Resize(ref Weight, Count * 2);
                Array.Resize(ref Zoomed, Count * 2);
                Array.Resize(ref Approach, Count * 2);
                Array.Resize(ref Reversal, Count * 2);
                Array.Resize(ref Time, Count * 2);
                Array.Resize(ref Value, Count * 2);
            }
            Row[Count] = row;
            Channel[Count] = channel;
            Weight[Count] = weight;
            Approach[Count] = approach;
            Reversal[Count] = reversal;
            Time[Count] = time;
            Value[Count] = value;
            Count++;
        }
    }

    private sealed class Template
    {
        public required double[][] Dense;
        public required int RowMin;
        public required double Norm;
        public required double MeanPips;
        public required int ShiftPips;
        public required double[] Kernel;
        public required double Sigma;
        public required int[] RuleRows;
        public required int[] RuleUp;
        public required int[] RuleDown;
        public required double[] RuleApproach;
        public required double[] RuleReversal;
        public required bool[] RuleRequired;
        public required int RequiredCount;
        public required int PreferredCount;
        public required int[] UnsequencedPreferred;
        public required int[][] SequenceGroups;
        public required int[] RuleSequence;
        public required int[] RequiredSequenced;
        public required bool HasRangePairs;

        public bool HasSequence => SequenceGroups.Length > 0;
    }

    private sealed class RangeIndex
    {
        private const int BlockShift = 6;
        private readonly Candle[] _minutes;
        private readonly int[] _blockMax;
        private readonly int[] _blockMin;

        public RangeIndex(Candle[] minutes)
        {
            _minutes = minutes;
            int blocks = (minutes.Length >> BlockShift) + 1;
            _blockMax = new int[blocks];
            _blockMin = new int[blocks];
            Array.Fill(_blockMax, int.MinValue);
            Array.Fill(_blockMin, int.MaxValue);
            for (int i = 0; i < minutes.Length; i++)
            {
                int b = i >> BlockShift;
                _blockMax[b] = Math.Max(_blockMax[b], minutes[i].Max);
                _blockMin[b] = Math.Min(_blockMin[b], minutes[i].Min);
            }
        }

        public bool Inside(int from, int to, int lo, int hi)
        {
            int i = Math.Max(0, from);
            int end = Math.Min(_minutes.Length - 1, to);
            while (i <= end)
            {
                if ((i & ((1 << BlockShift) - 1)) == 0 && i + (1 << BlockShift) - 1 <= end)
                {
                    int b = i >> BlockShift;
                    if (_blockMax[b] > hi || _blockMin[b] < lo) return false;
                    i += 1 << BlockShift;
                    continue;
                }
                if (_minutes[i].Max > hi || _minutes[i].Min < lo) return false;
                i++;
            }
            return true;
        }
    }

    private readonly record struct Best(double Sim, double Zoom, double ShiftPips, bool Mirrored, int Preferred);

    private readonly record struct Candidate(long AnchorVirtual, Best Best, int Levels, double MeanPips);

    private readonly record struct ShiftPick(int Shift, double Dot, int Preferred);

    private sealed class Scratch
    {
        public readonly Levels2Index.Scratch Index = new();
        public readonly List<Level2> Levels = new();
        public readonly Marks Marks = new();
        public int[] RequiredHits = Array.Empty<int>();
        public int[] PreferredHits = Array.Empty<int>();
        public int[] LastRule = Array.Empty<int>();
        public List<(int After, int Count)> States = new();
        public List<(int After, int Count)> NextStates = new();
        public readonly List<int> PreferredTimes = new();
        public int[] RuleStart = Array.Empty<int>();
        public int[] RuleMarks = Array.Empty<int>();
        public RangeIndex? Range;
        public int[] Pin = Array.Empty<int>();
        public int[] ChosenMark = Array.Empty<int>();
        public int Leaves;

        public void EnsureShifts(int count)
        {
            if (RequiredHits.Length >= count) return;
            RequiredHits = new int[count];
            PreferredHits = new int[count];
            LastRule = new int[count];
        }
    }

    private static bool Better(int preferred, double sim, int bestPreferred, double bestSim) =>
        preferred > bestPreferred || (preferred == bestPreferred && sim > bestSim);

    public static FindData Search(string template, Levels2FindTarget patternTarget,
        IReadOnlyList<Candle> patternMinutes, IReadOnlyList<Levels2SearchTarget> targets,
        long fragStartUnix, long fragEndUnix, int tolerancePips, int zoomPercent, int stepMinutes,
        IReadOnlyList<Levels2LevelRule> rules, IProgress<double>? progress, CancellationToken ct)
    {
        if (patternMinutes.Count == 0)
            throw new InvalidOperationException($"Template symbol {patternTarget.Source} has no data");
        if (targets.Count == 0) throw new InvalidOperationException("Pick at least one Levels2 indicator");
        foreach (var t in targets)
            if (t.Minutes.Count == 0)
                throw new InvalidOperationException($"Target symbol {t.Target.Pair} has no data");
        if (patternTarget.Left.IsEmpty || patternTarget.Right.IsEmpty)
            throw new InvalidOperationException($"{patternTarget.Name} has an empty pips table");

        var w = WeekendCompressor.Instance;
        long vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        int windowMinutes = (int)((vEnd - vStart) / 60);
        if (windowMinutes < SimilarSearch.MinWindowMinutes)
            throw new InvalidOperationException(
                $"Selection covers {windowMinutes} min of trading time, " +
                $"need at least {SimilarSearch.MinWindowMinutes} min");
        long windowSec = windowMinutes * 60L;
        double sigma = Math.Max(1, tolerancePips);

        var scratch = new Scratch();
        FindLevels(patternTarget, patternMinutes, vStart, vEnd, scratch.Levels);
        if (scratch.Levels.Count < 2)
            throw new InvalidOperationException(
                $"{patternTarget.Name} has {scratch.Levels.Count} levels in the selection, need at least 2");
        var pattern = BuildTemplate(scratch, patternTarget, sigma, rules);
        ct.ThrowIfCancellationRequested();

        var zooms = ZoomGrid(zoomPercent);
        long stepSec = Math.Max(1, stepMinutes) * 60L;
        var results = new List<FindResult>();
        bool anyHistory = false;
        for (int ti = 0; ti < targets.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[ti];
            var minutes = target.Minutes as Candle[] ?? target.Minutes.ToArray();
            long vFirst = SimilarSearch.CeilHour(w.ToVirtual(minutes[0].MinuteUnixSeconds)) + windowSec;
            int candCount = vFirst <= vStart ? (int)((vStart - vFirst) / stepSec) + 1 : 0;
            if (candCount <= 0) continue;
            anyHistory = true;
            int targetIndex = ti;
            var index = new Levels2Index(minutes, target.Target.Left.MinPoints, target.Target.Right.MinPoints);
            var range = pattern.HasRangePairs ? new RangeIndex(minutes) : null;
            ct.ThrowIfCancellationRequested();
            var candidates = Scan(index, range, target.Target, w, vFirst, stepSec, candCount, windowSec, pattern,
                zooms, f => progress?.Report((targetIndex + f) / targets.Count), ct);
            results.AddRange(Rank(candidates, target.Target, pattern, windowSec, w));
        }
        if (!anyHistory) throw new InvalidOperationException("No history before the selection to search in");
        results.Sort((a, b) => a.Preferred != b.Preferred
            ? b.Preferred.CompareTo(a.Preferred)
            : b.Sim.CompareTo(a.Sim));
        if (results.Count > SimilarSearch.MaxResults)
            results.RemoveRange(SimilarSearch.MaxResults, results.Count - SimilarSearch.MaxResults);
        progress?.Report(1.0);
        return new FindData(FindStore.CurrentVersion, template, w.ToReal(vStart), w.ToRealEnd(vEnd),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), windowMinutes, results,
            targets.Select(t => t.Target.Name).ToList(), 0, zoomPercent, stepMinutes,
            FindSearchTypes.Levels2, patternTarget.Name, tolerancePips);
    }

    public static bool TemplateLevels(Levels2FindTarget target, IReadOnlyList<Candle> minutes,
        long fragStartUnix, long fragEndUnix, List<Level2> output)
    {
        output.Clear();
        if (minutes.Count == 0 || target.Left.IsEmpty || target.Right.IsEmpty) return false;
        var w = WeekendCompressor.Instance;
        long vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        if ((vEnd - vStart) / 60 < SimilarSearch.MinWindowMinutes) return false;
        FindLevels(target, minutes, vStart, vEnd, output);
        return true;
    }

    public static bool TemplateExtremes(Levels2FindTarget target, IReadOnlyList<Candle> minutes,
        long fragStartUnix, long fragEndUnix, List<Level2> output)
    {
        output.Clear();
        if (minutes.Count == 0 || target.Left.IsEmpty) return false;
        var w = WeekendCompressor.Instance;
        long vStart = SimilarSearch.FloorHour(w.ToVirtual(fragStartUnix));
        long vEnd = SimilarSearch.CeilHour(w.ToVirtual(fragEndUnix));
        if ((vEnd - vStart) / 60 < SimilarSearch.MinWindowMinutes) return false;
        FindLevels(target, minutes, vStart, vEnd, output, 1, 1);
        return true;
    }

    private static void FindLevels(Levels2FindTarget target, IReadOnlyList<Candle> minutes,
        long vStart, long vEnd, List<Level2> output, double? minApproach = null, double? minReversal = null)
    {
        var w = WeekendCompressor.Instance;
        var finder = new Levels2Finder();
        finder.Find(MinuteSequence.Of(minutes as Candle[] ?? minutes.ToArray()),
            w.ToReal(vStart), w.ToReal(vEnd) - 1, minApproach ?? target.Left.MinPoints,
            minReversal ?? target.Right.MinPoints, target.Left.MaxPoints, output);
    }

    private static double CollectMarks(Scratch scratch, Levels2FindTarget target)
    {
        scratch.Marks.Clear();
        double sum = 0;
        foreach (var level in scratch.Levels) sum += level.Value;
        double mean = sum / scratch.Levels.Count;
        foreach (var level in scratch.Levels)
        {
            double row = (level.Value - mean) / DensityProfile.PipPoints;
            double weight = target.Left.PixelsAt(level.ApproachPoints)
                + target.Right.PixelsAt(level.ReversalPoints);
            scratch.Marks.Add(row, level.Top ? 0 : 1, weight, level.ApproachPoints, level.ReversalPoints,
                level.Index, level.Value);
        }
        return mean / DensityProfile.PipPoints;
    }

    private static double Kernel(double delta, double sigma) => Math.Exp(-delta * delta / (4 * sigma * sigma));

    private static double KernelAt(double[] table, int delta)
    {
        int i = Math.Abs(delta) * KernelStepsPerPip;
        return i < table.Length ? table[i] : 0;
    }

    private static double[] KernelTable(double sigma)
    {
        int count = (int)Math.Ceiling(4 * sigma * Math.Sqrt(2) * KernelStepsPerPip) + 1;
        var table = new double[count];
        for (int i = 0; i < count; i++) table[i] = Kernel((double)i / KernelStepsPerPip, sigma);
        return table;
    }

    private static void ZoomRows(Marks marks, double zoom)
    {
        for (int i = 0; i < marks.Count; i++) marks.Zoomed[i] = (int)Math.Round(zoom * marks.Row[i]);
    }

    private static double Norm(Marks marks, double[] kernel)
    {
        double total = 0;
        for (int i = 0; i < marks.Count; i++)
        {
            double wi = marks.Weight[i];
            total += wi * wi;
            for (int j = i + 1; j < marks.Count; j++)
            {
                if (marks.Channel[i] != marks.Channel[j]) continue;
                total += 2 * wi * marks.Weight[j] * KernelAt(kernel, marks.Zoomed[i] - marks.Zoomed[j]);
            }
        }
        return Math.Sqrt(total);
    }

    private static Template BuildTemplate(Scratch scratch, Levels2FindTarget target, double sigma,
        IReadOnlyList<Levels2LevelRule> rules)
    {
        double meanPips = CollectMarks(scratch, target);
        var marks = scratch.Marks;
        if (marks.Count == 0 || marks.Weight.Take(marks.Count).All(x => x <= 0))
            throw new InvalidOperationException($"{target.Name} levels in the selection draw no pixels");
        int n = rules.Count;
        var ruleRows = new int[n];
        var ruleUp = new int[n];
        var ruleDown = new int[n];
        var ruleApproach = new double[n];
        var ruleReversal = new double[n];
        var ruleRequired = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var rule = rules[i];
            ruleRows[i] = (int)Math.Round(rule.ValuePoints / DensityProfile.PipPoints - meanPips);
            ruleUp[i] = (int)Math.Round(Math.Max(0, rule.TolUpPips));
            ruleDown[i] = (int)Math.Round(Math.Max(0, rule.TolDownPips));
            ruleApproach[i] = Math.Max(0, rule.MinLeftPips) * DensityProfile.PipPoints;
            ruleReversal[i] = Math.Max(0, rule.MinRightPips) * DensityProfile.PipPoints;
            ruleRequired[i] = rule.Required;
        }
        var groups = Enumerable.Range(0, n)
            .Where(i => rules[i].Sequence > 0)
            .GroupBy(i => rules[i].Sequence)
            .OrderBy(g => g.Key)
            .Select(g => g.ToArray())
            .ToArray();
        var ruleSequence = rules.Select(r => Math.Max(0, r.Sequence)).ToArray();
        var requiredSequenced = Enumerable.Range(0, n)
            .Where(i => ruleRequired[i] && ruleSequence[i] > 0)
            .OrderBy(i => ruleSequence[i])
            .ToArray();
        bool hasRangePairs = requiredSequenced.Any(a => requiredSequenced.Any(
            b => ruleSequence[b] == ruleSequence[a] + 1));
        ZoomRows(marks, 1);
        int minRow = marks.Zoomed.Take(marks.Count).Min();
        int maxRow = marks.Zoomed.Take(marks.Count).Max();
        int pad = (int)Math.Ceiling(4 * sigma * Math.Sqrt(2));
        int rowMin = minRow - pad;
        int rowMax = maxRow + pad;
        var kernel = KernelTable(sigma);
        var dense = new double[Channels][];
        for (int c = 0; c < Channels; c++) dense[c] = new double[rowMax - rowMin + 1];
        for (int i = 0; i < marks.Count; i++)
        {
            var row = dense[marks.Channel[i]];
            for (int r = 0; r < row.Length; r++)
                row[r] += marks.Weight[i] * KernelAt(kernel, r + rowMin - marks.Zoomed[i]);
        }
        int shift = Math.Clamp((int)Math.Ceiling((maxRow - minRow) / 2.0), MinShiftPips, MaxShiftPips);
        return new Template
        {
            Dense = dense,
            RowMin = rowMin,
            Norm = Norm(marks, kernel),
            MeanPips = meanPips,
            ShiftPips = shift,
            Kernel = kernel,
            Sigma = sigma,
            RuleRows = ruleRows,
            RuleUp = ruleUp,
            RuleDown = ruleDown,
            RuleApproach = ruleApproach,
            RuleReversal = ruleReversal,
            RuleRequired = ruleRequired,
            RequiredCount = ruleRequired.Count(r => r),
            PreferredCount = ruleRequired.Count(r => !r),
            UnsequencedPreferred = Enumerable.Range(0, n)
                .Where(i => !ruleRequired[i] && rules[i].Sequence <= 0)
                .ToArray(),
            SequenceGroups = groups,
            RuleSequence = ruleSequence,
            RequiredSequenced = requiredSequenced,
            HasRangePairs = hasRangePairs,
        };
    }

    private static double[] ZoomGrid(int zoomPercent)
    {
        double maxZoom = 1 + Math.Max(0, zoomPercent) / 100.0;
        int steps = maxZoom <= 1 ? 0 : (int)Math.Ceiling(Math.Log(maxZoom) / Math.Log(ZoomStep) - 1e-9);
        var zooms = new double[2 * steps + 1];
        for (int i = -steps; i <= steps; i++)
            zooms[i + steps] = steps == 0 ? 1 : Math.Pow(maxZoom, (double)i / steps);
        return zooms;
    }

    private static Candidate[] Scan(Levels2Index index, RangeIndex? range, Levels2FindTarget target, WeekendCompressor w,
        long vFirst, long stepSec, int candCount, long windowSec, Template pattern, double[] zooms,
        Action<double> report, CancellationToken ct)
    {
        var candidates = new Candidate[candCount];
        int done = 0;
        int reportEvery = Math.Max(1, candCount / 200);
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, candCount, po, () => new Scratch { Range = range }, (k, _, scratch) =>
        {
            long anchor = vFirst + k * stepSec;
            index.Levels(w.ToReal(anchor - windowSec), w.ToReal(anchor) - 1, scratch.Index, scratch.Levels);
            candidates[k] = Score(scratch, target, anchor, pattern, zooms);
            int d = Interlocked.Increment(ref done);
            if (d % reportEvery == 0) report((double)d / candCount);
            return scratch;
        }, _ => { });
        return candidates;
    }

    private static Candidate Score(Scratch scratch, Levels2FindTarget target, long anchor, Template pattern,
        double[] zooms)
    {
        if (scratch.Levels.Count == 0) return new Candidate(anchor, default, 0, 0);
        double meanPips = CollectMarks(scratch, target);
        var marks = scratch.Marks;
        if (marks.Count == 0) return new Candidate(anchor, default, scratch.Levels.Count, meanPips);
        bool rules = pattern.RuleRows.Length > 0;
        if (rules && !CollectRuleMarks(scratch, marks, pattern))
            return new Candidate(anchor, default, scratch.Levels.Count, meanPips);
        int coarse = Math.Max(1, (int)Math.Round(pattern.Sigma / 2));
        scratch.EnsureShifts(2 * pattern.ShiftPips + 1);
        var best = new Best(0, 1, 0, false, 0);
        foreach (double zoom in zooms)
        {
            ZoomRows(marks, zoom);
            double norm = Norm(marks, pattern.Kernel);
            if (norm <= 0) continue;
            double scale = 1 / (norm * pattern.Norm);
            for (int mirror = 0; mirror < 2; mirror++)
            {
                bool m = mirror == 1;
                if (rules && !StampRules(scratch, marks, m, pattern)) continue;
                var floor = new ShiftPick(0, best.Sim / scale, best.Preferred);
                if (!BestShift(scratch, marks, m, pattern, coarse, rules, floor, out var pick)) continue;
                double sim = pick.Dot * scale;
                if (Better(pick.Preferred, sim, best.Preferred, best.Sim))
                    best = new Best(sim, zoom, pick.Shift, m, pick.Preferred);
            }
        }
        return new Candidate(anchor, best, scratch.Levels.Count, meanPips);
    }

    private static bool QualifiesFor(Marks marks, int i, Template pattern, int rule) =>
        marks.Approach[i] >= pattern.RuleApproach[rule]
        && marks.Reversal[i] >= pattern.RuleReversal[rule];

    private static bool InBand(Marks marks, int i, Template pattern, int rule, int sign, int shift)
    {
        int row = sign * marks.Zoomed[i] + shift;
        int target = pattern.RuleRows[rule];
        return row >= target - pattern.RuleDown[rule] && row <= target + pattern.RuleUp[rule];
    }

    private static bool CollectRuleMarks(Scratch scratch, Marks marks, Template pattern)
    {
        int rules = pattern.RuleRows.Length;
        if (scratch.RuleStart.Length < rules + 1) scratch.RuleStart = new int[rules + 1];
        if (scratch.RuleMarks.Length < rules * marks.Count)
            scratch.RuleMarks = new int[Math.Max(rules * marks.Count, 2 * scratch.RuleMarks.Length)];
        var list = scratch.RuleMarks;
        var time = marks.Time;
        int n = 0;
        for (int j = 0; j < rules; j++)
        {
            scratch.RuleStart[j] = n;
            int from = n;
            for (int i = 0; i < marks.Count; i++)
            {
                if (!QualifiesFor(marks, i, pattern, j)) continue;
                int k = n++;
                while (k > from && time[list[k - 1]] > time[i])
                {
                    list[k] = list[k - 1];
                    k--;
                }
                list[k] = i;
            }
            if (n == from && pattern.RuleRequired[j]) return false;
        }
        scratch.RuleStart[rules] = n;
        return true;
    }

    private static bool StampRules(Scratch scratch, Marks marks, bool mirror, Template pattern)
    {
        int shiftCount = 2 * pattern.ShiftPips + 1;
        Array.Clear(scratch.RequiredHits, 0, shiftCount);
        Array.Clear(scratch.PreferredHits, 0, shiftCount);
        Array.Fill(scratch.LastRule, -1, 0, shiftCount);
        var lastRule = scratch.LastRule;
        var list = scratch.RuleMarks;
        int sign = mirror ? -1 : 1;
        for (int j = 0; j < pattern.RuleRows.Length; j++)
        {
            var hits = pattern.RuleRequired[j] ? scratch.RequiredHits : scratch.PreferredHits;
            int target = pattern.RuleRows[j];
            for (int k = scratch.RuleStart[j]; k < scratch.RuleStart[j + 1]; k++)
            {
                int center = target - sign * marks.Zoomed[list[k]] + pattern.ShiftPips;
                int from = Math.Max(0, center - pattern.RuleDown[j]);
                int to = Math.Min(shiftCount - 1, center + pattern.RuleUp[j]);
                for (int d = from; d <= to; d++)
                {
                    if (lastRule[d] == j) continue;
                    lastRule[d] = j;
                    hits[d]++;
                }
            }
        }
        if (pattern.RequiredCount == 0) return true;
        for (int d = 0; d < shiftCount; d++)
            if (scratch.RequiredHits[d] == pattern.RequiredCount)
                return true;
        return false;
    }

    private static bool InLevel(Scratch scratch, Template pattern, bool rules, int d, int level)
    {
        if (!rules) return level == 0;
        if (pattern.RequiredCount > 0 && scratch.RequiredHits[d] != pattern.RequiredCount) return false;
        return scratch.PreferredHits[d] == level;
    }

    private static bool BestShift(Scratch scratch, Marks marks, bool mirror, Template pattern, int coarse,
        bool rules, ShiftPick floor, out ShiftPick best)
    {
        int shiftCount = 2 * pattern.ShiftPips + 1;
        int maxLevel = 0;
        if (rules)
            for (int d = 0; d < shiftCount; d++)
                if (pattern.RequiredCount == 0 || scratch.RequiredHits[d] == pattern.RequiredCount)
                    maxLevel = Math.Max(maxLevel, scratch.PreferredHits[d]);
        best = floor;
        bool found = false;
        for (int level = maxLevel; level >= best.Preferred; level--)
        {
            int count = 0;
            for (int d = 0; d < shiftCount; d++)
                if (InLevel(scratch, pattern, rules, d, level))
                    count++;
            if (count == 0) continue;
            bool exact = pattern.HasSequence && (level > 0 || pattern.RequiredCount > 0);
            bool sparse = !exact && count > ExhaustiveShifts;
            var before = best;
            for (int d = 0; d < shiftCount; d++)
            {
                if (!InLevel(scratch, pattern, rules, d, level)) continue;
                if (sparse && (d - pattern.ShiftPips) % coarse != 0) continue;
                found |= Consider(scratch, marks, mirror, pattern, d, level, exact, ref best);
            }
            if (sparse && best != before)
            {
                int center = best.Shift + pattern.ShiftPips;
                for (int d = Math.Max(0, center - coarse + 1); d < Math.Min(shiftCount, center + coarse); d++)
                {
                    if (d == center || !InLevel(scratch, pattern, rules, d, level)) continue;
                    found |= Consider(scratch, marks, mirror, pattern, d, level, exact, ref best);
                }
            }
        }
        return found;
    }

    private static bool Consider(Scratch scratch, Marks marks, bool mirror, Template pattern, int d, int level,
        bool exact, ref ShiftPick best)
    {
        if (level < best.Preferred) return false;
        int shift = d - pattern.ShiftPips;
        double dot = Dot(marks, mirror, shift, pattern);
        if (level == best.Preferred && dot <= best.Dot) return false;
        int preferred = level;
        if (exact && !Assign(scratch, marks, mirror, pattern, shift, level, out preferred)) return false;
        if (!Better(preferred, dot, best.Preferred, best.Dot)) return false;
        best = new ShiftPick(shift, dot, preferred);
        return true;
    }

    private static int Earliest(Scratch scratch, Marks marks, Template pattern, int rule, int sign, int shift,
        int after)
    {
        var list = scratch.RuleMarks;
        for (int k = scratch.RuleStart[rule]; k < scratch.RuleStart[rule + 1]; k++)
        {
            int i = list[k];
            if (marks.Time[i] <= after) continue;
            if (InBand(marks, i, pattern, rule, sign, shift)) return marks.Time[i];
        }
        return int.MaxValue;
    }

    private const int MaxRangeLeaves = 4096;

    private static bool Assign(Scratch scratch, Marks marks, bool mirror, Template pattern, int shift, int cap,
        out int preferred)
    {
        if (!pattern.HasRangePairs) return AssignOrdered(scratch, marks, mirror, pattern, shift, false, out preferred);
        int rules = pattern.RuleRows.Length;
        if (scratch.Pin.Length < rules) scratch.Pin = new int[rules];
        if (scratch.ChosenMark.Length < rules) scratch.ChosenMark = new int[rules];
        scratch.Leaves = 0;
        int best = -1;
        PinRequired(scratch, marks, mirror, pattern, shift, 0, cap, ref best);
        preferred = Math.Max(0, best);
        return best >= 0;
    }

    private static void PinRequired(Scratch scratch, Marks marks, bool mirror, Template pattern, int shift,
        int position, int cap, ref int best)
    {
        var order = pattern.RequiredSequenced;
        if (position == order.Length)
        {
            scratch.Leaves++;
            if (AssignOrdered(scratch, marks, mirror, pattern, shift, true, out int found) && found > best)
                best = found;
            return;
        }
        int rule = order[position];
        int seq = pattern.RuleSequence[rule];
        int sign = mirror ? -1 : 1;
        var list = scratch.RuleMarks;
        for (int k = scratch.RuleStart[rule]; k < scratch.RuleStart[rule + 1]; k++)
        {
            if (best >= cap || scratch.Leaves >= MaxRangeLeaves) return;
            int i = list[k];
            if (!InBand(marks, i, pattern, rule, sign, shift)) continue;
            int time = marks.Time[i];
            bool ok = true;
            for (int p = 0; p < position && ok; p++)
            {
                int other = order[p];
                int otherSeq = pattern.RuleSequence[other];
                if (otherSeq == seq) continue;
                int otherMark = scratch.ChosenMark[other];
                int otherTime = marks.Time[otherMark];
                if (otherTime >= time)
                {
                    ok = false;
                    break;
                }
                if (otherSeq != seq - 1 || scratch.Range == null) continue;
                int lo = Math.Min(marks.Value[otherMark], marks.Value[i]);
                int hi = Math.Max(marks.Value[otherMark], marks.Value[i]);
                ok = scratch.Range.Inside(otherTime + 1, time - 1, lo, hi);
            }
            if (!ok) continue;
            scratch.ChosenMark[rule] = i;
            scratch.Pin[rule] = time;
            PinRequired(scratch, marks, mirror, pattern, shift, position + 1, cap, ref best);
        }
    }

    private static bool AssignOrdered(Scratch scratch, Marks marks, bool mirror, Template pattern, int shift,
        bool pinned, out int preferred)
    {
        preferred = 0;
        int sign = mirror ? -1 : 1;
        foreach (int j in pattern.UnsequencedPreferred)
            if (Earliest(scratch, marks, pattern, j, sign, shift, int.MinValue) != int.MaxValue)
                preferred++;
        var states = scratch.States;
        var next = scratch.NextStates;
        var times = scratch.PreferredTimes;
        states.Clear();
        states.Add((int.MinValue, 0));
        foreach (var group in pattern.SequenceGroups)
        {
            next.Clear();
            foreach (var (after, count) in states)
            {
                int floor = after;
                bool dead = false;
                times.Clear();
                foreach (int j in group)
                {
                    int e = pinned && pattern.RuleRequired[j]
                        ? scratch.Pin[j] > after ? scratch.Pin[j] : int.MaxValue
                        : Earliest(scratch, marks, pattern, j, sign, shift, after);
                    if (pattern.RuleRequired[j])
                    {
                        if (e == int.MaxValue)
                        {
                            dead = true;
                            break;
                        }
                        floor = Math.Max(floor, e);
                    }
                    else if (e != int.MaxValue) times.Add(e);
                }
                if (dead) continue;
                times.Sort();
                int k = 0;
                while (k < times.Count && times[k] <= floor) k++;
                next.Add((floor, count + k));
                for (int t = k; t < times.Count; t++) next.Add((times[t], count + t + 1));
            }
            if (next.Count == 0) return false;
            next.Sort(StateOrder);
            states.Clear();
            int bestCount = -1;
            foreach (var state in next)
            {
                if (state.Count <= bestCount) continue;
                states.Add(state);
                bestCount = state.Count;
            }
        }
        int top = 0;
        foreach (var state in states) top = Math.Max(top, state.Count);
        preferred += top;
        return true;
    }

    private static readonly Comparison<(int After, int Count)> StateOrder = (a, b) =>
        a.After != b.After ? a.After.CompareTo(b.After) : b.Count.CompareTo(a.Count);

    private static double Dot(Marks marks, bool mirror, int shift, Template pattern)
    {
        double total = 0;
        var zoomed = marks.Zoomed;
        var channels = marks.Channel;
        var weights = marks.Weight;
        var dense = pattern.Dense;
        int channelFlip = mirror ? 1 : 0;
        int sign = mirror ? -1 : 1;
        int offset = shift - pattern.RowMin;
        for (int i = 0; i < marks.Count; i++)
        {
            int r = sign * zoomed[i] + offset;
            var row = dense[channels[i] ^ channelFlip];
            if ((uint)r < (uint)row.Length) total += weights[i] * row[r];
        }
        return total;
    }

    private static List<FindResult> Rank(Candidate[] candidates, Levels2FindTarget target, Template pattern,
        long windowSec, WeekendCompressor w)
    {
        var order = Enumerable.Range(0, candidates.Length)
            .Where(k => candidates[k].Best.Sim > 0)
            .OrderByDescending(k => candidates[k].Best.Preferred)
            .ThenByDescending(k => candidates[k].Best.Sim)
            .ToArray();
        var results = new List<FindResult>();
        var kept = new List<long>();
        foreach (var k in order)
        {
            if (results.Count >= SimilarSearch.MaxResults) break;
            var c = candidates[k];
            bool dup = false;
            foreach (var q in kept)
                if (Math.Abs(c.AnchorVirtual - q) < windowSec)
                {
                    dup = true;
                    break;
                }
            if (dup) continue;
            kept.Add(c.AnchorVirtual);
            double meanA = pattern.MeanPips * DensityProfile.PipPoints;
            double shiftPoints = c.Best.ShiftPips * DensityProfile.PipPoints;
            double meanC = c.MeanPips * DensityProfile.PipPoints;
            double meanB = c.Best.Mirrored ? meanC + shiftPoints : meanC - shiftPoints;
            results.Add(new FindResult(w.ToReal(c.AnchorVirtual - windowSec), c.Best.Sim, c.Best.Sim, 0,
                c.Best.Zoom, c.Best.Mirrored, c.Levels, meanA, meanB, target.Pair,
                Preferred: c.Best.Preferred, PreferredTotal: pattern.PreferredCount));
        }
        return results;
    }
}
