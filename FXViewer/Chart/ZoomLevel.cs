using System.Text.Json.Serialization;

namespace FXViewer.Chart;

public sealed class ZoomLevel
{
    public const int MaxCount = 60;

    private const double DaySeconds = 86400;
    private const double Points100Pips = 1000;
    private const long MaxColumnSeconds = 86400L * 365;

    public double PixelsPerDay { get; set; }
    public double PixelsPer100Pips { get; set; }

    public ZoomLevel Clone() => new()
    {
        PixelsPerDay = PixelsPerDay,
        PixelsPer100Pips = PixelsPer100Pips,
    };

    [JsonIgnore]
    public bool IsValid => PixelsPerDay > 0 && PixelsPer100Pips > 0;

    public static long ToColumnSeconds(double pixelsPerDay)
    {
        if (!(pixelsPerDay > 0)) return ChartColumns.MinuteSeconds;
        double seconds = Math.Clamp(DaySeconds / pixelsPerDay, 1, MaxColumnSeconds);
        return ChartColumns.Snap((long)Math.Round(seconds));
    }

    public static double PixelsPerDayOf(long columnSeconds) =>
        columnSeconds > 0 ? DaySeconds / columnSeconds : 0;

    public static double ToPointsPerRow(double pixelsPer100Pips) =>
        pixelsPer100Pips > 0 ? Points100Pips / pixelsPer100Pips : 0;

    public static double PixelsPer100PipsOf(double pointsPerRow) =>
        pointsPerRow > 0 ? Points100Pips / pointsPerRow : 0;

    private static readonly double[] DefaultPixelsPerDay =
    {
        1, 1.5, 3, 6, 8, 12, 24, 36, 48, 72, 96, 144, 288, 480, 720, 1440, 2880, 5760, 14400, 86400,
    };

    private static readonly double[] DefaultPixelsPer100Pips =
    {
        28, 32, 38, 50, 56, 68, 100, 120, 135, 165, 190, 250, 335, 415, 525, 715, 1000, 1430, 2270, 6250,
    };

    public static List<ZoomLevel> Defaults()
    {
        var list = new List<ZoomLevel>(DefaultPixelsPerDay.Length);
        for (int i = 0; i < DefaultPixelsPerDay.Length; i++)
            list.Add(new ZoomLevel
            {
                PixelsPerDay = DefaultPixelsPerDay[i],
                PixelsPer100Pips = DefaultPixelsPer100Pips[i],
            });
        return list;
    }
}
