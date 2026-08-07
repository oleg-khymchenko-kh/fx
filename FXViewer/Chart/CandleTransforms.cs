using FXViewer.Storage;

namespace FXViewer.Chart;

public static class CandleTransforms
{
    public static Candle[] Transform(
        List<Candle> candles, int pipPoints, bool mirror, out long mirrorBase, long? fixedMirrorBase = null)
    {
        mirrorBase = 0;
        var result = new Candle[candles.Count];
        if (pipPoints != 10)
        {
            for (int i = 0; i < result.Length; i++)
            {
                var c = candles[i];
                result[i] = new Candle(c.MinuteUnixSeconds,
                    ScalePoints(c.Min, pipPoints), ScalePoints(c.Max, pipPoints),
                    ScalePoints(c.Avg, pipPoints), c.AvgApproximated);
            }
        }
        else
        {
            candles.CopyTo(result);
        }
        if (mirror)
        {
            if (fixedMirrorBase != null)
            {
                mirrorBase = fixedMirrorBase.Value;
            }
            else
            {
                int mn = int.MaxValue;
                int mx = int.MinValue;
                foreach (var c in result)
                {
                    if (c.Min < mn) mn = c.Min;
                    if (c.Max > mx) mx = c.Max;
                }
                mirrorBase = (long)mn + mx;
            }
            for (int i = 0; i < result.Length; i++)
            {
                var c = result[i];
                result[i] = new Candle(c.MinuteUnixSeconds,
                    (int)(mirrorBase - c.Max), (int)(mirrorBase - c.Min),
                    (int)(mirrorBase - c.Avg), c.AvgApproximated);
            }
        }
        return result;
    }

    public static int ScalePoints(int points, int pipPoints) =>
        (int)(((long)points * 10 + pipPoints / 2) / pipPoints);
}
