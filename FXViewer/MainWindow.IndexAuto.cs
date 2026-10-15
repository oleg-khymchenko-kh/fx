using System.Diagnostics;
using System.Windows.Threading;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow
{
    private static readonly TimeSpan IndexRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IndexRebuildInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan IndexRebuildRetryDelay = TimeSpan.FromMinutes(5);
    private const int IndexLongTailMinutes = 90;
    private const int IndexInPlaceWriteLimit = 20000;
    private const string IndexTempSuffix = "AUTOREBUILD";

    private DispatcherTimer? _indexAutoTimer;
    private Task? _indexAutoTask;
    private CancellationTokenSource? _indexAutoCts;
    private DateTime _indexRebuildDueUtc = DateTime.MinValue;
    private DateTime _indexRebuiltUtc = DateTime.MinValue;

    private sealed record IndexAutoJob(IndicatorSymbol Index, IndicatorSymbol[] Currencies);

    private void InitIndexAuto()
    {
        _indexAutoCts = new CancellationTokenSource();
        _indexAutoTimer = new DispatcherTimer { Interval = IndexRefreshInterval };
        _indexAutoTimer.Tick += (_, _) => OnIndexAutoTick();
    }

    private void StartIndexAuto() => _indexAutoTimer?.Start();

    private void RequestIndexRebuild() => _indexRebuildDueUtc = DateTime.MinValue;

    private static string IndexTempName(string name) => CandleDatabase.Sanitize(name) + IndexTempSuffix;

    private void StopIndexAuto()
    {
        _indexAutoTimer?.Stop();
        _indexAutoCts?.Cancel();
    }

    private void OnIndexAutoTick()
    {
        if (_indexAutoTask is { IsCompleted: false }) return;
        if (_dbBusy || _historyCts != null || _chartLoadTask is { IsCompleted: false }) return;
        if (_indexAutoCts is not { IsCancellationRequested: false } cts) return;
        var jobs = IndexAutoJobs();
        if (jobs.Count == 0) return;
        _indexAutoTask = RunIndexAutoAsync(jobs, DateTime.UtcNow >= _indexRebuildDueUtc, cts.Token);
    }

    private List<IndexAutoJob> IndexAutoJobs() =>
        _config.Indicators
            .Where(x => IndicatorTypes.IsIndex(x.Type))
            .Select(index => new IndexAutoJob(index.Clone(), _config.Indicators
                .Where(x => IndicatorTypes.IsCurrency(x.Type) && SymbolNameEquals(x.Source, index.Name))
                .Select(x => x.Clone())
                .ToArray()))
            .ToList();

    private async Task RunIndexAutoAsync(List<IndexAutoJob> jobs, bool rebuild, CancellationToken ct)
    {
        try
        {
            if (rebuild && await RebuildIndexesAsync(jobs, ct))
            {
                _indexRebuiltUtc = DateTime.UtcNow;
                _indexRebuildDueUtc = _indexRebuiltUtc + IndexRebuildInterval;
            }
            if (ct.IsCancellationRequested) return;
            await PushIndexTailsAsync(jobs);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException || ct.IsCancellationRequested) return;
            if (rebuild) _indexRebuildDueUtc = DateTime.UtcNow + IndexRebuildRetryDelay;
            AppendLog("USD index auto update failed: " + ex.Message);
        }
    }

    private async Task PushIndexTailsAsync(List<IndexAutoJob> jobs)
    {
        var db = GetDb();
        var live = LivePairMinutes(jobs.SelectMany(j =>
            DollarIndexSymbol.Effective(j.Index.IndexPairs).Concat(j.Currencies.Select(c => c.IndexPair))));
        var tails = await Task.Run(() =>
        {
            var result = new List<(string Name, bool Mirror, List<Candle> Tail)>();
            foreach (var job in jobs)
            {
                var index = job.Index;
                var indexTail = DollarIndexSymbol.Tail(db, index.Name, index.IndexPairs, index.EndTimeUnix,
                    index.IndexMethod, index.IndexAlgorithm, SourcePipPoints, live);
                result.Add((index.Name, index.Flip, indexTail));
                foreach (var c in job.Currencies)
                    result.Add((c.Name, index.Flip, CurrencyIndexSymbol.Tail(db, c.Name, index.Name,
                        c.IndexPair, index.IndexAlgorithm, SourcePipPoints(c.IndexPair), indexTail,
                        live.GetValueOrDefault(c.IndexPair))));
            }
            return result;
        });
        foreach (var (name, mirror, tail) in tails) ShowIndexTail(name, mirror, tail);
        if (tails.Any(t => t.Tail.Count > IndexLongTailMinutes)
            && DateTime.UtcNow - _indexRebuiltUtc >= IndexRebuildRetryDelay)
            RequestIndexRebuild();
    }

    private Dictionary<string, Candle[]> LivePairMinutes(IEnumerable<string> pairs)
    {
        var result = new Dictionary<string, Candle[]>(SymbolNameComparer);
        foreach (var pair in pairs)
        {
            if (result.ContainsKey(pair) || !_live.TryGetValue(pair, out var s)) continue;
            var minutes = new List<Candle>(s.Closed.Count + 1);
            foreach (var c in s.Closed)
                if (!WideSpreadRule.Hidden(pair, c.MinuteUnixSeconds, c.HasSpread, c.SpreadCode))
                    minutes.Add(c);
            if (s.MinuteUnix != long.MinValue)
            {
                bool hasSpread = s.MaxSpreadTenths >= 0;
                int code = hasSpread ? SpreadCodes.FromTenths(s.MaxSpreadTenths) : 0;
                if (!WideSpreadRule.Hidden(pair, s.MinuteUnix, hasSpread, code))
                    minutes.Add(new Candle(s.MinuteUnix, s.Low, s.High, LiveAvg(s), true, hasSpread, code));
            }
            result[pair] = minutes.ToArray();
        }
        return result;
    }

    private void ShowIndexTail(string name, bool mirror, List<Candle> tail)
    {
        var series = Chart.GetSeries(name);
        if (series?.Transform is not { } transform || transform.Mirror != mirror) return;
        var minutes = series.History.Minutes;
        long after = minutes.Length > 0 ? minutes[^1].MinuteUnixSeconds : long.MinValue;
        var display = new List<Candle>(tail.Count);
        foreach (var c in tail)
        {
            if (c.MinuteUnixSeconds <= after) continue;
            int v = transform.ToDisplay(c.Avg);
            display.Add(new Candle(c.MinuteUnixSeconds, v, v, v, false));
        }
        if (!series.History.Live.SequenceEqual(display)) Chart.SetLiveTail(name, display.ToArray());
        if (tail.Count > 0) SymbolBar.SetLastPrice(name, tail[^1].Avg);
    }

    private async Task<bool> RebuildIndexesAsync(List<IndexAutoJob> jobs, CancellationToken ct)
    {
        var db = GetDb();
        var sw = Stopwatch.StartNew();
        var ends = jobs
            .Select(j => (
                Index: ConfirmedEndUnix(DollarIndexSymbol.Effective(j.Index.IndexPairs), j.Index.EndTimeUnix),
                Currencies: j.Currencies.Select(c => ConfirmedEndUnix(new[] { c.IndexPair }, 0)).ToArray()))
            .ToArray();
        var computed = await Task.Run(() =>
        {
            var result = new List<(Candle[] Values, FlatSeriesSync.Plan Plan, bool Swap)>();
            void Add(string name, Candle[] values)
            {
                var plan = FlatSeriesSync.Diff(db, name, values);
                bool swap = values.Length > 0
                    && plan.Writes.Length + plan.Clears.Length > IndexInPlaceWriteLimit;
                if (swap) FlatSeriesSync.Replace(db, IndexTempName(name), values);
                result.Add((values, plan, swap));
            }
            for (int i = 0; i < jobs.Count; i++)
            {
                var index = jobs[i].Index;
                var indexValues = DollarIndexSymbol.Compute(db, index.IndexPairs, index.StartTimeUnix,
                    ends[i].Index, index.IndexMethod, index.IndexAlgorithm, SourcePipPoints, ct);
                Add(index.Name, indexValues);
                for (int k = 0; k < jobs[i].Currencies.Length; k++)
                {
                    var c = jobs[i].Currencies[k];
                    Add(c.Name, CurrencyIndexSymbol.Compute(db, indexValues, c.IndexPair,
                        index.IndexAlgorithm, SourcePipPoints(c.IndexPair), ends[i].Currencies[k], ct));
                }
            }
            return result;
        }, ct);
        long computeMs = sw.ElapsedMilliseconds;

        bool Blocked() => _dbBusy || _historyCts != null || _chartLoadTask is { IsCompleted: false }
            || ct.IsCancellationRequested || !jobs.All(SameIndexJob);
        var temps = computed.Where(x => x.Swap).Select(x => IndexTempName(x.Plan.Symbol)).ToArray();
        Task DropTemps() => Task.Run(() =>
        {
            foreach (var temp in temps) db.DeleteSymbol(temp);
        });
        if (Blocked())
        {
            await DropTemps();
            return false;
        }
        var names = computed.Select(x => x.Plan.Symbol).ToArray();
        var loader = _loader;
        if (loader != null)
            foreach (var name in names) await loader.PauseAsync(name);
        try
        {
            if (Blocked())
            {
                await DropTemps();
                return false;
            }
            List<FlatSeriesSync.Plan> applied;
            var swWrite = Stopwatch.StartNew();
            _dbBusy = true;
            try
            {
                applied = await Task.Run(() => computed
                    .Select(x =>
                    {
                        if (x.Swap)
                        {
                            db.RenameSymbol(IndexTempName(x.Plan.Symbol), x.Plan.Symbol);
                            return x.Plan;
                        }
                        var plan = FlatSeriesSync.StillMatches(db, x.Plan)
                            ? x.Plan
                            : FlatSeriesSync.Diff(db, x.Plan.Symbol, x.Values);
                        FlatSeriesSync.Write(db, plan);
                        return plan;
                    })
                    .ToList());
            }
            finally
            {
                _dbBusy = false;
            }
            long writeMs = swWrite.ElapsedMilliseconds;
            await ReloadIndexSeriesAsync(applied.Where(p => !p.Empty).Select(p => p.Symbol).ToArray());
            AppendLog($"USD index auto rebuild: {string.Join(", ", names)} - " +
                $"{computed.Sum(x => x.Values.Length):N0} minutes computed in {computeMs} ms, " +
                $"{applied.Sum(p => p.Writes.Length):N0} written, {applied.Sum(p => p.Clears.Length):N0} " +
                $"cleared ({temps.Length} replaced whole) in {writeMs} ms");
        }
        finally
        {
            if (loader != null)
                foreach (var name in names) loader.Resume(name);
        }
        return true;
    }

    private bool SameIndexJob(IndexAutoJob job)
    {
        var index = _config.Indicators.FirstOrDefault(x =>
            IndicatorTypes.IsIndex(x.Type) && SymbolNameEquals(x.Name, job.Index.Name));
        if (index == null || !index.SameData(job.Index)) return false;
        var currencies = _config.Indicators
            .Where(x => IndicatorTypes.IsCurrency(x.Type) && SymbolNameEquals(x.Source, index.Name))
            .ToList();
        return currencies.Count == job.Currencies.Length
            && job.Currencies.All(c => currencies.Any(x => SymbolNameEquals(x.Name, c.Name) && x.SameData(c)));
    }

    private async Task ReloadIndexSeriesAsync(IReadOnlyList<string> names)
    {
        var db = GetDb();
        foreach (var name in names)
        {
            var series = Chart.GetSeries(name);
            if (series?.Transform is not { } transform || series.History.Minutes.Length == 0) continue;
            var old = series.History;
            int fromYear = SeriesDataLoader.YearOfUnix(old.Minutes[0].MinuteUnixSeconds);
            int toYear = Math.Max(SeriesDataLoader.YearOfUnix(old.Minutes[^1].MinuteUnixSeconds),
                DateTime.UtcNow.Year);
            var averageJobs = Chart.AverageJobsOf(name);
            var (history, averages, lastRaw) = await Task.Run(() =>
            {
                var raw = SeriesDataLoader.ReadYears(db, name, fromYear, toYear);
                var built = CandleHistory.Build(CandleTransforms.TransformWith(
                    raw, transform.PipPoints, transform.Mirror, transform.MirrorBase));
                return (built, AverageSeries.Rebuild(averageJobs, built.AverageMinutes),
                    raw.Count > 0 ? raw[^1].Avg : 0);
            });
            if (history.Minutes.Length == 0) continue;
            if (!ReferenceEquals(Chart.GetSeries(name)?.History, old)) continue;
            Chart.ReplaceSeries(name, history, null, averages);
            SymbolBar.SetLastPrice(name, lastRaw);
        }
    }
}
