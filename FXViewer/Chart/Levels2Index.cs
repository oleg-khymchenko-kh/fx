using FXViewer.Storage;

namespace FXViewer.Chart;

public sealed class Levels2Index
{
    private readonly record struct Node(int Previous, int Approach, int Peak, int Trough);

    private sealed class Side
    {
        public required Node[] Nodes;
        public required int[] Passing;
        public required int[] PassingReversal;
    }

    public sealed class Scratch
    {
        internal readonly List<int> ChainIndex = new();
        internal readonly List<int> ChainReversal = new();
    }

    private readonly Candle[] _minutes;
    private readonly double _minReversalPoints;
    private readonly Side _tops;
    private readonly Side _bottoms;

    public Levels2Index(Candle[] minutes, double minApproachPoints, double minReversalPoints)
    {
        _minutes = minutes;
        _minReversalPoints = minReversalPoints;
        _tops = Build(minutes, true, minApproachPoints, minReversalPoints);
        _bottoms = Build(minutes, false, minApproachPoints, minReversalPoints);
    }

    public int Length => _minutes.Length;

    public int IndexAfter(long unixSeconds)
    {
        int lo = 0;
        int hi = _minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_minutes[mid].MinuteUnixSeconds <= unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public void Levels(long fromUnix, long anchorUnix, Scratch scratch, List<Level2> output)
    {
        output.Clear();
        int last = IndexAfter(anchorUnix) - 1;
        int first = IndexAfter(fromUnix - 1);
        if (last < 0 || first > last) return;
        Collect(_tops, true, first, last, scratch, output);
        Collect(_bottoms, false, first, last, scratch, output);
    }

    private static int Peak(Candle c, bool top) => top ? c.Max : -c.Min;

    private static int Trough(Candle c, bool top) => top ? c.Min : -c.Max;

    private void Collect(Side side, bool top, int first, int last, Scratch scratch, List<Level2> output)
    {
        var chainIndex = scratch.ChainIndex;
        var chainReversal = scratch.ChainReversal;
        chainIndex.Clear();
        chainReversal.Clear();
        var nodes = side.Nodes;
        int running = int.MaxValue;
        for (int c = last; c >= first;)
        {
            var node = nodes[c];
            chainIndex.Add(c);
            chainReversal.Add(running == int.MaxValue ? 0 : node.Peak - running);
            running = Math.Min(running, Math.Min(node.Trough, node.Peak - node.Approach));
            c = node.Previous;
        }
        var passing = side.Passing;
        int lo = 0;
        int hi = passing.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (passing[mid] < first) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < passing.Length && passing[i] <= last; i++)
        {
            int k = passing[i];
            int reversal = ChainReversal(chainIndex, chainReversal, k, out bool onChain);
            if (!onChain) reversal = side.PassingReversal[i];
            if (reversal < _minReversalPoints) continue;
            var node = nodes[k];
            output.Add(new Level2(k, top ? node.Peak : -node.Peak, top, node.Approach, reversal));
        }
    }

    private static int ChainReversal(List<int> chainIndex, List<int> chainReversal, int index, out bool onChain)
    {
        int lo = 0;
        int hi = chainIndex.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (chainIndex[mid] > index) lo = mid + 1;
            else hi = mid;
        }
        onChain = lo < chainIndex.Count && chainIndex[lo] == index;
        return onChain ? chainReversal[lo] : 0;
    }

    private static Side Build(Candle[] minutes, bool top, double minApproachPoints, double minReversalPoints)
    {
        int n = minutes.Length;
        var nodes = new Node[n];
        var stackIndex = new int[n];
        var stackGap = new int[n];
        int sp = 0;
        int baseMin = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var candle = minutes[i];
            int peak = Peak(candle, top);
            int acc = int.MaxValue;
            while (sp > 0 && nodes[stackIndex[sp - 1]].Peak < peak)
            {
                sp--;
                acc = Math.Min(acc, Math.Min(nodes[stackIndex[sp]].Trough, stackGap[sp]));
            }
            int previous;
            int approach;
            if (sp > 0)
            {
                int gap = Math.Min(stackGap[sp - 1], acc);
                stackGap[sp - 1] = gap;
                previous = stackIndex[sp - 1];
                approach = gap == int.MaxValue ? 0 : peak - gap;
            }
            else
            {
                baseMin = Math.Min(baseMin, acc);
                previous = -1;
                approach = baseMin == int.MaxValue ? 0 : peak - baseMin;
            }
            nodes[i] = new Node(previous, approach, peak, Trough(candle, top));
            stackIndex[sp] = i;
            stackGap[sp] = int.MaxValue;
            sp++;
        }
        var passing = new List<int>();
        var passingReversal = new List<int>();
        sp = 0;
        baseMin = int.MaxValue;
        for (int i = n - 1; i >= 0; i--)
        {
            int peak = nodes[i].Peak;
            int acc = int.MaxValue;
            while (sp > 0 && nodes[stackIndex[sp - 1]].Peak <= peak)
            {
                sp--;
                acc = Math.Min(acc, Math.Min(nodes[stackIndex[sp]].Trough, stackGap[sp]));
            }
            int lowest;
            if (sp > 0)
            {
                stackGap[sp - 1] = Math.Min(stackGap[sp - 1], acc);
                lowest = stackGap[sp - 1];
            }
            else
            {
                baseMin = Math.Min(baseMin, acc);
                lowest = baseMin;
            }
            int reversal = lowest == int.MaxValue ? 0 : peak - lowest;
            if (nodes[i].Approach >= minApproachPoints && reversal >= minReversalPoints)
            {
                passing.Add(i);
                passingReversal.Add(reversal);
            }
            stackIndex[sp] = i;
            stackGap[sp] = int.MaxValue;
            sp++;
        }
        passing.Reverse();
        passingReversal.Reverse();
        return new Side
        {
            Nodes = nodes,
            Passing = passing.ToArray(),
            PassingReversal = passingReversal.ToArray(),
        };
    }
}
