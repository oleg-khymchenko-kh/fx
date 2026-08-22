namespace FXViewer.Compute;

public static class MovingAverageSymbol
{
    public static int WindowBars(int period, string unit)
    {
        long bars = (long)Math.Max(1, period) * IndicatorUnits.BarsPerUnit(unit);
        return (int)Math.Clamp(bars, 1, int.MaxValue);
    }
}
