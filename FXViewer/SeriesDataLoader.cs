using System.Diagnostics;
using System.Windows.Threading;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer;

public sealed class SeriesDataLoader : IDisposable
{
    public sealed record SeriesInit(string Symbol, string ReadSymbol, bool Mirror, int PipPoints,
        bool IsShift, long ShiftDelta, int MinYear, int MaxYear,
        int? LoadedFromYear, int? LoadedToYear, long MirrorBase);

    private sealed class SeriesState
    {
        public required string Symbol { get; init; }
        public required string ReadSymbol { get; set; }
        public required bool Mirror { get; set; }
        public required int PipPoints { get; set; }
        public required bool IsShift { get; init; }
        public required int MinYear { get; set; }
        public required int MaxYear { get; set; }
        public long ShiftDelta;
        public bool HasLoaded;
        public int LoadedLo;
        public int LoadedHi;
        public long MirrorBase;
    }

    private sealed class Job
    {
        public required string Symbol { get; init; }
        public required string Reason { get; init; }
        public int FromYear;
        public int ToYear;
        public bool Active;
        public string? Progress;
        public readonly List<TaskCompletionSource> Waiters = new();
    }

    private enum Side { Init, Prepend, Append }

    private readonly CandleDatabase _db;
    private readonly ChartView _chart;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly Action<string, long> _mirrorBaseComputed;
    private readonly Dictionary<string, SeriesState> _series = new();
    private readonly List<Job> _jobs = new();
    private readonly HashSet<string> _paused = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    public event Action? JobsChanged;

    public SeriesDataLoader(CandleDatabase db, ChartView chart, Dispatcher dispatcher,
        IEnumerable<SeriesInit> series, Action<string> log, Action<string, long> mirrorBaseComputed)
    {
        _db = db;
        _chart = chart;
        _dispatcher = dispatcher;
        _log = log;
        _mirrorBaseComputed = mirrorBaseComputed;
        foreach (var s in series)
        {
            var st = new SeriesState
            {
                Symbol = s.Symbol,
                ReadSymbol = s.ReadSymbol,
                Mirror = s.Mirror,
                PipPoints = s.PipPoints,
                IsShift = s.IsShift,
                MinYear = s.MinYear,
                MaxYear = s.MaxYear,
                ShiftDelta = s.ShiftDelta,
                MirrorBase = s.MirrorBase,
            };
            if (s.LoadedFromYear != null && s.LoadedToYear != null)
            {
                st.HasLoaded = true;
                st.LoadedLo = s.LoadedFromYear.Value;
                st.LoadedHi = s.LoadedToYear.Value;
            }
            _series[st.Symbol] = st;
        }
        _ = Task.Run(WorkerLoopAsync);
    }

    public void EnsureVisibleRange(long realLo, long realHi, IReadOnlyCollection<string> hiddenSymbols)
    {
        bool added = false;
        lock (_sync)
        {
            foreach (var st in _series.Values)
            {
                if (hiddenSymbols.Contains(st.Symbol)) continue;
                long lo = realLo;
                long hi = realHi;
                if (st.IsShift)
                {
                    lo = ChartToSource(lo, st.ShiftDelta);
                    hi = ChartToSource(hi, st.ShiftDelta);
                }
                int ylo = Math.Max(st.MinYear, YearOfUnix(lo));
                int yhi = Math.Min(st.MaxYear, YearOfUnix(hi));
                if (ylo > yhi) continue;
                added |= EnqueueLocked(st, ylo, yhi, "scroll");
            }
        }
        if (added)
        {
            _signal.Release();
            JobsChanged?.Invoke();
        }
    }

    public void EnsureFullVisible(IReadOnlyCollection<string> hiddenSymbols)
    {
        bool added = false;
        lock (_sync)
        {
            foreach (var st in _series.Values)
            {
                if (hiddenSymbols.Contains(st.Symbol)) continue;
                added |= EnqueueLocked(st, st.MinYear, st.MaxYear, "fit");
            }
        }
        if (added)
        {
            _signal.Release();
            JobsChanged?.Invoke();
        }
    }

    public Task EnsureFullAsync(string symbol, string reason) =>
        EnsureYearsAsync(symbol, int.MinValue, int.MaxValue, reason);

    public Task EnsureYearsAsync(string symbol, int fromYear, int toYear, string reason)
    {
        Task result;
        lock (_sync)
        {
            if (!_series.TryGetValue(symbol, out var st)) return Task.CompletedTask;
            int lo = Math.Max(st.MinYear, fromYear);
            int hi = Math.Min(st.MaxYear, toYear);
            if (lo > hi || (st.HasLoaded && st.LoadedLo <= lo && st.LoadedHi >= hi))
                return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = new Job { Symbol = symbol, Reason = reason, FromYear = lo, ToYear = hi };
            job.Waiters.Add(tcs);
            _jobs.Add(job);
            result = tcs.Task;
        }
        _signal.Release();
        JobsChanged?.Invoke();
        return result;
    }

    public bool IsBusy(string symbol)
    {
        lock (_sync)
        {
            foreach (var j in _jobs)
                if (j.Symbol == symbol) return true;
        }
        return false;
    }

    public Task PauseAsync(string symbol)
    {
        lock (_sync)
        {
            _paused.Add(symbol);
            Job? active = null;
            foreach (var j in _jobs)
                if (j.Symbol == symbol && j.Active) { active = j; break; }
            if (active == null) return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            active.Waiters.Add(tcs);
            return tcs.Task.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    public void Resume(string symbol)
    {
        bool pending;
        lock (_sync)
        {
            _paused.Remove(symbol);
            pending = HasRunnableLocked();
        }
        if (pending) _signal.Release();
    }

    public void SetShiftDelta(string symbol, long newDelta)
    {
        lock (_sync)
        {
            if (_series.TryGetValue(symbol, out var st)) st.ShiftDelta = newDelta;
        }
    }

    public void MarkShiftApplied(string symbol, string targetSymbol, long newDelta, bool mirror,
        long mirrorBase, int pipPoints)
    {
        lock (_sync)
        {
            if (!_series.TryGetValue(symbol, out var st)) return;
            if (_series.TryGetValue(targetSymbol, out var tgt))
            {
                st.ReadSymbol = tgt.ReadSymbol;
                st.MinYear = tgt.MinYear;
                st.MaxYear = tgt.MaxYear;
            }
            st.Mirror = mirror;
            st.MirrorBase = mirrorBase;
            st.PipPoints = pipPoints;
            st.ShiftDelta = newDelta;
            st.HasLoaded = true;
            st.LoadedLo = st.MinYear;
            st.LoadedHi = st.MaxYear;
        }
    }

    public void MarkShiftRetargeted(string symbol, string targetSymbol, long newDelta, bool mirror,
        long mirrorBase, int pipPoints)
    {
        lock (_sync)
        {
            if (!_series.TryGetValue(symbol, out var st)) return;
            if (_series.TryGetValue(targetSymbol, out var tgt))
            {
                st.ReadSymbol = tgt.ReadSymbol;
                st.MinYear = tgt.MinYear;
                st.MaxYear = tgt.MaxYear;
                st.HasLoaded = tgt.HasLoaded;
                st.LoadedLo = tgt.LoadedLo;
                st.LoadedHi = tgt.LoadedHi;
            }
            st.Mirror = mirror;
            st.MirrorBase = mirrorBase;
            st.PipPoints = pipPoints;
            st.ShiftDelta = newDelta;
        }
    }

    public IReadOnlyList<string> DescribeJobs()
    {
        lock (_sync)
        {
            var list = new List<string>(_jobs.Count);
            foreach (var j in _jobs)
            {
                string state = j.Active
                    ? j.Progress == null ? "reading" : "reading " + j.Progress
                    : "queued";
                list.Add($"{j.Symbol} {YearSpanText(j.FromYear, j.ToYear)} · {state} ({j.Reason})");
            }
            return list;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        List<TaskCompletionSource> waiters = new();
        lock (_sync)
        {
            foreach (var j in _jobs) waiters.AddRange(j.Waiters);
            _jobs.Clear();
        }
        foreach (var w in waiters) w.TrySetCanceled();
        _signal.Release();
    }

    private bool HasRunnableLocked()
    {
        foreach (var j in _jobs)
            if (!j.Active && !_paused.Contains(j.Symbol)) return true;
        return false;
    }

    private bool EnqueueLocked(SeriesState st, int ylo, int yhi, string reason)
    {
        int covLo = st.HasLoaded ? st.LoadedLo : int.MaxValue;
        int covHi = st.HasLoaded ? st.LoadedHi : int.MinValue;
        Job? queued = null;
        foreach (var j in _jobs)
        {
            if (j.Symbol != st.Symbol) continue;
            covLo = Math.Min(covLo, j.FromYear);
            covHi = Math.Max(covHi, j.ToYear);
            if (!j.Active) queued = j;
        }
        if (covLo != int.MaxValue && ylo >= covLo && yhi <= covHi) return false;
        if (queued != null)
        {
            queued.FromYear = Math.Min(queued.FromYear, ylo);
            queued.ToYear = Math.Max(queued.ToYear, yhi);
            return false;
        }
        _jobs.Add(new Job { Symbol = st.Symbol, Reason = reason, FromYear = ylo, ToYear = yhi });
        return true;
    }

    private async Task WorkerLoopAsync()
    {
        var ct = _cts.Token;
        while (true)
        {
            try
            {
                await _signal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (ct.IsCancellationRequested) return;
            Job? job = null;
            lock (_sync)
            {
                foreach (var j in _jobs)
                    if (!j.Active && !_paused.Contains(j.Symbol)) { job = j; break; }
                if (job != null) job.Active = true;
            }
            if (job == null) continue;
            JobsChanged?.Invoke();
            bool ok = false;
            string? error = null;
            try
            {
                await RunJobAsync(job, ct);
                ok = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            lock (_sync) _jobs.Remove(job);
            foreach (var w in job.Waiters)
            {
                if (ok) w.TrySetResult();
                else if (error != null) w.TrySetException(new InvalidOperationException(error));
                else w.TrySetCanceled();
            }
            if (error != null)
            {
                string message = $"Load failed: {job.Symbol} {YearSpanText(job.FromYear, job.ToYear)}: {error}";
                Post(() => _log(message));
            }
            JobsChanged?.Invoke();
            if (ct.IsCancellationRequested) return;
            bool pending;
            lock (_sync) pending = HasRunnableLocked();
            if (pending) _signal.Release();
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken ct)
    {
        SeriesState? st;
        (int Lo, int Hi)? init = null;
        (int Lo, int Hi)? pre = null;
        (int Lo, int Hi)? post = null;
        lock (_sync)
        {
            if (!_series.TryGetValue(job.Symbol, out st)) return;
            if (!st.HasLoaded)
            {
                init = (job.FromYear, job.ToYear);
            }
            else
            {
                if (job.FromYear < st.LoadedLo) pre = (job.FromYear, st.LoadedLo - 1);
                if (job.ToYear > st.LoadedHi) post = (st.LoadedHi + 1, job.ToYear);
            }
        }
        if (init != null) await LoadPartAsync(job, st, Side.Init, init.Value.Lo, init.Value.Hi, ct);
        if (pre != null) await LoadPartAsync(job, st, Side.Prepend, pre.Value.Lo, pre.Value.Hi, ct);
        if (post != null) await LoadPartAsync(job, st, Side.Append, post.Value.Lo, post.Value.Hi, ct);
    }

    private async Task LoadPartAsync(Job job, SeriesState st, Side side, int yearLo, int yearHi,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var raw = new List<Candle>();
        for (int y = yearLo; y <= yearHi; y++)
        {
            ct.ThrowIfCancellationRequested();
            SetProgress(job, y.ToString());
            raw.AddRange(_db.ReadRange(st.ReadSymbol, YearStart(y), YearEnd(y), includeWide: true));
        }
        raw = AskViewRule.ToAsk(raw, st.ReadSymbol);
        if (st.IsShift) raw = ShiftedSymbol.Shift(raw, st.ShiftDelta);
        var (minutes, rawHidden) = SplitHidden(raw);
        raw = minutes;
        SetProgress(job, "merge");
        for (int attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snap = await _dispatcher.InvokeAsync(() =>
            {
                var s = _chart.GetSeries(st.Symbol);
                return s == null ? null : new { s.History, Averages = _chart.AverageJobsOf(st.Symbol) };
            });
            if (snap == null) return;
            List<Candle> chunk;
            List<Candle> hiddenChunk;
            if (snap.History.Minutes.Length == 0)
            {
                chunk = raw;
                hiddenChunk = rawHidden;
            }
            else if (side == Side.Prepend || side == Side.Init)
            {
                long first = snap.History.Minutes[0].MinuteUnixSeconds;
                chunk = raw.Where(c => c.MinuteUnixSeconds < first).ToList();
                hiddenChunk = rawHidden.Where(c => c.MinuteUnixSeconds < first).ToList();
            }
            else
            {
                long last = snap.History.Minutes[^1].MinuteUnixSeconds;
                chunk = raw.Where(c => c.MinuteUnixSeconds > last).ToList();
                hiddenChunk = rawHidden.Where(c => c.MinuteUnixSeconds > last).ToList();
            }
            if (chunk.Count == 0)
            {
                bool done = await _dispatcher.InvokeAsync(() => !_cts.IsCancellationRequested);
                if (!done) return;
                CommitSpan(st, yearLo, yearHi);
                return;
            }
            long? fixedBase = st.Mirror && st.MirrorBase != 0 ? st.MirrorBase : null;
            var transformed = CandleTransforms.Transform(chunk, st.PipPoints, st.Mirror, out long mb, fixedBase);
            bool newBase = st.Mirror && fixedBase == null;
            bool prepend = snap.History.Minutes.Length > 0
                && transformed[^1].MinuteUnixSeconds < snap.History.Minutes[0].MinuteUnixSeconds;
            var newHistory = prepend
                ? snap.History.WithReplacedRange(0, 0, transformed)
                : snap.History.WithReplacedRange(snap.History.Minutes.Length, 0, transformed);
            var hiddenMarks = CandleHistory.MarksOf(hiddenChunk);
            newHistory.SetHiddenSpreads(prepend
                ? CandleHistory.MergeMarks(hiddenMarks, snap.History.HiddenSpreads)
                : CandleHistory.MergeMarks(snap.History.HiddenSpreads, hiddenMarks));
            newHistory.SetHiddenMinutes(CandleHistory.MergeByTime(snap.History.HiddenMinutes,
                CandleTransforms.TransformWith(hiddenChunk, st.PipPoints, st.Mirror, mb)));
            var newTransform = newBase ? new SeriesTransform(true, mb, st.PipPoints) : null;
            ct.ThrowIfCancellationRequested();
            SetProgress(job, "averages");
            var avgWatch = Stopwatch.StartNew();
            var averages = AverageSeries.Rebuild(snap.Averages, newHistory.AverageMinutes);
            long avgMs = avgWatch.ElapsedMilliseconds;
            bool committed = await _dispatcher.InvokeAsync(() =>
            {
                if (_cts.IsCancellationRequested) return false;
                var s = _chart.GetSeries(st.Symbol);
                if (s == null || !ReferenceEquals(s.History, snap.History)) return false;
                newHistory.SetLive(s.History.Live, s.History.LiveHidden);
                if (s.History.HasLastTick) newHistory.SetLastTick(s.History.LastTick);
                _chart.ReplaceSeries(st.Symbol, newHistory, newTransform, averages);
                if (newBase) _mirrorBaseComputed(st.Symbol, mb);
                return true;
            });
            if (committed)
            {
                if (newBase) st.MirrorBase = mb;
                CommitSpan(st, yearLo, yearHi);
                string averagesText = averages.Length > 0
                    ? $", {averages.Length} average(s) in {avgMs} ms"
                    : "";
                string message = $"Loaded {st.Symbol} {YearSpanText(yearLo, yearHi)}: " +
                    $"{chunk.Count:N0} candles in {sw.ElapsedMilliseconds} ms{averagesText} ({job.Reason})";
                Post(() => _log(message));
                return;
            }
            if (_cts.IsCancellationRequested) return;
        }
        throw new InvalidOperationException("series changed repeatedly during load");
    }

    private void CommitSpan(SeriesState st, int yearLo, int yearHi)
    {
        lock (_sync)
        {
            if (st.HasLoaded)
            {
                st.LoadedLo = Math.Min(st.LoadedLo, yearLo);
                st.LoadedHi = Math.Max(st.LoadedHi, yearHi);
            }
            else
            {
                st.HasLoaded = true;
                st.LoadedLo = yearLo;
                st.LoadedHi = yearHi;
            }
        }
    }

    private void SetProgress(Job job, string text)
    {
        job.Progress = text;
        JobsChanged?.Invoke();
    }

    private void Post(Action action)
    {
        try
        {
            _dispatcher.BeginInvoke(action);
        }
        catch
        {
        }
    }

    internal static List<Candle> ReadYears(CandleDatabase db, string symbol, int yearLo, int yearHi) =>
        db.ReadRange(symbol, YearStart(yearLo), YearEnd(yearHi), includeWide: true);

    internal static (List<Candle> Minutes, List<Candle> Hidden) SplitHidden(List<Candle> candles)
    {
        if (!WideSpreadRule.Hide) return (candles, new List<Candle>());
        var minutes = new List<Candle>(candles.Count);
        var hidden = new List<Candle>();
        foreach (var c in candles)
        {
            if (c.WideSpread) hidden.Add(c);
            else minutes.Add(c);
        }
        return (minutes, hidden);
    }

    internal static long ChartToSource(long chartUnix, long shiftDelta)
    {
        var w = WeekendCompressor.Instance;
        return w.ToReal(w.ToVirtual(chartUnix) - shiftDelta);
    }

    internal static int YearOfUnix(long unixSeconds)
    {
        long clamped = Math.Clamp(unixSeconds, -62135596800, 253402300799);
        return DateTimeOffset.FromUnixTimeSeconds(clamped).UtcDateTime.Year;
    }

    internal static string YearSpanText(int yearLo, int yearHi) =>
        yearLo == yearHi ? yearLo.ToString() : $"{yearLo}-{yearHi}";

    private static DateTime YearStart(int year) => new(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime YearEnd(int year) => new(year, 12, 31, 23, 59, 0, DateTimeKind.Utc);

}
