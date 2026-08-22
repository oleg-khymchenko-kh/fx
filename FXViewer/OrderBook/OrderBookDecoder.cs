using System.IO;
using System.Windows.Media.Imaging;

namespace FXViewer.OrderBook;

public static class OrderBookDecoder
{
    public const double LabelRowBias = -1.33;
    public const double MaxFrameOffsetRows = 6.0;

    public readonly record struct Panels(
        byte[] PendingLeft, byte[] PendingRight, byte[] PositionsLeft, byte[] PositionsRight,
        int PendingCenterX, int PositionsCenterX, int AnchorPoints, bool Calibrated);

    public static Panels Decode(byte[] png, int middlePoints, int stepPoints)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        int w = frame.PixelWidth;
        int h = frame.PixelHeight;
        if (w != OrderBookGeometry.Width || h != OrderBookGeometry.Height)
            throw new InvalidDataException(
                $"snapshot is {w}x{h}, expected {OrderBookGeometry.Width}x{OrderBookGeometry.Height}");

        var bgra = new BitmapImageConverter(frame);
        var (centerA, centerB) = FindCenters(bgra, w, h);

        var pendingLeft = new byte[OrderBookSnapshot.Rows];
        var pendingRight = new byte[OrderBookSnapshot.Rows];
        var positionsLeft = new byte[OrderBookSnapshot.Rows];
        var positionsRight = new byte[OrderBookSnapshot.Rows];

        for (int y = 0; y < h; y++)
        {
            pendingLeft[y] = (byte)CountSide(bgra, w, y, centerA, left: true);
            pendingRight[y] = (byte)CountSide(bgra, w, y, centerA, left: false);
            positionsLeft[y] = (byte)CountSide(bgra, w, y, centerB, left: true);
            positionsRight[y] = (byte)CountSide(bgra, w, y, centerB, left: false);
        }

        var (anchorPoints, calibrated) = Calibrate(bgra, w, h, middlePoints, stepPoints);
        return new Panels(pendingLeft, pendingRight, positionsLeft, positionsRight,
            centerA, centerB, anchorPoints, calibrated);
    }

    private static (int AnchorPoints, bool Ok) Calibrate(
        BitmapImageConverter px, int w, int h, int middlePoints, int stepPoints)
    {
        if (stepPoints <= 0 || middlePoints <= 0) return (middlePoints, false);
        double halfStep = stepPoints / 2.0;
        double phase = LabelChainPhase(px, w, h);
        if (double.IsNaN(phase)) return (middlePoints, false);
        double predicted = (OrderBookGeometry.PriceRow
            + middlePoints % (stepPoints * 10) / halfStep) % 20.0;
        double resid = WrapHalf(phase % 20.0 - predicted);
        double offsetRows = resid - LabelRowBias;
        if (Math.Abs(offsetRows) > MaxFrameOffsetRows) return (middlePoints, false);
        return (middlePoints + (int)Math.Round(offsetRows * halfStep), true);
    }

    private static double LabelChainPhase(BitmapImageConverter px, int w, int h)
    {
        var darkCount = new int[h];
        var weight = new double[h];
        for (int y = 0; y < h; y++)
        {
            int dark = 0;
            double sum = 0;
            for (int x = w - 42; x < w - 4; x++)
            {
                var (r, g, b) = px.Rgb(x, y);
                double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                if (lum < 200) sum += 200.0 - lum;
                if (r < 120 && g < 120 && b < 120) dark++;
            }
            darkCount[y] = dark;
            weight[y] = sum;
        }

        var centers = new List<double>();
        int start = -1;
        for (int y = 0; y < h; y++)
        {
            bool has = darkCount[y] > 0;
            if (has && start < 0) start = y;
            if ((!has || y == h - 1) && start >= 0)
            {
                int end = has ? y : y - 1;
                double num = 0;
                double den = 0;
                for (int z = Math.Max(0, start - 1); z <= Math.Min(h - 1, end + 1); z++)
                {
                    num += z * weight[z];
                    den += weight[z];
                }
                centers.Add(den > 0 ? num / den : (start + end) / 2.0);
                start = -1;
            }
        }

        var chain = new List<double>();
        foreach (double c in centers)
        {
            if (chain.Count == 0)
            {
                chain.Add(c);
                continue;
            }
            double gap = c - chain[^1];
            if (gap >= 18 && gap <= 22)
                chain.Add(c);
            else if (gap > 22 && chain.Count < 3)
            {
                chain.Clear();
                chain.Add(c);
            }
        }
        if (chain.Count < 6) return double.NaN;

        var residues = new double[chain.Count];
        for (int k = 0; k < chain.Count; k++)
            residues[k] = chain[k] - 20.0 * k;
        Array.Sort(residues);
        return residues[residues.Length / 2];
    }

    private static double WrapHalf(double v)
    {
        while (v > 10) v -= 20;
        while (v < -10) v += 20;
        return v;
    }

    private static (int Left, int Right) FindCenters(BitmapImageConverter px, int w, int h)
    {
        var counts = new int[w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (IsBar(px, x, y)) counts[x]++;

        int mid = w / 2;
        int left = ArgMax(counts, 0, mid - 1);
        int right = ArgMax(counts, mid, w - 1);
        if (counts[left] == 0 || counts[right] == 0)
            throw new InvalidDataException("no histogram bars found in the snapshot");
        return (left, right);
    }

    private static int ArgMax(int[] values, int from, int to)
    {
        int best = from;
        for (int i = from; i <= to; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }

    private static int CountSide(BitmapImageConverter px, int w, int y, int centerX, bool left)
    {
        int from = left ? Math.Max(0, centerX - OrderBookGeometry.PanelReach) : centerX + 1;
        int to = left ? centerX - 1 : Math.Min(w - 1, centerX + OrderBookGeometry.PanelReach);
        int count = 0;
        for (int x = from; x <= to; x++)
            if (IsBar(px, x, y)) count++;
        return Math.Min(count, byte.MaxValue);
    }

    private static bool IsBar(BitmapImageConverter px, int x, int y)
    {
        var (r, g, b) = px.Rgb(x, y);
        return Near(r, g, b, OrderBookGeometry.Teal) || Near(r, g, b, OrderBookGeometry.Orange);
    }

    private static bool Near(byte r, byte g, byte b, byte[] target) =>
        Math.Abs(r - target[0]) < OrderBookGeometry.ColorTolerance
        && Math.Abs(g - target[1]) < OrderBookGeometry.ColorTolerance
        && Math.Abs(b - target[2]) < OrderBookGeometry.ColorTolerance;

    private sealed class BitmapImageConverter
    {
        private readonly byte[] _pixels;
        private readonly int _stride;

        public BitmapImageConverter(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32,
                null, 0);
            _stride = converted.PixelWidth * 4;
            _pixels = new byte[_stride * converted.PixelHeight];
            converted.CopyPixels(_pixels, _stride, 0);
        }

        public (byte R, byte G, byte B) Rgb(int x, int y)
        {
            int i = y * _stride + x * 4;
            return (_pixels[i + 2], _pixels[i + 1], _pixels[i]);
        }
    }
}
