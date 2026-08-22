using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace FXViewer.OrderBook;

public static class OrderBookFetcher
{
    public const string ApiUrl = "https://c.fxssi.com/api/mini-oanda?pair=";
    public const string ImageUrl = "https://fxssi.com/images/snapshots/snapshot-";

    public readonly record struct Fetched(OrderBookSnapshot Snapshot, byte[] Png, string Report);

    private readonly record struct Nano(
        long TimeUnix, long ImageTimeUnix, double Price, double Middle, double Step,
        double Mvo, double Mvp, double Sr, double Gr, double Ob, double Os,
        int Orders, int Positions);

    public const int MaxBackfill = 48;

    public static async Task<List<Fetched>> FetchNewAsync(
        HttpClient client, string pair, long afterUnix, Action<string> log, CancellationToken ct)
    {
        var result = new List<Fetched>();
        var json = await client.GetStringAsync(ApiUrl + pair, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("nanos", out var nanos)
            || nanos.ValueKind != JsonValueKind.Object)
        {
            log($"{pair}: response has no nanos block");
            return result;
        }

        var pending = NewerThan(nanos, afterUnix);
        if (pending.Count == 0) return result;

        int missing = 0;
        foreach (var nano in pending)
        {
            ct.ThrowIfCancellationRequested();
            var image = await DownloadImageAsync(client, pair, nano, ct);
            if (image == null)
            {
                missing++;
                continue;
            }
            result.Add(Build(nano, image.Value.Stamp, image.Value.Png, pair, log));
        }
        if (missing > 0)
            log($"{pair}: {missing} of {pending.Count} snapshot image(s) not on the server");
        return result;
    }

    private static Fetched Build(Nano nano, long stamp, byte[] png, string pair, Action<string> log)
    {
        int middlePoints = OrderBookSnapshot.ToPoints(nano.Middle);
        int stepPoints = OrderBookSnapshot.ToPoints(nano.Step);
        var panels = OrderBookDecoder.Decode(png, middlePoints, stepPoints);
        if (!panels.Calibrated)
            log($"{pair}: axis labels not readable, frame anchor falls back to middle_price");
        var snapshot = new OrderBookSnapshot
        {
            TimeUnix = nano.TimeUnix,
            ImageTimeUnix = stamp,
            PricePoints = OrderBookSnapshot.ToPoints(nano.Price),
            MiddlePoints = middlePoints,
            StepPoints = stepPoints,
            AnchorPoints = panels.AnchorPoints,
            MvoPoints = OrderBookSnapshot.ToPoints(nano.Mvo),
            MvpPoints = OrderBookSnapshot.ToPoints(nano.Mvp),
            SrPoints = OrderBookSnapshot.ToPoints(nano.Sr),
            GrPoints = OrderBookSnapshot.ToPoints(nano.Gr),
            ObPoints = OrderBookSnapshot.ToPoints(nano.Ob),
            OsPoints = OrderBookSnapshot.ToPoints(nano.Os),
            OrdersCount = nano.Orders,
            PositionsCount = nano.Positions,
            PendingLeft = panels.PendingLeft,
            PendingRight = panels.PendingRight,
            PositionsLeft = panels.PositionsLeft,
            PositionsRight = panels.PositionsRight,
        };

        return new Fetched(snapshot, png, Describe(snapshot, panels, pair, log));
    }

    private static List<Nano> NewerThan(JsonElement nanos, long afterUnix)
    {
        var list = new List<Nano>();
        foreach (var item in nanos.EnumerateObject())
        {
            if (!long.TryParse(item.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                continue;
            var v = item.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            long time = (long)Number(v, "time", ms / 1000.0);
            if (time <= afterUnix) continue;
            double step = Number(v, "step", 0);
            double middle = Number(v, "middle_price", 0);
            if (step <= 0 || middle <= 0) continue;
            long imgTime = (long)Number(v, "imgTime", 0);
            list.Add(new Nano(
                time, imgTime > 0 ? imgTime : time,
                Number(v, "price", middle), middle, step,
                Number(v, "mvo", 0), Number(v, "mvp", 0),
                Number(v, "sr", 0), Number(v, "gr", 0),
                Number(v, "ob", 0), Number(v, "os", 0),
                (int)Number(v, "oio", 0), (int)Number(v, "oip", 0)));
        }
        list.Sort((a, b) => a.TimeUnix.CompareTo(b.TimeUnix));
        if (list.Count > MaxBackfill) list.RemoveRange(0, list.Count - MaxBackfill);
        return list;
    }

    private static async Task<(long Stamp, byte[] Png)?> DownloadImageAsync(
        HttpClient client, string pair, Nano nano, CancellationToken ct)
    {
        long[] stamps = nano.ImageTimeUnix != nano.TimeUnix
            ? new[] { nano.TimeUnix, nano.ImageTimeUnix }
            : new[] { nano.TimeUnix };
        foreach (long stamp in stamps)
        {
            string url = ImageUrl + pair + "-" + stamp.ToString(CultureInfo.InvariantCulture) + ".png";
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) continue;
            var png = await response.Content.ReadAsByteArrayAsync(ct);
            if (IsPng(png)) return (stamp, png);
        }
        return null;
    }

    private static bool IsPng(byte[] data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static double Number(JsonElement owner, string name, double fallback)
    {
        if (!owner.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback,
            _ => fallback,
        };
    }

    private static string Describe(OrderBookSnapshot s, OrderBookDecoder.Panels panels, string pair,
        Action<string> log)
    {
        if (panels.PendingCenterX != OrderBookGeometry.PendingCenterX
            || panels.PositionsCenterX != OrderBookGeometry.PositionsCenterX)
            log($"{pair}: panel centers moved to {panels.PendingCenterX}/{panels.PositionsCenterX}, " +
                $"expected {OrderBookGeometry.PendingCenterX}/{OrderBookGeometry.PositionsCenterX} " +
                "- check the layout before trusting these rows");

        long pending = Sum(s.PendingLeft) + Sum(s.PendingRight);
        long positions = Sum(s.PositionsLeft) + Sum(s.PositionsRight);
        if (pending == 0 || positions == 0)
            log($"{pair}: one of the panels decoded empty (pending {pending}, positions {positions})");

        double top = s.RowPrice(0);
        double bottom = s.RowPrice(OrderBookSnapshot.Rows - 1);
        double anchorPips = (s.FramePoints - s.MiddlePoints) / 10.0;
        return $"rows {bottom:0.00000}..{top:0.00000}, " +
            $"mass {OrderBookGeometry.ToPercent((int)pending):0.0}%/{OrderBookGeometry.ToPercent((int)positions):0.0}%, " +
            $"anchor {anchorPips:+0.0;-0.0;0.0}p";
    }

    private static long Sum(byte[] values)
    {
        long total = 0;
        foreach (byte v in values) total += v;
        return total;
    }
}
