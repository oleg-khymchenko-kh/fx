namespace FXViewer.Storage;

public static class SpreadCodes
{
    public const int Unknown = -1;
    public const int Keep = -2;

    public const int TenthsBandTop = 127;
    public const int MinWholePips = 13;
    public const int MaxPips = 140;
    public const int WholePipBase = TenthsBandTop + 1 - MinWholePips;
    public const int MaxTenths = MaxPips * 10;

    public static int FromTenths(int tenths)
    {
        if (tenths <= 0) return 0;
        if (tenths <= TenthsBandTop) return tenths;
        int pips = (tenths + 5) / 10;
        if (pips < MinWholePips) pips = MinWholePips;
        if (pips > MaxPips) pips = MaxPips;
        return pips + WholePipBase;
    }

    public static int ToTenths(int code) =>
        code <= TenthsBandTop ? code : (code - WholePipBase) * 10;

    public static int TenthsFromPoints(long points, int pipPoints)
    {
        if (points <= 0 || pipPoints <= 0) return 0;
        return (int)((points * 10 + pipPoints / 2) / pipPoints);
    }

    public static int FromPoints(long points, int pipPoints) =>
        FromTenths(TenthsFromPoints(points, pipPoints));

    public static double ToPips(int code) => ToTenths(code) / 10.0;
}
