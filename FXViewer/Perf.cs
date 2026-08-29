using System.Diagnostics;
using System.Text;

namespace FXViewer;

public static class Perf
{
    private sealed class Counter
    {
        public long Count;
        public double TotalMs;
        public double MaxMs;
    }

    private readonly record struct Entry(string Name, double Ms);

    public const string LogName = "log.append";

    private const int RingSize = 16384;
    private const int SlowNamesShown = 10;
    private const int SummaryNamesShown = 14;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Counter> Counters = new(StringComparer.Ordinal);
    private static readonly Entry[] Ring = new Entry[RingSize];
    private static long _seq;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _windowStartMs;

    public static bool Enabled { get; set; } = true;

    public static double SlowFrameMs { get; set; } = 50;

    public static event Action<string>? Line;

    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _ticks;
        private readonly long _seqStart;
        private readonly bool _frame;

        internal Scope(string name, bool frame)
        {
            _name = name;
            _frame = frame;
            _seqStart = frame ? Mark() : 0;
            _ticks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name == null) return;
            if (_frame) Frame(_name, _ticks, _seqStart);
            else Add(_name, Elapsed(_ticks));
        }
    }

    public static long Now() => Stopwatch.GetTimestamp();

    public static long Mark()
    {
        lock (Gate) return _seq;
    }

    public static Scope Step(string name) => Enabled ? new Scope(name, false) : default;

    public static Scope FrameStep(string name) => Enabled ? new Scope(name, true) : default;

    public static long Since(string name, long from)
    {
        long now = Stopwatch.GetTimestamp();
        if (Enabled) Add(name, (now - from) * 1000.0 / Stopwatch.Frequency);
        return now;
    }

    public static void Count(string name)
    {
        if (Enabled) Add(name, 0);
    }

    public static void Frame(string name, long fromTicks, long fromSeq)
    {
        if (!Enabled) return;
        double ms = Elapsed(fromTicks);
        Add(name, ms);
        if (ms < SlowFrameMs) return;
        string? text = Breakdown(name, ms, fromSeq);
        if (text != null) Line?.Invoke(text);
    }

    public static string? Flush()
    {
        if (!Enabled) return null;
        List<(string Name, Counter C)> snapshot;
        double windowMs;
        lock (Gate)
        {
            if (Counters.Count == 0) return null;
            snapshot = new List<(string, Counter)>(Counters.Count);
            foreach (var kv in Counters) snapshot.Add((kv.Key, kv.Value));
            windowMs = Clock.Elapsed.TotalMilliseconds - _windowStartMs;
            _windowStartMs = Clock.Elapsed.TotalMilliseconds;
            Counters.Clear();
        }
        bool onlyLogging = true;
        foreach (var (n, _) in snapshot)
            if (n != LogName) { onlyLogging = false; break; }
        if (onlyLogging) return null;
        snapshot.Sort((a, b) => b.C.TotalMs.CompareTo(a.C.TotalMs));
        var sb = new StringBuilder();
        sb.Append("PERF ").Append((windowMs / 1000).ToString("F0")).Append("s:");
        for (int i = 0; i < snapshot.Count && i < SummaryNamesShown; i++)
        {
            var (n, c) = snapshot[i];
            sb.Append(i == 0 ? " " : " | ").Append(n)
                .Append(" x").Append(c.Count)
                .Append(" tot ").Append(c.TotalMs.ToString("F0"))
                .Append(" max ").Append(c.MaxMs.ToString("F0"));
        }
        if (snapshot.Count > SummaryNamesShown)
            sb.Append(" | +").Append(snapshot.Count - SummaryNamesShown).Append(" more");
        return sb.ToString();
    }

    private static double Elapsed(long fromTicks) =>
        (Stopwatch.GetTimestamp() - fromTicks) * 1000.0 / Stopwatch.Frequency;

    private static void Add(string name, double ms)
    {
        lock (Gate)
        {
            if (!Counters.TryGetValue(name, out var c))
            {
                c = new Counter();
                Counters[name] = c;
            }
            c.Count++;
            c.TotalMs += ms;
            if (ms > c.MaxMs) c.MaxMs = ms;
            Ring[(int)(_seq % RingSize)] = new Entry(name, ms);
            _seq++;
        }
    }

    private static string? Breakdown(string frameName, double frameMs, long fromSeq)
    {
        var parts = new List<(string Name, double Ms, int Count)>();
        lock (Gate)
        {
            long to = _seq - 1;
            long from = Math.Max(fromSeq, to - RingSize + 1);
            for (long i = from; i < to; i++)
            {
                var e = Ring[(int)(i % RingSize)];
                if (e.Name == null || e.Name == frameName) continue;
                int at = -1;
                for (int j = 0; j < parts.Count; j++)
                    if (parts[j].Name == e.Name) { at = j; break; }
                if (at < 0) parts.Add((e.Name, e.Ms, 1));
                else parts[at] = (e.Name, parts[at].Ms + e.Ms, parts[at].Count + 1);
            }
        }
        parts.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        var sb = new StringBuilder();
        sb.Append("PERF slow ").Append(frameName).Append(' ')
            .Append(frameMs.ToString("F0")).Append(" ms");
        for (int i = 0; i < parts.Count && i < SlowNamesShown; i++)
        {
            var (n, ms, count) = parts[i];
            if (ms < 1 && count <= 1) continue;
            sb.Append(" | ").Append(n).Append(' ').Append(ms.ToString("F0"));
            if (count > 1) sb.Append(" x").Append(count);
        }
        return sb.ToString();
    }
}
