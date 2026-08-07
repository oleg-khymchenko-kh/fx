using FXViewer.Storage;

namespace FXViewer;

public sealed class LiveDbWriter
{
    public sealed record RepairRange(string Symbol, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

    private sealed class Mark
    {
        public long ConfirmedToUnix;
        public long LastWrittenUnix;
        public int FailCount;
    }

    public const int MaxOpenRangeDays = 14;

    private const int RepairLagMinutes = 2;
    private const int MaxRepairSpanMinutes = 1440;
    private const int GiveUpAfterFailures = 10;

    private readonly CandleDatabase _db;
    private readonly Func<bool> _canWrite;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.OrdinalIgnoreCase);

    public LiveDbWriter(CandleDatabase db, Func<bool> canWrite, Action<string> log)
    {
        _db = db;
        _canWrite = canWrite;
        _log = log;
    }

    public void RecordMinute(string symbol, long minuteUnix, int min, int max, int avg)
    {
        if (!_marks.TryGetValue(symbol, out var mark))
        {
            mark = new Mark();
            _marks[symbol] = mark;
        }
        if (mark.ConfirmedToUnix != 0 && minuteUnix <= mark.ConfirmedToUnix) return;
        if (mark.ConfirmedToUnix == 0) mark.ConfirmedToUnix = minuteUnix - 60;
        if (minuteUnix > mark.LastWrittenUnix) mark.LastWrittenUnix = minuteUnix;
        if (_canWrite())
            _db.WriteMinute(symbol, UnixToUtc(minuteUnix), min, max, avg,
                avgApprox: true, provisional: true);
        Persist(symbol, mark);
    }

    public bool HasOpenRange(string symbol) =>
        _marks.TryGetValue(symbol, out var mark) && mark.LastWrittenUnix > mark.ConfirmedToUnix;

    public long ConfirmedToUnix(string symbol) =>
        _marks.TryGetValue(symbol, out var mark) ? mark.ConfirmedToUnix : 0;

    public IReadOnlyList<RepairRange> PlanRepair(DateTimeOffset nowUtc)
    {
        var result = new List<RepairRange>();
        long now = nowUtc.ToUnixTimeSeconds();
        long nowMinute = now - now % 60;
        long cap = nowMinute - RepairLagMinutes * 60L;
        long floor = nowMinute - MaxOpenRangeDays * 86400L;
        foreach (var (symbol, mark) in _marks)
        {
            if (mark.LastWrittenUnix <= mark.ConfirmedToUnix) continue;
            if (mark.LastWrittenUnix < floor)
            {
                _log($"{symbol}: provisional minutes older than {MaxOpenRangeDays} days are left as is");
                mark.ConfirmedToUnix = mark.LastWrittenUnix;
                Persist(symbol, mark);
                continue;
            }
            long from = mark.ConfirmedToUnix + 60;
            if (from < floor)
            {
                _log($"{symbol}: skipping provisional minutes older than {MaxOpenRangeDays} days");
                mark.ConfirmedToUnix = floor - 60;
                from = floor;
                Persist(symbol, mark);
            }
            long to = Math.Min(mark.LastWrittenUnix + 60, cap);
            to = Math.Min(to, from + MaxRepairSpanMinutes * 60L);
            if (to <= from) continue;
            result.Add(new RepairRange(symbol, UnixToUtc(from), UnixToUtc(to)));
        }
        return result;
    }

    public void MarkRepaired(string symbol, DateTimeOffset toUtc)
    {
        if (!_marks.TryGetValue(symbol, out var mark)) return;
        mark.FailCount = 0;
        long candidate = toUtc.ToUnixTimeSeconds() - 60;
        if (candidate <= mark.ConfirmedToUnix) return;
        mark.ConfirmedToUnix = candidate;
        Persist(symbol, mark);
    }

    public void MarkFailed(string symbol, DateTimeOffset toUtc)
    {
        if (!_marks.TryGetValue(symbol, out var mark)) return;
        mark.FailCount++;
        if (mark.FailCount < GiveUpAfterFailures) return;
        _log($"{symbol}: giving up on repair up to {toUtc:yyyy-MM-dd HH:mm} UTC, minutes stay provisional");
        mark.FailCount = 0;
        mark.ConfirmedToUnix = Math.Max(mark.ConfirmedToUnix, toUtc.ToUnixTimeSeconds() - 60);
        Persist(symbol, mark);
    }

    public void InstallMarks(IReadOnlyDictionary<string, (long ConfirmedTo, long LastWritten)> marks)
    {
        foreach (var (symbol, value) in marks)
        {
            if (value.LastWritten == 0) continue;
            if (_marks.ContainsKey(symbol)) continue;
            _marks[symbol] = new Mark
            {
                ConfirmedToUnix = value.ConfirmedTo,
                LastWrittenUnix = value.LastWritten,
            };
            if (value.LastWritten <= value.ConfirmedTo) continue;
            _log($"{symbol}: provisional minutes to repair " +
                $"{UnixToUtc(value.ConfirmedTo + 60):yyyy-MM-dd HH:mm}" +
                $"..{UnixToUtc(value.LastWritten):yyyy-MM-dd HH:mm} UTC");
        }
    }

    public static (long ConfirmedTo, long LastWritten) ReadMark(
        CandleDatabase db, string symbol, DateTimeOffset nowUtc)
    {
        var saved = LiveStore.Load(db.SymbolDirectory(symbol));
        if (saved != null) return (saved.ConfirmedToUnix, saved.LastWrittenUnix);
        var to = nowUtc.UtcDateTime;
        var from = to.AddDays(-MaxOpenRangeDays);
        var first = db.FirstProvisionalMinuteUtc(symbol, from, to);
        if (first == null) return (0, 0);
        var last = db.LastProvisionalMinuteUtc(symbol, from, to);
        long firstUnix = ((DateTimeOffset)first.Value).ToUnixTimeSeconds();
        long lastUnix = last == null ? firstUnix : ((DateTimeOffset)last.Value).ToUnixTimeSeconds();
        return (firstUnix - 60, lastUnix);
    }

    private void Persist(string symbol, Mark mark)
    {
        try
        {
            LiveStore.Save(_db.SymbolDirectory(symbol), new LiveData(
                LiveStore.CurrentVersion, mark.ConfirmedToUnix, mark.LastWrittenUnix,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
        catch (Exception ex)
        {
            _log($"{symbol}: cannot save live.json: {ex.Message}");
        }
    }

    private static DateTime UnixToUtc(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
}
