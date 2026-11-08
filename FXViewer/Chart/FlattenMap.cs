using FXViewer.Compute;

namespace FXViewer.Chart;

public abstract class FlattenMap
{
    public abstract double MaxAbsShift { get; }

    public abstract double ShiftAt(long virtualSeconds);

    public abstract double[] ColumnShifts(long firstBucket, int count, long bucketSeconds);

    public abstract long[] CutsBetween(long fromVirtual, long toVirtual, long columnSeconds,
        long clipFrom = long.MinValue, long clipTo = long.MaxValue);

    public static FlattenMap? Build(PivotPoint[] points, SeriesTransform transform,
        WeekendCompressor? map) =>
        LineFlattenMap.FromPoints(points, transform, map);
}
