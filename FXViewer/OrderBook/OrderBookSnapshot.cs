namespace FXViewer.OrderBook;

public sealed class OrderBookSnapshot
{
    public const int Rows = 338;
    public const int PriceScale = 100000;

    public long TimeUnix { get; init; }
    public long ImageTimeUnix { get; init; }
    public int PricePoints { get; init; }
    public int MiddlePoints { get; init; }
    public int StepPoints { get; init; }
    public int AnchorPoints { get; set; }
    public int MvoPoints { get; init; }
    public int MvpPoints { get; init; }
    public int SrPoints { get; init; }
    public int GrPoints { get; init; }
    public int ObPoints { get; init; }
    public int OsPoints { get; init; }
    public int OrdersCount { get; init; }
    public int PositionsCount { get; init; }

    public byte[] PendingLeft { get; init; } = new byte[Rows];
    public byte[] PendingRight { get; init; } = new byte[Rows];
    public byte[] PositionsLeft { get; init; } = new byte[Rows];
    public byte[] PositionsRight { get; init; } = new byte[Rows];

    public int PendingAt(int row) => PendingLeft[row] + PendingRight[row];

    public int PositionsAt(int row) => PositionsLeft[row] + PositionsRight[row];

    public int FramePoints => AnchorPoints != 0 ? AnchorPoints : MiddlePoints;

    public double RowPrice(int row) =>
        (FramePoints - (row - OrderBookGeometry.PriceRow) * (StepPoints / 2.0)) / (double)PriceScale;

    public int RowPricePoints(int row) =>
        (int)Math.Round(FramePoints - (row - OrderBookGeometry.PriceRow) * (StepPoints / 2.0));

    public static int ToPoints(double price) => (int)Math.Round(price * PriceScale);

    public static double FromPoints(int points) => points / (double)PriceScale;
}

public static class OrderBookGeometry
{
    public const int Width = 520;
    public const int Height = OrderBookSnapshot.Rows;
    public const int PriceRow = 151;
    public const double HalfWidthPx = 79.5;
    public const double HalfWidthPercent = 3.0;

    public const int PendingCenterX = 130;
    public const int PositionsCenterX = 390;
    public const int PanelReach = 85;

    public static readonly byte[] Teal = { 4, 166, 188 };
    public static readonly byte[] Orange = { 252, 134, 4 };

    public const int ColorTolerance = 26;

    public static double ToPercent(int pixels) => pixels / HalfWidthPx * HalfWidthPercent;
}
