namespace FXViewer.Storage;

public readonly record struct Candle(long MinuteUnixSeconds, int Min, int Max, int Avg, bool AvgApproximated,
    bool HasSpread = false, int SpreadCode = 0, bool HasVolume = false, int Volume = 0,
    bool WideSpread = false)
{
    public DateTime TimeUtc => DateTimeOffset.FromUnixTimeSeconds(MinuteUnixSeconds).UtcDateTime;

    public int SpreadTenths => HasSpread ? SpreadCodes.ToTenths(SpreadCode) : -1;

    public double SpreadPips => SpreadCodes.ToPips(SpreadCode);
}
