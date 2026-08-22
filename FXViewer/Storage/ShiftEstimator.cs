namespace FXViewer.Storage;

public sealed class ShiftEstimator
{
    public const int MaxSamples = 60;
    public const int MaxAgeSeconds = 6 * 3600;

    private readonly Queue<(long MinuteUnix, int Raw)> _window = new();

    public int Push(long minuteUnix, int spotAvgPoints, int futVwapPoints)
    {
        int raw = spotAvgPoints - futVwapPoints;
        _window.Enqueue((minuteUnix, raw));
        while (_window.Count > MaxSamples) _window.Dequeue();
        while (_window.Count > 1 && minuteUnix - _window.Peek().MinuteUnix > MaxAgeSeconds) _window.Dequeue();
        return Median();
    }

    public void Reset() => _window.Clear();

    private int Median()
    {
        var sorted = _window.Select(s => s.Raw).ToArray();
        Array.Sort(sorted);
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (int)Math.Round((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0);
    }
}
