using FXViewer.Storage;

namespace FXViewer;

public static class LiveTailPatch
{
    public static bool Apply(List<Candle> closed, Func<Candle, Candle> patch)
    {
        bool changed = false;
        for (int i = 0; i < closed.Count; i++)
        {
            var patched = patch(closed[i]);
            if (patched == closed[i]) continue;
            closed[i] = patched;
            changed = true;
        }
        return changed;
    }

    public static bool InsertMissing(List<Candle> closed, IReadOnlyList<Candle> fresh,
        long baseEndUnix, long openMinuteUnix)
    {
        var present = new HashSet<long>(closed.Count);
        foreach (var c in closed) present.Add(c.MinuteUnixSeconds);
        bool inserted = false;
        foreach (var c in fresh)
        {
            if (c.MinuteUnixSeconds <= baseEndUnix || c.MinuteUnixSeconds >= openMinuteUnix) continue;
            if (!present.Add(c.MinuteUnixSeconds)) continue;
            closed.Add(c);
            inserted = true;
        }
        if (inserted) closed.Sort((a, b) => a.MinuteUnixSeconds.CompareTo(b.MinuteUnixSeconds));
        return inserted;
    }
}
