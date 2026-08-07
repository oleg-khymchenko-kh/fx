namespace FXViewer.Chart;

public static class LineDecimator
{
    private const int Infinity = int.MaxValue;

    public static int[] ChooseValues(ColumnAggregate[] columns, int lookback, int noiseThreshold)
    {
        var chosen = new int[columns.Length];
        var maxSeq = new int[columns.Length];
        var maxVal = new int[columns.Length];
        var minSeq = new int[columns.Length];
        var minVal = new int[columns.Length];
        int maxTop = -1;
        int minTop = -1;
        int seq = 0;
        bool hasPrev = false;
        ColumnAggregate prev = default;
        for (int i = 0; i < columns.Length; i++)
        {
            var cur = columns[i];
            if (!cur.HasData) continue;
            while (maxTop >= 0 && maxVal[maxTop] < cur.Max) maxTop--;
            int dMax = Infinity;
            int maxCoverSeq = -1;
            if (maxTop >= 0 && seq - maxSeq[maxTop] <= lookback)
            {
                dMax = seq - maxSeq[maxTop];
                maxCoverSeq = maxSeq[maxTop];
            }
            while (minTop >= 0 && minVal[minTop] > cur.Min) minTop--;
            int dMin = Infinity;
            int minCoverSeq = -2;
            if (minTop >= 0 && seq - minSeq[minTop] <= lookback)
            {
                dMin = seq - minSeq[minTop];
                minCoverSeq = minSeq[minTop];
            }
            chosen[i] = Choose(cur, prev, hasPrev, dMax, dMin, maxCoverSeq, minCoverSeq, noiseThreshold);
            maxTop++;
            maxSeq[maxTop] = seq;
            maxVal[maxTop] = cur.Max;
            minTop++;
            minSeq[minTop] = seq;
            minVal[minTop] = cur.Min;
            prev = cur;
            hasPrev = true;
            seq++;
        }
        return chosen;
    }

    private static int Choose(ColumnAggregate cur, ColumnAggregate prev, bool hasPrev,
        int dMax, int dMin, int maxCoverSeq, int minCoverSeq, int noiseThreshold)
    {
        if (!hasPrev) return cur.Avg;
        if (maxCoverSeq == minCoverSeq) return cur.Avg;
        if (dMax < noiseThreshold && dMin < noiseThreshold) return cur.Avg;
        if (dMax > dMin) return cur.Max;
        if (dMax < dMin) return cur.Min;
        int upDev = cur.Max - prev.Max;
        int downDev = prev.Min - cur.Min;
        return upDev >= downDev ? cur.Max : cur.Min;
    }
}
