using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FXViewer.Calendar;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.OrderBook;
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow : Window, INotesHost
{
    private static readonly (string Symbol, int ColorArgb, bool Mirror, int PipPoints, int PriceDiv)[] SymbolConfigs =
    {
        ("EURUSD", unchecked((int)0xFF3366DD), false, 10, 1),
        ("GBPUSD", unchecked((int)0xFFD32F2F), false, 10, 1),
        ("EURGBP", unchecked((int)0xFF0097A7), false, 10, 1),
        ("USDCHF", unchecked((int)0xFF7B1FA2), true, 10, 1),
        ("USDJPY", unchecked((int)0xFFB8860B), true, 1000, 1),
        ("AUDUSD", unchecked((int)0xFF1B5E20), false, 10, 1),
        ("NZDUSD", unchecked((int)0xFF66BB6A), false, 10, 1),
        ("USDCAD", unchecked((int)0xFF795548), true, 10, 1),
        ("GER40", unchecked((int)0xFF37474F), false, 100, 1000),
    };

    private static readonly Dictionary<string, string[]> BrokerAliases = new()
    {
        ["GER40"] = new[] { "GERMANY40", "DE40", "DAX40" },
    };

    private static readonly Dictionary<string, string> AskSources = new();

    private static bool IsAskSymbol(string symbol) => AskSources.ContainsKey(symbol);

    private static readonly (string Symbol, int ColorArgb, int LimitPips)[] DerivedSymbolConfigs =
    {
        ("EURUSD30", unchecked((int)0xFFFF8C00), 30),
        ("EURUSD40", unchecked((int)0xFFE91E63), 40),
        ("EURUSD50", unchecked((int)0xFF00897B), 50),
    };

    private sealed record DisplayConfig(
        string Symbol, int ColorArgb, bool Mirror, int PipPoints, bool Editable, string? Source,
        bool IsDrawing, bool IsShift = false, long ShiftVirtualDelta = 0)
    {
        public bool IsEntryPanel { get; init; }
        public bool IsAgePanel { get; init; }
        public bool AgeMirror { get; init; }
        public bool IsDeals { get; init; }
        public bool IsDensity { get; init; }
        public bool IsSpread { get; init; }
        public bool IsVolume { get; init; }
        public bool IsOrderBook { get; init; }
        public bool IsOrderBookPositions { get; init; }
        public bool IsMarketDepth { get; init; }
        public bool IsAverage { get; init; }
        public bool IsLevels { get; init; }
        public AverageSpec? Average { get; init; }
        public string? ShiftReadSymbol { get; init; }
        public int PriceDiv { get; init; } = 1;
        public bool IsBasePair { get; init; }
        public bool HasRangeStats { get; init; }
    }

    private sealed record SeriesSlot(
        SymbolSeries Series, long MirrorBase, int PipPoints, int LastAvg, bool IsBase, long LastUnix,
        string ReadSymbol, bool Mirror, bool IsShift, long ShiftDelta,
        int MinYear, int MaxYear, int? LoadedLo, int? LoadedHi);

    private const int IndexPipPoints = 10;
    private const string RebuildJobKey = "rebuild:";
    private const int ShiftNudgeMinutes = 15;
    private const int ShiftNudgeFineMinutes = 1;
    private const int AverageWheelMinutes = 240;
    private const int AverageWheelFineMinutes = 15;
    private const int MaxAverageWindowMinutes = 365 * 1440;

    private static DisplayConfig IndicatorConfig(
        IndicatorSymbol ind, string sourceSymbol, bool sourceMirror, int sourcePipPoints,
        int sourcePriceDiv)
    {
        bool isShift = IndicatorTypes.IsShift(ind.Type);
        bool isEntry = IndicatorTypes.IsEntryPoints(ind.Type);
        bool isAge = IndicatorTypes.IsPriceAge(ind.Type);
        bool isDensity = IndicatorTypes.IsDensity(ind.Type);
        bool isSpread = IndicatorTypes.IsSpread(ind.Type);
        bool isVolume = IndicatorTypes.IsVolume(ind.Type);
        bool isOrderBook = IndicatorTypes.IsOrderBook(ind.Type);
        bool isLevels = IndicatorTypes.IsLevels(ind.Type);
        bool isPanel = isEntry || isAge || isDensity || isSpread || isVolume || isOrderBook
            || isLevels;
        string target = isShift ? ind.ShiftTarget() : "";
        var pair = isShift ? PairDisplay(target) : null;
        bool targetMirror = pair?.Mirror ?? sourceMirror;
        int targetPipPoints = pair?.PipPoints ?? sourcePipPoints;
        int targetPriceDiv = pair?.PriceDiv ?? sourcePriceDiv;
        return new DisplayConfig(ind.Name, ind.ColorArgb,
            isPanel ? false : isShift ? targetMirror ^ ind.Flip : sourceMirror,
            isPanel ? IndexPipPoints : isShift ? targetPipPoints : sourcePipPoints,
            IndicatorTypes.IsZigZag(ind.Type), sourceSymbol, IndicatorTypes.IsDrawing(ind.Type),
            isShift, ShiftedSymbol.VirtualDelta(ind.SourceTimeUnix, ind.ChartTimeUnix))
        {
            IsEntryPanel = isEntry,
            IsAgePanel = isAge,
            AgeMirror = isAge && sourceMirror,
            IsDeals = IndicatorTypes.IsDeals(ind.Type),
            IsDensity = isDensity,
            IsSpread = isSpread,
            IsVolume = isVolume,
            IsOrderBook = isOrderBook,
            IsOrderBookPositions = IndicatorTypes.IsOpenPositions(ind.Type),
            IsMarketDepth = IndicatorTypes.IsMarketDepth(ind.Type),
            IsAverage = IndicatorTypes.IsAverage(ind.Type),
            IsLevels = isLevels,
            Average = IndicatorTypes.IsAverage(ind.Type) ? AverageSpecOf(ind) : null,
            ShiftReadSymbol = isShift ? target : null,
            PriceDiv = isPanel ? 1 : isShift ? targetPriceDiv : sourcePriceDiv,
            HasRangeStats = IndicatorTypes.HasRangeStats(ind.Type),
        };
    }

    private static AverageSpec AverageSpecOf(IndicatorSymbol ind)
    {
        bool band = IndicatorTypes.IsAverageBand(ind.Type);
        return new AverageSpec(
            MovingAverageSymbol.WindowBars(ind.Period, ind.Unit),
            ind.FromFuture,
            !band && ind.AverageWeighted,
            band ? Math.Max(1, ind.BandCount) : 1,
            BandPickOf(ind.BandMode),
            ind.AverageTimeWindow);
    }

    private static BandPick BandPickOf(string mode) =>
        BandModes.Same(mode, BandModes.Min) ? BandPick.Min
        : BandModes.Same(mode, BandModes.Avg) ? BandPick.Avg
        : BandPick.Max;

    private static (bool Mirror, int PipPoints, int PriceDiv)? PairDisplay(string symbol)
    {
        foreach (var c in SymbolConfigs)
            if (SymbolNameEquals(c.Symbol, symbol)) return (c.Mirror, c.PipPoints, c.PriceDiv);
        return null;
    }

    private int PairColorOf(string symbol, int defaultColor) =>
        _config.PairColors.TryGetValue(symbol, out var color) ? color : defaultColor;

    private IEnumerable<DisplayConfig> DisplayConfigs(IReadOnlyList<IndicatorSymbol> indicators)
    {
        var emitted = new HashSet<string>();
        IEnumerable<DisplayConfig> LevelsOf(string parent)
        {
            foreach (var ind in indicators)
            {
                if (!IndicatorTypes.IsLevels(ind.Type)) continue;
                if (!SymbolNameEquals(ind.Source, parent)) continue;
                emitted.Add(IndicatorSymbol.NameKey(ind.Name));
                yield return IndicatorConfig(ind, parent, false, IndexPipPoints, 1);
            }
        }
        foreach (var c in SymbolConfigs)
        {
            yield return new DisplayConfig(c.Symbol, PairColorOf(c.Symbol, c.ColorArgb), c.Mirror,
                c.PipPoints, false, null, false)
            {
                PriceDiv = c.PriceDiv,
                IsBasePair = true,
                HasRangeStats = true,
            };
            foreach (var ind in indicators)
            {
                if (IndicatorTypes.IsIndex(ind.Type)) continue;
                if (!SymbolNameEquals(ind.Source, c.Symbol)) continue;
                emitted.Add(IndicatorSymbol.NameKey(ind.Name));
                yield return IndicatorConfig(ind, c.Symbol, c.Mirror, c.PipPoints, c.PriceDiv);
                foreach (var child in LevelsOf(ind.Name)) yield return child;
            }
        }
        foreach (var index in indicators)
        {
            if (!IndicatorTypes.IsIndex(index.Type)) continue;
            emitted.Add(IndicatorSymbol.NameKey(index.Name));
            yield return new DisplayConfig(
                index.Name, index.ColorArgb, index.Flip, IndexPipPoints, false, null, false)
            {
                HasRangeStats = true,
            };
            foreach (var ind in indicators)
            {
                if (IndicatorTypes.IsIndex(ind.Type)) continue;
                if (!SymbolNameEquals(ind.Source, index.Name)) continue;
                emitted.Add(IndicatorSymbol.NameKey(ind.Name));
                yield return IndicatorConfig(ind, index.Name, index.Flip, IndexPipPoints, 1);
                foreach (var child in LevelsOf(ind.Name)) yield return child;
            }
        }
        foreach (var ind in indicators)
        {
            if (emitted.Contains(IndicatorSymbol.NameKey(ind.Name))) continue;
            yield return new DisplayConfig(
                ind.Name, ind.ColorArgb, false, IndexPipPoints, false, null,
                IndicatorTypes.IsDrawing(ind.Type))
            {
                IsEntryPanel = IndicatorTypes.IsEntryPoints(ind.Type),
                IsAgePanel = IndicatorTypes.IsPriceAge(ind.Type),
                IsDeals = IndicatorTypes.IsDeals(ind.Type),
                IsDensity = IndicatorTypes.IsDensity(ind.Type),
                IsSpread = IndicatorTypes.IsSpread(ind.Type),
                IsVolume = IndicatorTypes.IsVolume(ind.Type),
                IsOrderBook = IndicatorTypes.IsOrderBook(ind.Type),
                IsOrderBookPositions = IndicatorTypes.IsOpenPositions(ind.Type),
                IsMarketDepth = IndicatorTypes.IsMarketDepth(ind.Type),
                IsLevels = IndicatorTypes.IsLevels(ind.Type),
                HasRangeStats = IndicatorTypes.HasRangeStats(ind.Type),
            };
        }
    }

    private static bool IsStandaloneIndicator(IndicatorSymbol ind) => IndicatorTypes.IsIndex(ind.Type);

    private static bool SymbolNameEquals(string a, string b) =>
        IndicatorSymbol.NameKey(a) == IndicatorSymbol.NameKey(b);

    private sealed class NameKeyComparer : IEqualityComparer<string>
    {
        public bool Equals(string? a, string? b) =>
            IndicatorSymbol.NameKey(a ?? "") == IndicatorSymbol.NameKey(b ?? "");

        public int GetHashCode(string s) => IndicatorSymbol.NameKey(s).GetHashCode();
    }

    private static readonly NameKeyComparer SymbolNameComparer = new();

    private static int SourcePipPoints(string source)
    {
        foreach (var c in SymbolConfigs)
            if (SymbolNameEquals(c.Symbol, source)) return c.PipPoints;
        return 10;
    }

    private static int SymbolPriceDiv(string symbol)
    {
        foreach (var c in SymbolConfigs)
            if (SymbolNameEquals(c.Symbol, symbol)) return c.PriceDiv;
        return 1;
    }

    private string IndexAlgorithmOf(string indexName)
    {
        var index = _config.Indicators.FirstOrDefault(x =>
            IndicatorTypes.IsIndex(x.Type) && SymbolNameEquals(x.Name, indexName));
        return index?.IndexAlgorithm ?? IndexAlgorithms.Percent;
    }

    private readonly Stopwatch _bootSw = Stopwatch.StartNew();
    private readonly AppConfig _config = AppConfig.Load();
    private CTraderClient? _client;
    private long _accountId;
    private readonly Dictionary<string, long> _symbolIds = new();
    private ulong? _lastBid;
    private ulong? _lastAsk;
    private bool _connectInProgress;
    private CancellationTokenSource? _authCts;
    private Task<string>? _authCodeTask;
    private CancellationTokenSource? _historyCts;
    private CandleDatabase? _db;
    private CalendarStore? _calendar;
    private CalendarEntry[] _calendarEntries = Array.Empty<CalendarEntry>();
    private CancellationTokenSource? _calendarCts;
    private bool _calendarBackfillRunning;
    private const string CalendarCenterSymbol = "EURUSD";
    private const long MinCalendarGapSeconds = 10 * 86400L;
    private List<CalendarSummary>? _calendarSummaries;
    private bool _calendarSummariesLoading;
    private CalendarFindWindow? _calendarFindWindow;
    private bool _dbBusy;
    private readonly DispatcherTimer _stateSaveTimer;
    private readonly DispatcherTimer _perfTimer;
    private ChartViewState? _pendingChartState;
    private (string Symbol, long MirrorBase, int PipPoints)[] _seriesTransforms =
        Array.Empty<(string, long, int)>();
    private HashSet<string> _spreadSeriesKeys = new(StringComparer.Ordinal);
    private HashSet<string> _volumeSeriesKeys = new(StringComparer.Ordinal);
    private SeriesDataLoader? _loader;
    private (long Lo, long Hi)? _lastViewRealRange;
    private readonly ConcurrentDictionary<string, string> _startupJobs = new();

    private enum ConnState { Offline, Connecting, Downloading, Online }

    private sealed class LiveState
    {
        public long BaseLastUnix = long.MinValue;
        public long MirrorBase;
        public int PipPoints = 10;
        public readonly List<Candle> Closed = new();
        public long MinuteUnix = long.MinValue;
        public int Open;
        public int High;
        public int Low;
        public int Close;
        public int MaxSpreadTenths = -1;
        public bool Dirty;
    }

    private readonly DispatcherTimer _liveFlushTimer;
    private readonly DispatcherTimer _liveRepairTimer;
    private readonly DispatcherTimer _orderBookTimer;
    private readonly DispatcherTimer _volumeTimer;
    private CancellationTokenSource? _repairCts;
    private Task? _repairTask;
    private Task? _orderBookTask;
    private Task? _volumeTask;
    private OrderBookCollector? _orderBookCollector;
    private VolumeCollector? _volumeCollector;
    private DepthCollector? _depthCollector;
    private Task<IReadOnlyList<DepthCollector.Write>>? _depthTask;
    private DispatcherTimer? _depthTimer;
    private DepthProbe? _depthProbe;
    private LiveDbWriter? _liveDb;
    private readonly DispatcherTimer _averageWindowTimer;
    private readonly Dictionary<string, int> _pendingAverageWindows = new(SymbolNameComparer);
    private readonly Dictionary<string, LiveState> _live = new();
    private readonly Dictionary<long, string> _idToSymbol = new();
    private readonly Dictionary<long, string> _idToAskSymbol = new();
    private readonly Dictionary<long, (long Bid, long Ask)> _lastQuotes = new();
    private Dictionary<string, (long LastUnix, long MirrorBase, int PipPoints)> _baseInfo = new();
    private ConnState _connState = ConnState.Offline;
    private bool _firstSpotLogged;
    private bool _autoReconnect;
    private bool _reconnecting;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "fxviewer.log");

    private static string DbRoot => Path.Combine(AppContext.BaseDirectory, "data");

    private CandleDatabase GetDb() => _db ??= new CandleDatabase(DbRoot);

    private LiveDbWriter GetLiveDb() =>
        _liveDb ??= new LiveDbWriter(GetDb(), () => !_dbBusy && _historyCts == null, AppendLog);

    private CalendarStore GetCalendar() => _calendar ??= new CalendarStore(Path.Combine(DbRoot, "calendar"));

    private OrderBookCollector GetOrderBookCollector() =>
        _orderBookCollector ??= new OrderBookCollector(GetDb().SymbolDirectory,
            msg => Dispatcher.BeginInvoke(() => AppendLog(msg)));

    private void PollOrderBook()
    {
        if (_orderBookTask is { IsCompleted: false }) return;
        _orderBookTask = GetOrderBookCollector().RunOnceAsync(CancellationToken.None);
    }

    private async Task UpgradeOrderBookStoresAsync()
    {
        var db = GetDb();
        try
        {
            await Task.Run(() =>
            {
                foreach (var pair in OrderBookCollector.Pairs)
                    OrderBookStore.UpgradeAll(db.SymbolDirectory(pair),
                        msg => Dispatcher.BeginInvoke(() => AppendLog(msg)));
            });
        }
        catch (Exception ex)
        {
            AppendLog("order book upgrade failed: " + ex.Message);
        }
    }

    private void LogConfigBackups()
    {
        try
        {
            var dir = AppConfig.BackupDir;
            if (!Directory.Exists(dir))
            {
                AppendLog("Config backups: none yet in " + dir);
                return;
            }
            var files = Directory.GetFiles(dir, "config-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            AppendLog(files.Length == 0
                ? "Config backups: none yet in " + dir
                : $"Config backups: {files.Length} in {dir}, newest {Path.GetFileNameWithoutExtension(files[^1])[7..]}");
        }
        catch (Exception ex)
        {
            AppendLog("Config backups: " + ex.Message);
        }
    }

    private VolumeCollector GetVolumeCollector() =>
        _volumeCollector ??= new VolumeCollector(
            _config.SierraDataFolder,
            () => !_dbBusy && _historyCts == null,
            message => Dispatcher.InvokeAsync(() => AppendLog(message)));

    private DepthCollector GetDepthCollector()
    {
        if (_depthCollector != null) return _depthCollector;
        var volume = GetVolumeCollector();
        return _depthCollector = new DepthCollector(
            _config.SierraDataFolder,
            volume.ContractFor,
            symbol => GetDb().SymbolDirectory(symbol),
            () => !_dbBusy && _historyCts == null,
            message => Dispatcher.InvokeAsync(() => AppendLog(message)));
    }

    private void PollDepth()
    {
        if (_depthTask is { IsCompleted: false }) return;
        if (_dbBusy || _historyCts != null) return;
        var collector = GetDepthCollector();
        var now = DateTime.UtcNow;
        _depthTask = Task.Run(() => collector.RunOnce(now)).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppendLog("depth: " + (t.Exception?.GetBaseException().Message ?? "poll failed"));
            else
                foreach (var write in t.Result)
                    Chart.MergeDepthSnapshots(write.Symbol, write.Snapshots);
            return t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void PollVolume()
    {
        if (_volumeTask is { IsCompleted: false }) return;
        if (_dbBusy || _historyCts != null) return;
        var collector = GetVolumeCollector();
        var db = GetDb();
        var now = DateTime.UtcNow;
        _volumeTask = Task.Run(() => collector.RunOnce(db, now)).ContinueWith(t =>
        {
            if (t.IsFaulted)
                AppendLog("volume: " + (t.Exception?.GetBaseException().Message ?? "poll failed"));
            else
                ApplyVolumeWrites(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyVolumeWrites(VolumeCollector.RunResult result)
    {
        foreach (var group in result.Profiles.GroupBy(p => p.Symbol, StringComparer.Ordinal))
            Chart.MergeVolumeProfiles(group.Key, group.Select(p => p.Record).ToList());
        var writes = result.Volumes;
        if (writes.Count == 0) return;
        foreach (var group in writes.GroupBy(w => w.Symbol, StringComparer.Ordinal))
        {
            var byMinute = new Dictionary<long, int>();
            long lo = long.MaxValue;
            long hi = long.MinValue;
            foreach (var w in group)
            {
                byMinute[w.MinuteUnix] = w.Volume;
                if (w.MinuteUnix < lo) lo = w.MinuteUnix;
                if (w.MinuteUnix > hi) hi = w.MinuteUnix;
            }
            PatchLoadedVolume(group.Key, byMinute, lo, hi);
            PatchLiveVolume(group.Key, byMinute);
        }
    }

    private void PatchLoadedVolume(string symbol, Dictionary<long, int> byMinute, long lo, long hi)
    {
        var series = Chart.GetSeries(symbol);
        if (series == null) return;
        var minutes = series.History.Minutes;
        if (minutes.Length == 0) return;
        int from = LowerBoundMinute(minutes, lo);
        int toExcl = LowerBoundMinute(minutes, hi + 1);
        if (from >= toExcl) return;
        var replacement = new List<Candle>(toExcl - from);
        bool changed = false;
        for (int k = from; k < toExcl; k++)
        {
            var c = minutes[k];
            if (byMinute.TryGetValue(c.MinuteUnixSeconds, out var v) && (!c.HasVolume || c.Volume != v))
            {
                replacement.Add(c with { HasVolume = true, Volume = v });
                changed = true;
            }
            else
            {
                replacement.Add(c);
            }
        }
        if (!changed) return;
        var history = series.History.WithReplacedRange(from, toExcl - from, replacement);
        history.SetLive(series.History.Live);
        if (series.History.HasLastTick) history.SetLastTick(series.History.LastTick);
        Chart.PatchSeriesHistory(symbol, history);
    }

    private void PatchLiveVolume(string symbol, Dictionary<long, int> byMinute)
    {
        if (!_live.TryGetValue(symbol, out var s)) return;
        bool changed = false;
        for (int i = 0; i < s.Closed.Count; i++)
        {
            var c = s.Closed[i];
            if (!byMinute.TryGetValue(c.MinuteUnixSeconds, out var v)) continue;
            if (c.HasVolume && c.Volume == v) continue;
            s.Closed[i] = c with { HasVolume = true, Volume = v };
            changed = true;
        }
        if (changed) PushLiveTail(symbol, s);
    }

    private static int LowerBoundMinute(Candle[] minutes, long unixSeconds)
    {
        int lo = 0;
        int hi = minutes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (minutes[mid].MinuteUnixSeconds < unixSeconds) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public MainWindow()
    {
        InitializeComponent();
        WideSpreadRule.Hide = _config.HideWideSpread;
        AskViewRule.SetPairs(SymbolConfigs.Select(c => (c.Symbol, c.PipPoints)));
        WideSpreadRule.SetMeasuredPairs(SymbolConfigs.Select(c => c.Symbol));
        AskViewRule.Show = _config.ShowAsk;
        UpdateAskTitle();
        CrashLog.Reported += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        ClientIdBox.Text = _config.ClientId;
        ClientSecretBox.Text = _config.ClientSecret;
        LiveCheck.IsChecked = _config.IsLive;
        AppendLog("Config folder: " + AppConfig.Dir);
        LogConfigBackups();
        if (!string.IsNullOrEmpty(_config.AccessToken))
            AppendLog("Access token found in config, Authorize can be skipped");
        Chart.Info += AppendLog;
        Chart.ViewChanged += (k, startBucket, widthPx, map) =>
        {
            using (Perf.Step("timeaxis.update")) TimeAxis.Update(k, startBucket, widthPx, map);
            OnViewRangeChanged(k, startBucket, widthPx, map);
        };
        Chart.CursorTimeChanged += (unix, xDip) => TimeAxis.SetCursor(unix, xDip);
        Chart.CursorPricesChanged += (p, d) =>
        {
            using var _ = Perf.Step("symbolbar.cursor");
            SymbolBar.SetCursorPrices(ToTruePrices(p), d);
        };
        Chart.DensitySelectedChanged += option =>
        {
            bool changed = false;
            foreach (var ind in _config.Indicators)
                if ((IndicatorTypes.IsDensity(ind.Type) || IndicatorTypes.IsVolume(ind.Type))
                    && ind.DensitySelected != option)
                {
                    ind.DensitySelected = option;
                    changed = true;
                }
            if (changed) _config.Save();
            if (option >= IndicatorSymbol.DensityAllOption && _loader != null)
                foreach (var ind in _config.Indicators)
                    if (IndicatorTypes.IsDensity(ind.Type) || IndicatorTypes.IsVolume(ind.Type))
                        _ = _loader.EnsureFullAsync(ind.Source, "density all history");
        };
        SymbolBar.PriceOffsetWheel += Chart.ShiftSeriesOffset;
        SymbolBar.VolumeScaleWheel += Chart.ShiftVolumeScale;
        SymbolBar.VolumeGroupWheel += Chart.ShiftVolumeGroup;
        SymbolBar.DensityScaleWheel += Chart.ShiftDensityScale;
        SymbolBar.AveragePeriodWheel += OnAveragePeriodWheel;
        _averageWindowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _averageWindowTimer.Tick += (_, _) =>
        {
            _averageWindowTimer.Stop();
            foreach (var (name, window) in _pendingAverageWindows)
                Chart.SetAverageWindow(name, window);
            _pendingAverageWindows.Clear();
        };
        Chart.DensityScaleChanged += (symbol, perPixel) =>
        {
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
            if (ind == null || Math.Abs(ind.DensityScalePerPixel - perPixel) < 1e-9) return;
            ind.DensityScalePerPixel = perPixel;
            _config.Save();
        };
        Chart.VolumeScaleChanged += (symbol, scale) =>
        {
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
            if (ind == null || Math.Abs(ind.VolumeBarScale - scale) < 1e-9) return;
            ind.VolumeBarScale = scale;
            _config.Save();
        };
        Chart.VolumeUnitChanged += (symbol, unit) =>
        {
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
            if (ind == null || Math.Abs(ind.VolumeBarUnit - unit) < 1e-9) return;
            ind.VolumeBarUnit = unit;
            _config.Save();
        };
        Chart.VolumeGroupChanged += (symbol, minutes) =>
        {
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
            if (ind == null || ind.VolumeGroupMinutes == minutes) return;
            ind.VolumeGroupMinutes = minutes;
            _config.Save();
            SymbolBar.SetVolumeGroup(symbol, minutes);
        };
        SymbolBar.TimeShiftWheel += (symbol, delta, fine) =>
            QueueShiftNudge(symbol, delta, fine ? ShiftNudgeFineMinutes : ShiftNudgeMinutes);
        Chart.SeriesTimeShiftRequested += QueueShiftStep;
        SymbolBar.SymbolClick += symbol =>
        {
            bool enabled = Chart.ToggleSeries(symbol);
            SymbolBar.SetSymbolEnabled(symbol, enabled);
            RefreshGameIfPlaying();
            if (!enabled || _loader == null) return;
            string loadSymbol = symbol;
            var avg = _config.Indicators.FirstOrDefault(x =>
                IndicatorTypes.IsAverage(x.Type) && SymbolNameEquals(x.Name, symbol));
            if (avg != null) loadSymbol = avg.Source;
            if (Chart.IsFitView || _lastViewRealRange == null)
                _ = _loader.EnsureFullAsync(loadSymbol, "toggle");
            else
                _loader.EnsureVisibleRange(
                    _lastViewRealRange.Value.Lo, _lastViewRealRange.Value.Hi, LoaderHidden());
        };
        SymbolBar.CollapseToggled += symbol =>
        {
            Chart.ToggleCollapsed(symbol);
            SymbolBar.SetCollapsedSources(Chart.CollapsedSources);
        };
        SymbolBar.AlignToGrid += Chart.AlignSeriesOffsetToGrid;
        SymbolBar.AutoAlign += Chart.AutoAlignSeries;
        SymbolBar.AlignToSource += Chart.AlignSeriesToSource;
        SymbolBar.AlignIndicatorsToSource += Chart.AlignSeriesToSource;
        SymbolBar.IsAlignedToSource = Chart.IsAlignedToSource;
        Chart.SeriesOffsetsChanged += SymbolBar.RefreshAlignment;
        SymbolBar.AddSymbolRequested += source => OpenSymbolEditor(source, null);
        SymbolBar.EditSymbolRequested += name => OpenSymbolEditor(null, name);
        SymbolBar.DeleteSymbolRequested += DeleteIndicator;
        SymbolBar.CanRefresh = name =>
        {
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
            return ind != null && IndicatorTypes.HasStorage(ind.Type) && !IndicatorTypes.IsZigZag(ind.Type);
        };
        SymbolBar.RefreshSymbolRequested += name => _ = RefreshIndicatorFromMenuAsync(name);
        SymbolBar.CanRebuild = name =>
        {
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
            return ind != null && IndicatorTypes.HasStorage(ind.Type);
        };
        SymbolBar.RebuildSymbolRequested += name => _ = RebuildIndicatorFromMenuAsync(name);
        SymbolBar.DrawRequested += Chart.BeginDrawLine;
        SymbolBar.DrawLevelRequested += Chart.BeginDrawLevel;
        SymbolBar.FindRequested += name => _ = RunFindAsync(name);
        SymbolBar.ShowResultsRequested += ShowFindResults;
        SymbolBar.HasChartSelection = () => Chart.SelectedRange != null && !_findBusy;
        SymbolBar.HasFindResults = name =>
        {
            if (_findBusy) return false;
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
            return ind != null && LoadFindResults(ind) != null;
        };
        ChartTools.AddClick += () => OpenSymbolEditor(null, null);
        ChartTools.SettingsClick += OpenAppSettings;
        ChartTools.CalendarClick += () =>
            ChartTools.SetCalendarRow(Chart.HasCalendar, Chart.ToggleCalendar());
        ChartTools.ForecastClick += () =>
            ChartTools.SetForecastRow(Chart.HasForecasts, Chart.ToggleForecasts());
        ChartTools.ForecastReloadRequested += ReloadForecasts;
        Chart.ForecastDaySelected += () =>
            ChartTools.SetForecastRow(Chart.HasForecasts, Chart.ForecastVisible);
        ChartTools.CalendarSettingsRequested += OpenCalendarSettings;
        ChartTools.CalendarFindRequested += () => _ = OpenCalendarFindAsync();
        ChartTools.WeekendsClick += () => ChartTools.SetWeekendsRow(Chart.ToggleWeekends());
        ChartTools.SessionsClick += () => ChartTools.SetSessionsRow(Chart.ToggleSessions());
        SymbolBar.UnflattenClick += () => Chart.SetFlattenLine(null, -1);
        SymbolBar.TiltedGridSelected += (up, slot) =>
        {
            Chart.ToggleTiltedGrid(up, slot);
            SymbolBar.SetTiltedGridRow(Chart.TiltedUpGridIndex, Chart.TiltedDownGridIndex);
        };
        SymbolBar.TiltedGridSettingsRequested += () =>
        {
            var dlg = new TiltedGridSettingsWindow(Chart.TiltedGrids, Chart.TiltedSettingsGridIndex)
            {
                Owner = this,
            };
            if (dlg.ShowDialog() == true) Chart.SetTiltedGrids(dlg.Grids);
        };
        SymbolBar.TiltedGridResetRequested += Chart.ResetTiltedGridPlacement;
        Chart.CalendarDetailLookup = entry => GetCalendar().ReadDetail(entry);
        Chart.SetCalendarSettings(_config.Calendar);
        Chart.DrawingCommitted += OnDrawingCommitted;
        Chart.DrawingLinesChanged += OnDrawingLinesChanged;
        Chart.EditHitRadiusPx = Math.Max(1, _config.EditHitRadiusPx);
        Chart.SetSeriesOrder(_config.SeriesOrder);
        Chart.SeriesOrderChanged += () =>
        {
            _config.SeriesOrder = new List<string>(Chart.SeriesOrder);
            _config.Save();
        };
        Chart.PivotEditRequested += req => _ = ApplyPivotEditAsync(req);
        Chart.SetZoomLevels(_config.ZoomLevels);
        Chart.ZoomLevelChanged += RefreshZoomLevelState;
        Chart.MeasureLabelBoundsChanged += HideZoomLevelsUnderMeasureLabel;
        ZoomLevels.LevelSelected += Chart.SelectZoomLevel;
        ZoomLevels.SaveRequested += () => CommitZoomLevels(Chart.SaveCurrentZoomToLevel());
        ZoomLevels.RevertRequested += Chart.RevertToZoomLevel;
        ZoomLevels.InsertRequested += index => CommitZoomLevels(Chart.InsertZoomLevel(index));
        ZoomLevels.DeleteRequested += index => CommitZoomLevels(Chart.DeleteZoomLevel(index));
        ZoomLevels.LevelEdited += (index, perDay, per100Pips) =>
        {
            if (Chart.SetZoomLevelValues(index, perDay, per100Pips)) _config.Save();
        };
        _tabProperties.CustomZoomChanged += ApplyTabCustomZoom;
        InitGame();
        InitComments();
        RefreshZoomLevels();
        SeedIndicators();
        _stateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stateSaveTimer.Tick += (_, _) =>
        {
            _stateSaveTimer.Stop();
            SaveChartState();
        };
        Chart.StateChanged += s =>
        {
            SymbolBar.SetFlattenRow(s.FlattenSymbol != null);
            _pendingChartState = s;
            _stateSaveTimer.Stop();
            _stateSaveTimer.Start();
        };
        Perf.Line += AppendLog;
        _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _perfTimer.Tick += (_, _) =>
        {
            FlushRepeatedLog();
            var summary = Perf.Flush();
            if (summary != null) AppendLog(summary);
        };
        _perfTimer.Start();
        _liveFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _liveFlushTimer.Tick += (_, _) => FlushLive();
        _liveRepairTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _liveRepairTimer.Tick += (_, _) =>
        {
            if (_repairTask is { IsCompleted: false }) return;
            _repairTask = RepairProvisionalAsync();
        };
        _orderBookTimer = new DispatcherTimer { Interval = OrderBookCollector.PollInterval };
        _orderBookTimer.Tick += (_, _) => PollOrderBook();
        _depthTimer = new DispatcherTimer { Interval = DepthCollector.PollInterval };
        _depthTimer.Tick += (_, _) => PollDepth();
        _depthTimer.Start();
        _volumeTimer = new DispatcherTimer { Interval = VolumeCollector.PollInterval };
        _volumeTimer.Tick += (_, _) => PollVolume();
        BuildTabs();
        Loaded += async (_, _) => await StartupAsync();
    }

    private async Task StartupAsync()
    {
        AppendLog($"=== Startup === (window ready in {_bootSw.ElapsedMilliseconds} ms since ctor)");
        SetConnState(ConnState.Offline);
        await UpgradeOrderBookStoresAsync();
        await LoadChartAsync();
        ReloadForecasts();
        await LoadCalendarEntriesAsync();
        _ = AutoRefreshCalendarAsync();
        PollOrderBook();
        _orderBookTimer.Start();
        PollVolume();
        _volumeTimer.Start();
        if (string.IsNullOrEmpty(_config.AccessToken))
        {
            AppendLog("No access token - open the Connection tab and Authorize to enable live updates");
            return;
        }
        _autoReconnect = true;
        AppendLog("Auto-connecting...");
        if (await ConnectAndStreamAsync())
            AppendLog($"Startup done. EURUSD base last minute (UTC): {BaseLastUtcText()}");
        else if (_autoReconnect)
            StartReconnectLoop();
    }

    private async Task<bool> ConnectAndStreamAsync()
    {
        if (!await ConnectCoreAsync()) return false;
        await PrimeLiveMarksAsync();
        await RepairProvisionalAsync();
        await DownloadHistoryAsync(recentOnly: true);
        await SubscribeAllAsync();
        await MergeRecentTailAsync();
        return _connState == ConnState.Online;
    }

    private void StartReconnectLoop()
    {
        if (!_reconnecting) _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            while (_autoReconnect)
            {
                AppendLog("Reconnecting...");
                SetConnState(ConnState.Connecting);
                if (await ConnectAndStreamAsync()) return;
                if (!_autoReconnect) return;
                SetConnState(ConnState.Connecting);
                await Task.Delay(ReconnectDelay);
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private string BaseLastUtcText() => _baseInfo.TryGetValue("EURUSD", out var bi)
        ? DateTimeOffset.FromUnixTimeSeconds(bi.LastUnix).UtcDateTime.ToString("yyyy-MM-dd HH:mm")
        : "none";

    private CancellationTokenSource? _chartCts;
    private Task? _chartLoadTask;
    private CancellationTokenSource? _computeCts;

    private Task LoadChartAsync()
    {
        var task = LoadChartCoreAsync();
        _chartLoadTask = task;
        return task;
    }

    private async Task LoadChartCoreAsync()
    {
        _stateSaveTimer.Stop();
        SaveChartState();
        _chartCts?.Cancel();
        var cts = new CancellationTokenSource();
        _chartCts = cts;
        var token = cts.Token;
        _loader?.Dispose();
        _loader = null;
        _startupJobs.Clear();
        RefreshLoadIndicator();
        var swTotal = Stopwatch.StartNew();
        try
        {
            var db = GetDb();
            var indicators = _config.Indicators.ToArray();
            DropLegacyZigZagCandles(db, indicators);
            DropStoredAverageCandles(db, indicators);
            var indicatorNames = new HashSet<string>(indicators.Select(i => IndicatorSymbol.NameKey(i.Name)));
            var hiddenSymbols = new HashSet<string>(ActiveState?.HiddenSymbols ?? new List<string>());
            var averageSourceNames = new HashSet<string>(indicators
                .Where(x => IndicatorTypes.IsAverage(x.Type) && !hiddenSymbols.Contains(x.Name))
                .Select(x => x.Source), SymbolNameComparer);
            var viewRange = StartupRealRange();
            var knownBases = new Dictionary<string, long>(
                _config.MirrorBases ?? new Dictionary<string, long>());
            var newBases = new ConcurrentDictionary<string, long>();
            AppendLog(viewRange == null
                ? "Chart load: no saved view - reading full history of enabled symbols..."
                : "Chart load: reading only the visible range, the rest loads on demand...");
            var swBg = Stopwatch.StartNew();
            var activeNote = ActiveNote();
            var configs = DisplayConfigs(indicators).ToArray();
            var slots = new SeriesSlot?[configs.Length];
            await Task.Run(() =>
            {
                var options = new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                };
                Parallel.For(0, configs.Length, options, i =>
                {
                    var (symbol, color, mirror, pipPoints, editable, source, isDrawing, isShift, shiftDelta) =
                        configs[i];
                    if (isDrawing || editable || configs[i].IsDeals || configs[i].IsDensity
                        || configs[i].IsSpread || configs[i].IsVolume
                        || configs[i].IsOrderBook || configs[i].IsAverage
                        || configs[i].IsLevels) return;
                    bool entryPanel = configs[i].IsEntryPanel;
                    bool agePanel = configs[i].IsAgePanel;
                    var swSym = Stopwatch.StartNew();
                    var readSymbol = isShift ? configs[i].ShiftReadSymbol ?? source! : symbol;
                    var years = db.ExistingYears(readSymbol);
                    if (years.Count == 0)
                    {
                        if (!indicatorNames.Contains(IndicatorSymbol.NameKey(symbol))) return;
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, new SeriesTransform(false, 0, pipPoints), source,
                                null, entryPanel, null, agePanel)
                            {
                                PriceMul = configs[i].PriceDiv,
                                BasePair = configs[i].IsBasePair,
                                RangeStatsRow = configs[i].HasRangeStats,
                                TimeShift = isShift,
                                AgeMirror = configs[i].AgeMirror,
                            },
                            0, pipPoints, 0, false, 0, "", mirror, false, 0, 0, -1, null, null);
                        Dispatcher.BeginInvoke(() => AppendLog($"  {symbol}: no data on disk, empty series"));
                        return;
                    }
                    int minYear = years[0];
                    int maxYear = years[^1];
                    long? persistedBase = mirror && knownBases.TryGetValue(symbol, out var kb)
                        ? kb
                        : null;
                    var loadYears = InitialLoadYears(
                        hiddenSymbols.Contains(symbol) && !averageSourceNames.Contains(symbol),
                        viewRange, isShift, shiftDelta, minYear, maxYear);
                    var candles = new List<Candle>();
                    var hiddenSpreads = Array.Empty<SpreadMark>();
                    if (loadYears is { } ly)
                    {
                        _startupJobs[symbol] =
                            $"{symbol} {SeriesDataLoader.YearSpanText(ly.Lo, ly.Hi)} · reading (startup)";
                        Dispatcher.BeginInvoke((Action)RefreshLoadIndicator);
                        candles = SeriesDataLoader.ReadYears(db, readSymbol, ly.Lo, ly.Hi);
                        candles = AskViewRule.ToAsk(candles, readSymbol);
                        if (isShift) candles = ShiftedSymbol.Shift(candles, shiftDelta);
                        (candles, hiddenSpreads) = SeriesDataLoader.SplitHidden(candles);
                    }
                    long readMs = swSym.ElapsedMilliseconds;
                    long mirrorBase = persistedBase ?? 0;
                    var transformed = Array.Empty<Candle>();
                    if (candles.Count > 0)
                    {
                        transformed = CandleTransforms.Transform(
                            candles, pipPoints, mirror, out mirrorBase, persistedBase);
                        if (mirror && persistedBase == null) newBases[symbol] = mirrorBase;
                    }
                    var transform = new SeriesTransform(mirror && mirrorBase != 0, mirrorBase, pipPoints);
                    int lastAvg;
                    long lastUnix;
                    if (loadYears is { } lySpan && lySpan.Hi >= maxYear && candles.Count > 0)
                    {
                        lastAvg = candles[^1].Avg;
                        lastUnix = candles[^1].MinuteUnixSeconds;
                    }
                    else
                    {
                        var lastCandle = ReadLastCandle(db, readSymbol);
                        lastAvg = lastCandle?.Avg ?? 0;
                        lastUnix = lastCandle?.MinuteUnixSeconds ?? 0;
                    }
                    bool isBase = SymbolConfigs.Any(c => c.Symbol == symbol);
                    var history = CandleHistory.Build(transformed);
                    history.SetHiddenSpreads(hiddenSpreads);
                    slots[i] = new SeriesSlot(
                        new SymbolSeries(symbol, history, color, pipPoints,
                            false, null, transform, source, null, entryPanel, null, agePanel)
                        {
                            PriceMul = configs[i].PriceDiv,
                            BasePair = configs[i].IsBasePair,
                            RangeStatsRow = configs[i].HasRangeStats,
                            TimeShift = isShift,
                            AgeMirror = configs[i].AgeMirror,
                        },
                        mirrorBase, pipPoints, lastAvg, isBase, lastUnix,
                        readSymbol, mirror, isShift, shiftDelta,
                        minYear, maxYear, loadYears?.Lo, loadYears?.Hi);
                    long buildMs = swSym.ElapsedMilliseconds - readMs;
                    _startupJobs.TryRemove(symbol, out _);
                    string logLine = loadYears == null
                        ? $"  {symbol}: hidden, load deferred ({minYear}-{maxYear} on disk)"
                        : $"  {symbol}: {candles.Count:N0} candles / " +
                          $"{SeriesDataLoader.YearSpanText(loadYears.Value.Lo, loadYears.Value.Hi)}" +
                          $" of {minYear}-{maxYear} - read {readMs} ms, build {buildMs} ms";
                    Dispatcher.BeginInvoke(() =>
                    {
                        AppendLog(logLine);
                        RefreshLoadIndicator();
                    });
                });
                Parallel.For(0, configs.Length, options, i =>
                {
                    var (symbol, color, mirror, pipPoints, editable, source, isDrawing, _, _) = configs[i];
                    bool isDeals = configs[i].IsDeals;
                    bool isDensity = configs[i].IsDensity;
                    bool isSpread = configs[i].IsSpread;
                    bool isVolume = configs[i].IsVolume;
                    bool isOrderBook = configs[i].IsOrderBook;
                    bool isAverage = configs[i].IsAverage;
                    bool isLevels = configs[i].IsLevels;
                    if (!isDrawing && !isDeals && !editable && !isDensity && !isSpread && !isVolume
                        && !isOrderBook && !isAverage && !isLevels)
                        return;
                    long mirrorBase = 0;
                    if (source != null)
                    {
                        if (knownBases.TryGetValue(source, out var known)) mirrorBase = known;
                        else if (newBases.TryGetValue(source, out var computed)) mirrorBase = computed;
                        else
                            for (int j = 0; j < configs.Length; j++)
                                if (configs[j].Symbol == source && slots[j] is { } sv)
                                {
                                    mirrorBase = sv.MirrorBase;
                                    break;
                                }
                    }
                    var transform = new SeriesTransform(mirror && mirrorBase != 0, mirrorBase, pipPoints);
                    int lastVal = 0;
                    long lastUnix = 0;
                    if (isAverage)
                    {
                        var parentMinutes = Array.Empty<Candle>();
                        if (source != null)
                            for (int j = 0; j < configs.Length; j++)
                                if (configs[j].Symbol == source && slots[j] is { } parentSlot)
                                {
                                    parentMinutes = parentSlot.Series.History.Minutes;
                                    break;
                                }
                        var swAvg = Stopwatch.StartNew();
                        var avgSpec = configs[i].Average ?? default;
                        var avgCandles = CachedAverage(symbol, parentMinutes, avgSpec);
                        if (avgCandles.Length > 0)
                        {
                            lastVal = transform.ToRaw(avgCandles[^1].Avg);
                            lastUnix = avgCandles[^1].MinuteUnixSeconds;
                        }
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(avgCandles), color,
                                pipPoints, false, null, transform, source)
                            {
                                PriceMul = configs[i].PriceDiv,
                                Average = avgSpec,
                            },
                            mirrorBase, pipPoints, lastVal, false, lastUnix,
                            "", mirror, false, 0, 0, -1, null, null);
                        string avgKind = avgSpec.Band
                            ? $"{avgSpec.Steps} SMA {avgSpec.StepBars}-{avgSpec.LongestBars} bars, "
                              + avgSpec.Pick.ToString().ToLowerInvariant()
                            : $"SMA {avgSpec.StepBars} bars";
                        string avgLogLine =
                            $"  {symbol}: {avgKind} over " +
                            $"{avgCandles.Length:N0} candles of {source} in {swAvg.ElapsedMilliseconds} ms";
                        Dispatcher.BeginInvoke(() => AppendLog(avgLogLine));
                        return;
                    }
                    if (isLevels)
                    {
                        var ind = indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, null, source)
                            {
                                PriceMul = configs[i].PriceDiv,
                                LevelsPanel = true,
                                LevelWindowMinutes = ind?.LevelWindowMinutes()
                                    ?? IndicatorSymbol.DefaultLevelPeriod * 1440,
                                LevelLengthPx = ind?.LevelLengthPx ?? IndicatorSymbol.DefaultLevelLengthPx,
                                LevelStepPx = ind?.LevelStepPx ?? IndicatorSymbol.DefaultLevelStepPx,
                                SellColorArgb = ind?.SellColorArgb ?? color,
                            },
                            0, pipPoints, 0, false, 0,
                            "", mirror, false, 0, 0, -1, null, null);
                        return;
                    }
                    if (isDensity)
                    {
                        var ind = indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, null, source)
                            {
                                PriceMul = configs[i].PriceDiv,
                                DensityPanel = true,
                                DensityWindows = ind?.DensityWindowBars(),
                                DensityScalePercents = ind?.DensityScalePercentValues(),
                                DensityScalePerPixel = ind?.DensityScalePerPixel ?? 0,
                                DensitySelected = ind?.DensitySelected ?? 0,
                            },
                            0, pipPoints, 0, false, 0,
                            "", mirror, false, 0, 0, -1, null, null);
                        return;
                    }
                    if (isOrderBook)
                    {
                        var ind = indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
                        OrderBookSnapshot[] book = Array.Empty<OrderBookSnapshot>();
                        DepthSnapshot[] depth = Array.Empty<DepthSnapshot>();
                        int depthPip = pipPoints;
                        if (configs[i].IsMarketDepth)
                        {
                            depth = LoadDepth(db, source, out depthPip);
                            Dispatcher.BeginInvoke(() => AppendLog(
                                $"  {symbol}: {depth.Length} depth minute(s) for {source}"));
                        }
                        else
                        {
                            book = LoadOrderBook(db, source);
                            Dispatcher.BeginInvoke(() => AppendLog(
                                $"  {symbol}: {book.Length} order book snapshot(s) for {source}"));
                        }
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, null, source)
                            {
                                PriceMul = configs[i].PriceDiv,
                                OrderBookPanel = true,
                                OrderBookPositions = configs[i].IsOrderBookPositions,
                                OrderBookSnapshots = book.Length > 0 ? book : null,
                                DepthSnapshots = depth.Length > 0 ? depth : null,
                                DepthPipPoints = depthPip,
                                SellColorArgb = ind?.SellColorArgb ?? color,
                            },
                            0, pipPoints, 0, false, 0,
                            "", mirror, false, 0, 0, -1, null, null);
                        return;
                    }
                    if (isSpread)
                    {
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, null, source)
                            {
                                PriceMul = configs[i].PriceDiv,
                                SpreadPanel = true,
                            },
                            0, pipPoints, 0, false, 0,
                            "", mirror, false, 0, 0, -1, null, null);
                        return;
                    }
                    if (isVolume)
                    {
                        var ind = indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
                        var volumeProfiles = LoadVolumeProfiles(db, source, pipPoints);
                        if (volumeProfiles != null)
                            Dispatcher.BeginInvoke(() => AppendLog(
                                $"  {symbol}: {volumeProfiles.Count:N0} profile minute(s) for {source}"));
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, null, source)
                            {
                                PriceMul = configs[i].PriceDiv,
                                VolumePanel = true,
                                VolumeGroupMinutes = ind?.EffectiveVolumeGroupMinutes() ?? 1,
                                VolumeBarScale = ind?.VolumeBarScale ?? 1,
                                VolumeBarUnit = ind?.VolumeBarUnit ?? 0,
                                VolumeGroupLocked = ind?.VolumeGroupLocked ?? false,
                                DensityPanel = true,
                                VolumeWeighted = true,
                                VolumeSplitSides = ind?.VolumeSplitSides ?? true,
                                VolumeProfiles = volumeProfiles,
                                SellColorArgb = ind?.SellColorArgb ?? color,
                                DensityWindows = ind?.DensityWindowBars(),
                                DensityScalePercents = ind?.DensityScalePercentValues(),
                                DensityScalePerPixel = ind?.DensityScalePerPixel ?? 0,
                                DensitySelected = ind?.DensitySelected ?? 0,
                            },
                            0, pipPoints, 0, false, 0,
                            "", mirror, false, 0, 0, -1, null, null);
                        return;
                    }
                    if (isDeals)
                    {
                        var ind = indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, symbol));
                        var marks = LoadDealMarks(ind, source);
                        if (marks.Length > 0)
                        {
                            var lastMark = marks[^1];
                            lastVal = lastMark.Closed ? lastMark.ExitValue : lastMark.EntryValue;
                            lastUnix = lastMark.Closed ? lastMark.ExitUnix : lastMark.EntryUnix;
                        }
                        Dispatcher.BeginInvoke(() =>
                            AppendLog($"  {symbol}: {marks.Length} deals from {ind?.DealsFile}"));
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, transform, source, null, false, marks)
                            { PriceMul = configs[i].PriceDiv },
                            mirrorBase, pipPoints, lastVal, false, lastUnix,
                            "", mirror, false, 0, 0, -1, null, null);
                        return;
                    }
                    if (editable)
                    {
                        var points = ZigZagStore.Load(db.SymbolDirectory(symbol));
                        if (points.Length > 0)
                        {
                            lastVal = (int)Math.Round(points[^1].Value);
                            lastUnix = points[^1].UnixSeconds;
                        }
                        Dispatcher.BeginInvoke(() =>
                            AppendLog($"  {symbol}: {points.Length:N0} ZigZag points"));
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, true, points, transform, source)
                            {
                                PriceMul = configs[i].PriceDiv,
                                RangeStatsRow = configs[i].HasRangeStats,
                            },
                            mirrorBase, pipPoints, lastVal, false, lastUnix,
                            "", mirror, false, 0, 0, -1, null, null);
                        return;
                    }
                    var drawingLines = activeNote?.LinesOf(symbol)
                        ?? DrawingStore.Load(db.SymbolDirectory(symbol));
                    if (drawingLines.Length > 0 && drawingLines[^1].Length > 0)
                    {
                        lastVal = (int)Math.Round(drawingLines[^1][^1].Value);
                        lastUnix = drawingLines[^1][^1].UnixSeconds;
                    }
                    slots[i] = new SeriesSlot(
                        new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                            pipPoints, false, null, transform, source, drawingLines)
                        { PriceMul = configs[i].PriceDiv },
                        mirrorBase, pipPoints, lastVal, false, lastUnix,
                        "", mirror, false, 0, 0, -1, null, null);
                });
            }, token);
            if (token.IsCancellationRequested) return;
            if (!newBases.IsEmpty)
            {
                _config.MirrorBases ??= new Dictionary<string, long>();
                foreach (var (sym, mb) in newBases) _config.MirrorBases[sym] = mb;
                _config.Save();
            }
            var series = new List<SymbolSeries>();
            var transforms = new List<(string Symbol, long MirrorBase, int PipPoints)>();
            var lastTrueAvgs = new List<int>();
            var baseInfo = new Dictionary<string, (long, long, int)>();
            var loaderSeries = new List<SeriesDataLoader.SeriesInit>();
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                series.Add(slot.Series);
                transforms.Add((slot.Series.Symbol, slot.MirrorBase, slot.PipPoints));
                lastTrueAvgs.Add(slot.LastAvg);
                if (slot.IsBase) baseInfo[slot.Series.Symbol] = (slot.LastUnix, slot.MirrorBase, slot.PipPoints);
                if (slot.ReadSymbol.Length > 0)
                    loaderSeries.Add(new SeriesDataLoader.SeriesInit(
                        slot.Series.Symbol, slot.ReadSymbol, slot.Mirror, slot.PipPoints,
                        slot.IsShift, slot.ShiftDelta, slot.MinYear, slot.MaxYear,
                        slot.LoadedLo, slot.LoadedHi, slot.MirrorBase));
            }
            long totalCandles = 0;
            foreach (var s in series) totalCandles += s.History.Minutes.Length;
            AppendLog($"Chart load: DB read + build done in {swBg.ElapsedMilliseconds} ms - {series.Count} series, {totalCandles:N0} candles in memory");
            if (series.Count == 0)
            {
                AppendLog("DB is empty - use the Connection tab to download history");
                return;
            }
            _seriesTransforms = transforms.ToArray();
            _appliedNoteId = _activeTab.NoteId;
            var swUi = Stopwatch.StartNew();
            Chart.SetSeries(series);
            var alignExcludedNames = new HashSet<string>(indicators
                .Where(x => IndicatorTypes.IsAverage(x.Type) || IndicatorTypes.IsEntryPoints(x.Type)
                    || IndicatorTypes.IsPriceAge(x.Type) || IndicatorTypes.IsDeals(x.Type)
                    || IndicatorTypes.IsDensity(x.Type) || IndicatorTypes.IsSpread(x.Type)
                    || IndicatorTypes.IsVolume(x.Type) || IndicatorTypes.IsOrderBook(x.Type)
                    || IndicatorTypes.IsLevels(x.Type))
                .Select(x => IndicatorSymbol.NameKey(x.Name)));
            Chart.SetAlignExcluded(series
                .Select(s => s.Symbol)
                .Where(s => alignExcludedNames.Contains(IndicatorSymbol.NameKey(s))));
            if (ActiveState != null) Chart.RestoreState(ActiveState);
            var drawingNames = new HashSet<string>(indicators
                .Where(x => IndicatorTypes.IsDrawing(x.Type))
                .Select(x => IndicatorSymbol.NameKey(x.Name)));
            var shiftNames = new HashSet<string>(indicators
                .Where(x => IndicatorTypes.IsShift(x.Type))
                .Select(x => IndicatorSymbol.NameKey(x.Name)));
            var standaloneNames = new HashSet<string>(indicators
                .Where(IsStandaloneIndicator)
                .Select(x => IndicatorSymbol.NameKey(x.Name)));
            SymbolBar.SetSymbols(
                series.Select((s, i) =>
                    (s.Symbol, lastTrueAvgs[i], s.ColorArgb, PipDigits(s.PipPoints * s.PriceMul),
                        s.PriceMul,
                        indicatorNames.Contains(IndicatorSymbol.NameKey(s.Symbol)),
                        drawingNames.Contains(IndicatorSymbol.NameKey(s.Symbol)),
                        shiftNames.Contains(IndicatorSymbol.NameKey(s.Symbol)),
                        s.SourceSymbol,
                        standaloneNames.Contains(IndicatorSymbol.NameKey(s.Symbol)))).ToList());
            SymbolBar.SetSpreadSymbols(series.Where(s => s.SpreadPanel).Select(s => s.Symbol));
            _spreadSeriesKeys = new HashSet<string>(series
                .Where(s => s.SpreadPanel)
                .Select(s => IndicatorSymbol.NameKey(s.Symbol)), StringComparer.Ordinal);
            SymbolBar.SetDensitySymbols(series.Where(s => s.DensityPanel).Select(s => s.Symbol));
            SymbolBar.SetAverageSymbols(series
                .Where(s => s.Average is { Band: false })
                .Select(s => (s.Symbol, s.AverageWindowBars)));
            SymbolBar.SetVolumeSymbols(series
                .Where(s => s.VolumePanel)
                .Select(s => (s.Symbol, s.VolumeGroupMinutes, s.VolumeGroupLocked)));
            _volumeSeriesKeys = new HashSet<string>(series
                .Where(s => s.VolumePanel)
                .Select(s => IndicatorSymbol.NameKey(s.Symbol)), StringComparer.Ordinal);
            SymbolBar.SetCollapsedSources(Chart.CollapsedSources);
            foreach (var s in series)
                SymbolBar.SetSymbolEnabled(s.Symbol, !Chart.HiddenSymbols.Contains(s.Symbol));
            ChartTools.SetCalendarRow(_calendarEntries.Length > 0, Chart.CalendarVisible);
            ChartTools.SetForecastRow(Chart.HasForecasts, Chart.ForecastVisible);
            ChartTools.SetWeekendsRow(Chart.WeekendsHidden);
            ChartTools.SetSessionsRow(Chart.SessionsVisible);
            ChartTools.SetCommentsRow(Chart.CommentsVisible);
            SymbolBar.SetTiltedGridRow(Chart.TiltedUpGridIndex, Chart.TiltedDownGridIndex);
            _baseInfo = baseInfo;
            RefreshGame();
            ReconcileLive();
            var loader = new SeriesDataLoader(db, Chart, Dispatcher, loaderSeries,
                AppendLog, OnMirrorBaseComputed);
            loader.JobsChanged += () => Dispatcher.BeginInvoke((Action)RefreshLoadIndicator);
            _loader = loader;
            foreach (var ind in indicators)
                if ((IndicatorTypes.IsDensity(ind.Type) || IndicatorTypes.IsVolume(ind.Type))
                    && ind.DensitySelected >= IndicatorSymbol.DensityAllOption)
                    _ = loader.EnsureFullAsync(ind.Source, "density all history");
            AppendLog($"Chart load: UI setup {swUi.ElapsedMilliseconds} ms; total {swTotal.ElapsedMilliseconds} ms (first paint follows ~200 ms later)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog("Chart load failed: " + ex.Message);
        }
        finally
        {
            _startupJobs.Clear();
            RefreshLoadIndicator();
        }
    }

    private readonly Dictionary<string, (string Stamp, object Value)> _storeCache =
        new(StringComparer.Ordinal);

    private object? CachedStore(string key, string stamp, Func<object?> read)
    {
        if (stamp.Length > 0)
            lock (_storeCache)
                if (_storeCache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Value;
        var value = read();
        if (stamp.Length > 0 && value != null)
            lock (_storeCache) _storeCache[key] = (stamp, value);
        return value;
    }

    private static string FolderStamp(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory)) return "";
            var files = Directory.GetFiles(directory, pattern);
            if (files.Length == 0) return "";
            Array.Sort(files, StringComparer.Ordinal);
            var parts = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                var info = new FileInfo(files[i]);
                parts[i] = $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }
            return string.Join(";", parts);
        }
        catch
        {
            return "";
        }
    }

    private static string FileStamp(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return "";
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
        }
        catch
        {
            return "";
        }
    }

    private readonly Dictionary<string, (string Key, Candle[] Value)> _averageCache =
        new(StringComparer.Ordinal);

    private Candle[] CachedAverage(string symbol, Candle[] parent, AverageSpec spec)
    {
        var key = AverageCacheKey(parent, spec);
        lock (_averageCache)
            if (_averageCache.TryGetValue(symbol, out var hit) && hit.Key == key) return hit.Value;
        var value = AverageSeries.Compute(parent, spec);
        lock (_averageCache) _averageCache[symbol] = (key, value);
        return value;
    }

    private static string AverageCacheKey(Candle[] parent, AverageSpec spec)
    {
        long prices = 0;
        long wide = 0;
        long volumes = 0;
        for (int i = 0; i < parent.Length; i++)
        {
            var c = parent[i];
            prices += c.Avg;
            if (c.WideSpread) wide += i + 1;
            if (c.HasVolume) volumes += c.Volume;
        }
        long first = parent.Length == 0 ? 0 : parent[0].MinuteUnixSeconds;
        long last = parent.Length == 0 ? 0 : parent[^1].MinuteUnixSeconds;
        return $"{parent.Length}:{first}:{last}:{prices}:{wide}:{volumes}:{spec}";
    }

    private ProfileSet? LoadVolumeProfiles(CandleDatabase db, string? source, int pipPoints)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var directory = db.SymbolDirectory(source);
        return (ProfileSet?)CachedStore(
            $"volume:{directory}:{pipPoints}",
            FolderStamp(VolumeProfileStore.Directory(directory), "*.vap"),
            () => ReadVolumeProfiles(directory, source, pipPoints));
    }

    private ProfileSet? ReadVolumeProfiles(string directory, string source, int pipPoints)
    {
        try
        {
            var records = VolumeProfileStore.ReadAll(directory,
                CandleDatabase.Sanitize(source), pipPoints, 100000, out int duplicates,
                s => Dispatcher.BeginInvoke(() => AppendLog("  " + s)));
            if (records.Count == 0) return null;
            if (duplicates * 10 > records.Count * 3)
                Dispatcher.BeginInvoke(() => AppendLog(
                    $"  {source}: profile store carries {duplicates:N0} duplicate records, " +
                    "re-run Fx.VolumeFill to compact"));
            return ProfileSet.FromRecords(records);
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => AppendLog(
                $"  {source}: cannot read the volume profile store: {ex.Message}"));
            return null;
        }
    }

    private sealed class DepthCache
    {
        public readonly Dictionary<int, (long Length, List<DepthSnapshot> Items)> Years = new();
        public int PipPoints = 10;
        public DepthSnapshot[] Flat = Array.Empty<DepthSnapshot>();
        public string Stamp = "";
    }

    private readonly Dictionary<string, DepthCache> _depthCache = new(StringComparer.OrdinalIgnoreCase);

    private DepthSnapshot[] LoadDepth(CandleDatabase db, string? source, out int pipPoints)
    {
        pipPoints = 10;
        if (string.IsNullOrEmpty(source)) return Array.Empty<DepthSnapshot>();
        var directory = db.SymbolDirectory(source);
        try
        {
            lock (_depthCache)
            {
                if (!_depthCache.TryGetValue(directory, out var cache))
                {
                    cache = new DepthCache();
                    _depthCache[directory] = cache;
                }
                var stamp = FolderStamp(DepthStore.Directory(directory), "*.dpt");
                if (stamp.Length > 0 && stamp == cache.Stamp)
                {
                    pipPoints = cache.PipPoints;
                    return cache.Flat;
                }
                var years = DepthStore.ExistingYears(directory);
                foreach (var gone in cache.Years.Keys.Where(y => !years.Contains(y)).ToList())
                    cache.Years.Remove(gone);
                int pp = cache.PipPoints;
                int total = 0;
                foreach (var year in years)
                {
                    if (!cache.Years.TryGetValue(year, out var part))
                        part = (0, new List<DepthSnapshot>());
                    long length = DepthStore.ReadYearFrom(directory, year, part.Length, part.Items, ref pp);
                    cache.Years[year] = (length, part.Items);
                    total += part.Items.Count;
                }
                var flat = new DepthSnapshot[total];
                int at = 0;
                foreach (var year in years)
                {
                    var items = cache.Years[year].Items;
                    items.CopyTo(flat, at);
                    at += items.Count;
                }
                cache.PipPoints = pp > 0 ? pp : 10;
                cache.Flat = flat;
                cache.Stamp = stamp;
                pipPoints = cache.PipPoints;
                return flat;
            }
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => AppendLog($"  {source}: cannot read the depth store: {ex.Message}"));
            return Array.Empty<DepthSnapshot>();
        }
    }

    private OrderBookSnapshot[] LoadOrderBook(CandleDatabase db, string? source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<OrderBookSnapshot>();
        var directory = db.SymbolDirectory(source);
        return (OrderBookSnapshot[]?)CachedStore(
            $"orderbook:{directory}",
            FolderStamp(OrderBookStore.Directory(directory), "*.obk"),
            () => ReadOrderBook(directory, source)) ?? Array.Empty<OrderBookSnapshot>();
    }

    private OrderBookSnapshot[]? ReadOrderBook(string directory, string source)
    {
        try
        {
            return OrderBookStore.ReadAll(directory).ToArray();
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => AppendLog($"  {source}: cannot read the order book: {ex.Message}"));
            return null;
        }
    }

    private static string ForecastFolder => Path.Combine(DbRoot, ForecastStore.DefaultFolder);

    private static ForecastMark[] LoadForecastMarks()
    {
        var marks = new List<ForecastMark>();
        foreach (var (day, records) in ForecastStore.LoadFolderByDay(ForecastFolder))
        foreach (var rec in records)
        {
            if (!rec.IsUsable()) continue;
            int priceDiv = SymbolPriceDiv(rec.Pair);
            int top = (int)Math.Round(rec.TopPrice * 100000 / priceDiv);
            int bottom = (int)Math.Round(rec.BottomPrice * 100000 / priceDiv);
            marks.Add(new ForecastMark(
                rec.Pair, day, rec.MadeAtUnix - rec.MadeAtUnix % 60, rec.UntilUnix - rec.UntilUnix % 60,
                top, bottom, rec.IsRange, rec));
        }
        return marks.OrderBy(m => m.FromUnix).ToArray();
    }

    private DealMark[] LoadDealMarks(IndicatorSymbol? ind, string? source)
    {
        if (ind == null || source == null) return Array.Empty<DealMark>();
        return (DealMark[]?)CachedStore(
            $"deals:{ind.DealsFile}:{IndicatorSymbol.NameKey(source)}",
            FileStamp(ind.DealsFile),
            () => ReadDealMarks(ind, source)) ?? Array.Empty<DealMark>();
    }

    private static DealMark[] ReadDealMarks(IndicatorSymbol ind, string source)
    {
        var sourceKey = IndicatorSymbol.NameKey(source);
        int priceDiv = SymbolPriceDiv(source);
        var marks = new List<DealMark>();
        foreach (var deal in DealsStore.Load(ind.DealsFile))
        {
            if (IndicatorSymbol.NameKey(deal.Symbol) != sourceKey) continue;
            if (deal.OpenTimeUnix <= 0 && !deal.IsClosed) continue;
            long entryUnix = deal.OpenTimeUnix > 0 ? deal.OpenTimeUnix : deal.CloseTimeUnix;
            int entryValue = (int)Math.Round(
                (deal.OpenPrice > 0 ? deal.OpenPrice : deal.ClosePrice) * 100000 / priceDiv);
            marks.Add(new DealMark(
                entryUnix - entryUnix % 60, entryValue,
                deal.CloseTimeUnix - deal.CloseTimeUnix % 60,
                (int)Math.Round(deal.ClosePrice * 100000 / priceDiv),
                deal.IsBuy, deal.Profit >= 0, deal.IsClosed));
        }
        return marks.OrderBy(m => m.EntryUnix).ToArray();
    }

    private (long Lo, long Hi)? StartupRealRange()
    {
        var state = ActiveState;
        long saved = state?.RestoredColumnSeconds() ?? 0;
        if (saved <= 0) return null;
        long bucketSec = ChartColumns.Snap(saved);
        long startBucket = state!.ViewStartBucket * saved / bucketSec;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pw = (int)Math.Round(Chart.ActualWidth * scale);
        if (pw < 100) pw = 2000;
        long lo = (startBucket - pw) * bucketSec;
        long hi = (startBucket + 2L * pw) * bucketSec;
        if (state.WeekendsHidden)
        {
            lo = WeekendCompressor.Instance.ToReal(lo);
            hi = WeekendCompressor.Instance.ToReal(hi);
        }
        return (lo, hi);
    }

    private static (int Lo, int Hi)? InitialLoadYears(bool hidden, (long Lo, long Hi)? viewRange,
        bool isShift, long shiftDelta, int minYear, int maxYear)
    {
        if (hidden) return null;
        if (viewRange == null) return (minYear, maxYear);
        long lo = viewRange.Value.Lo;
        long hi = viewRange.Value.Hi;
        if (isShift)
        {
            lo = SeriesDataLoader.ChartToSource(lo, shiftDelta);
            hi = SeriesDataLoader.ChartToSource(hi, shiftDelta);
        }
        int yearLo = Math.Max(minYear, SeriesDataLoader.YearOfUnix(lo));
        int yearHi = Math.Min(maxYear, SeriesDataLoader.YearOfUnix(hi));
        if (yearLo <= yearHi) return (yearLo, yearHi);
        return SeriesDataLoader.YearOfUnix(hi) < minYear ? (minYear, minYear) : (maxYear, maxYear);
    }

    private static Candle? ReadLastCandle(CandleDatabase db, string symbol)
    {
        var last = db.LastFilledMinuteUtc(symbol);
        if (last == null) return null;
        var candles = db.ReadRange(symbol, last.Value.AddDays(-1), last.Value);
        return candles.Count > 0 ? candles[^1] : null;
    }

    private void OnViewRangeChanged(long columnSeconds, long startBucket, int widthPx, WeekendCompressor? map)
    {
        using var _ = Perf.Step("view.loader");
        long bucketSec = columnSeconds;
        long lo = (startBucket - widthPx) * bucketSec;
        long hi = (startBucket + 2L * widthPx) * bucketSec;
        if (map != null)
        {
            lo = map.ToReal(lo);
            hi = map.ToReal(hi);
        }
        _lastViewRealRange = (lo, hi);
        var loader = _loader;
        if (loader == null) return;
        if (Chart.IsFitView) loader.EnsureFullVisible(LoaderHidden());
        else loader.EnsureVisibleRange(lo, hi, LoaderHidden());
    }

    private IReadOnlyCollection<string> LoaderHidden()
    {
        var hidden = Chart.HiddenSymbols;
        if (hidden.Count == 0) return hidden;
        HashSet<string>? filtered = null;
        foreach (var ind in _config.Indicators)
        {
            if (!IndicatorTypes.IsAverage(ind.Type)) continue;
            if (hidden.Contains(ind.Name)) continue;
            if (!hidden.Contains(ind.Source)) continue;
            filtered ??= new HashSet<string>(hidden);
            filtered.Remove(ind.Source);
        }
        return filtered ?? hidden;
    }

    private void OnAveragePeriodWheel(string symbol, int wheelDelta, bool fine)
    {
        if (wheelDelta == 0) return;
        var ind = _config.Indicators.FirstOrDefault(x =>
            IndicatorTypes.IsPlainAverage(x.Type) && SymbolNameEquals(x.Name, symbol));
        if (ind == null) return;
        int step = fine ? AverageWheelFineMinutes : AverageWheelMinutes;
        int current = _pendingAverageWindows.TryGetValue(symbol, out var pending)
            ? pending
            : MovingAverageSymbol.WindowBars(ind.Period, ind.Unit);
        int minutes = Math.Clamp(current + (wheelDelta > 0 ? step : -step),
            AverageWheelFineMinutes, MaxAverageWindowMinutes);
        if (minutes == current) return;
        if (minutes % IndicatorUnits.BarsPerUnit(IndicatorUnits.Days) == 0)
        {
            ind.Period = minutes / IndicatorUnits.BarsPerUnit(IndicatorUnits.Days);
            ind.Unit = IndicatorUnits.Days;
        }
        else if (minutes % IndicatorUnits.BarsPerUnit(IndicatorUnits.Hours) == 0)
        {
            ind.Period = minutes / IndicatorUnits.BarsPerUnit(IndicatorUnits.Hours);
            ind.Unit = IndicatorUnits.Hours;
        }
        else
        {
            ind.Period = minutes;
            ind.Unit = IndicatorUnits.Minutes;
        }
        _config.Save();
        SymbolBar.SetAverageWindow(symbol, minutes);
        _pendingAverageWindows[symbol] = minutes;
        _averageWindowTimer.Stop();
        _averageWindowTimer.Start();
        AppendLog($"{ind.Name}: window {ind.Period} {ind.Unit.ToLowerInvariant()}");
    }

    private void OnMirrorBaseComputed(string symbol, long mirrorBase)
    {
        _config.MirrorBases ??= new Dictionary<string, long>();
        _config.MirrorBases[symbol] = mirrorBase;
        _config.Save();
        var transforms = _seriesTransforms;
        for (int i = 0; i < transforms.Length; i++)
            if (transforms[i].Symbol == symbol)
                transforms[i] = (symbol, mirrorBase, transforms[i].PipPoints);
        if (_baseInfo.TryGetValue(symbol, out var bi))
            _baseInfo[symbol] = (bi.LastUnix, mirrorBase, bi.PipPoints);
        if (_live.TryGetValue(symbol, out var live)) live.MirrorBase = mirrorBase;
    }

    private void StopLoader()
    {
        _loader?.Dispose();
        _loader = null;
        RefreshLoadIndicator();
    }

    private void RefreshLoadIndicator()
    {
        var lines = new List<string>();
        foreach (var text in _startupJobs.Values.OrderBy(x => x, StringComparer.Ordinal))
            lines.Add(text);
        var loader = _loader;
        if (loader != null) lines.AddRange(loader.DescribeJobs());
        LoadIndicator.SetJobs(lines);
    }

    private void SaveInputs()
    {
        _config.ClientId = ClientIdBox.Text.Trim();
        _config.ClientSecret = ClientSecretBox.Text.Trim();
        _config.IsLive = LiveCheck.IsChecked == true;
        _config.Save();
    }

    private async void AuthorizeBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveInputs();
        if (_config.ClientId == "" || _config.ClientSecret == "")
        {
            AppendLog("Fill Client ID and Client Secret first (from openapi.ctrader.com)");
            return;
        }
        await CancelPendingAuthAsync();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var codeTask = OAuthService.WaitForCodeAsync(cts.Token);
        _authCts = cts;
        _authCodeTask = codeTask;
        try
        {
            AppendLog("Redirect URI registered for the app must be exactly: " + OAuthService.RedirectUri);
            AppendLog("Opening browser for authorization...");
            OAuthService.OpenBrowser(OAuthService.BuildAuthUrl(_config.ClientId));
            var code = await codeTask;
            AppendLog("Authorization code received, exchanging for token...");
            StoreToken(await OAuthService.ExchangeCodeAsync(_config.ClientId, _config.ClientSecret, code));
            AppendLog("Access token saved to config" + TokenExpiryText());
        }
        catch (OperationCanceledException)
        {
            AppendLog("Authorization cancelled or timed out");
        }
        catch (Exception ex)
        {
            cts.Cancel();
            AppendLog("Authorize failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_authCts, cts))
            {
                _authCts = null;
                _authCodeTask = null;
            }
        }
    }

    private async Task CancelPendingAuthAsync()
    {
        var cts = _authCts;
        var task = _authCodeTask;
        _authCts = null;
        _authCodeTask = null;
        if (cts == null) return;
        cts.Cancel();
        if (task != null)
        {
            try { await task; } catch { }
        }
    }

    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        _autoReconnect = true;
        if (!await ConnectAndStreamAsync() && _autoReconnect)
            StartReconnectLoop();
    }

    private void StoreToken(TokenSet token)
    {
        _config.AccessToken = token.AccessToken;
        if (token.RefreshToken != "") _config.RefreshToken = token.RefreshToken;
        _config.TokenExpiresUnix = token.ExpiresInSeconds > 0
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + token.ExpiresInSeconds
            : 0;
        _config.Save();
    }

    private string TokenExpiryText() => _config.TokenExpiresUnix > 0
        ? ", valid until " + DateTimeOffset.FromUnixTimeSeconds(_config.TokenExpiresUnix)
            .UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC"
        : "";

    private static bool IsTokenError(Exception ex) =>
        ex.Message.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        && (ex.Message.Contains("INVALID", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase));

    private async Task<bool> RefreshTokenAsync()
    {
        if (_config.RefreshToken == "" || _config.ClientId == "" || _config.ClientSecret == "")
        {
            AppendLog("No refresh token saved - open the Connection tab and press Authorize");
            return false;
        }
        try
        {
            AppendLog("Refreshing access token...");
            StoreToken(await OAuthService.RefreshAsync(_config.ClientId, _config.ClientSecret, _config.RefreshToken));
            AppendLog("Access token refreshed" + TokenExpiryText());
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("Token refresh failed: " + ex.Message + " - press Authorize to log in again");
            return false;
        }
    }

    private async Task<bool> ConnectCoreAsync()
    {
        if (_connectInProgress)
        {
            AppendLog("Connect already in progress");
            return false;
        }
        _connectInProgress = true;
        try
        {
            SaveInputs();
            if (_config.AccessToken == "")
            {
                AppendLog("No access token, run Authorize first");
                _autoReconnect = false;
                SetConnState(ConnState.Offline);
                return false;
            }
            if (_config.TokenExpiresUnix > 0
                && _config.TokenExpiresUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds() < TokenRenewAheadSeconds
                && !await RefreshTokenAsync())
                return StopForExpiredToken();
            try
            {
                return await ConnectAttemptAsync();
            }
            catch (Exception ex) when (IsTokenError(ex))
            {
                AppendLog("Access token rejected: " + ex.Message);
                if (!await RefreshTokenAsync()) return StopForExpiredToken();
                return await ConnectAttemptAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog("Connect failed: " + ex.Message);
            SetStatus("Failed");
            SetConnState(ConnState.Offline);
            return false;
        }
        finally
        {
            _connectInProgress = false;
        }
    }

    private const long TokenRenewAheadSeconds = 24 * 3600;

    private bool StopForExpiredToken()
    {
        _autoReconnect = false;
        SetStatus("Token expired");
        SetConnState(ConnState.Offline);
        return false;
    }

    private async Task<bool> ConnectAttemptAsync()
    {
        _historyCts?.Cancel();
        _repairCts?.Cancel();
        await DisconnectAsync();
        var client = new CTraderClient();
        _client = client;
        client.Log += m => Dispatcher.BeginInvoke(() => AppendLog(m));
        client.Disconnected += m => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_client, client)) return;
            AppendLog("Connection lost: " + m);
            SetStatus("Disconnected");
            SetConnState(ConnState.Offline);
            _historyCts?.Cancel();
            _ = ResumeAfterLossAsync();
        });
        client.SpotReceived += OnSpot;
        client.DepthReceived += OnDepth;
        SetStatus("Connecting...");
        SetConnState(ConnState.Connecting);
        await client.ConnectAsync(_config.IsLive, CancellationToken.None);
        await client.ApplicationAuthAsync(_config.ClientId, _config.ClientSecret, CancellationToken.None);
        var accounts = await client.GetAccountListAsync(_config.AccessToken, CancellationToken.None);
        var account = accounts.FirstOrDefault(a => a.IsLive == _config.IsLive);
        if (account == null)
        {
            if (accounts.Count == 0)
            {
                AppendLog("No cTrader accounts found for this access token");
                SetStatus("No account");
            }
            else
            {
                var available = string.Join(", ",
                    accounts.Select(a => $"{a.CtidTraderAccountId} ({(a.IsLive ? "live" : "demo")})"));
                AppendLog($"Token has no {(_config.IsLive ? "live" : "demo")} accounts. Available: {available}. Toggle the Live checkbox and press Connect again.");
                SetStatus("Wrong environment");
            }
            _autoReconnect = false;
            SetConnState(ConnState.Offline);
            return false;
        }
        _accountId = (long)account.CtidTraderAccountId;
        AppendLog($"Accounts on token: {accounts.Count}, using {_accountId} (login {account.TraderLogin}, live={account.IsLive}, broker={account.BrokerTitleShort})");
        await client.AccountAuthAsync(_accountId, _config.AccessToken, CancellationToken.None);
        var symbols = await client.GetSymbolsAsync(_accountId, CancellationToken.None);
        _symbolIds.Clear();
        foreach (var (name, _, _, _, _) in SymbolConfigs)
        {
            var brokerName = AskSources.TryGetValue(name, out var askSource) ? askSource : name;
            var found = symbols.FirstOrDefault(s => Normalize(s.SymbolName) == brokerName);
            if (found == null && BrokerAliases.TryGetValue(brokerName, out var aliases))
                found = aliases
                    .Select(a => symbols.FirstOrDefault(s => Normalize(s.SymbolName) == a))
                    .FirstOrDefault(s => s != null);
            if (found == null)
            {
                var letters = new string(brokerName.Where(char.IsLetter).Take(3).ToArray());
                var digits = new string(brokerName.Where(char.IsDigit).ToArray());
                var similar = symbols
                    .Select(s => s.SymbolName)
                    .Where(n => (letters.Length > 0
                            && n.Contains(letters, StringComparison.OrdinalIgnoreCase))
                        || (digits.Length > 0 && n.Contains(digits)))
                    .Take(20)
                    .ToList();
                AppendLog($"{name} not found among {symbols.Count} symbols" +
                    (similar.Count > 0
                        ? "; similar: " + string.Join(", ", similar)
                        : "; available: " + string.Join(", ", symbols.Select(s => s.SymbolName))));
                continue;
            }
            _symbolIds[name] = found.SymbolId;
            AppendLog(IsAskSymbol(name)
                ? $"{name} symbolId = {found.SymbolId} (ask side of {found.SymbolName})"
                : Normalize(found.SymbolName) == name
                    ? $"{name} symbolId = {found.SymbolId}"
                    : $"{name} symbolId = {found.SymbolId} (broker name {found.SymbolName})");
        }
        if (_symbolIds.Count == 0)
        {
            SetStatus("No symbols");
            _autoReconnect = false;
            SetConnState(ConnState.Offline);
            return false;
        }
        SetStatus("Connected");
        return true;
    }

    private async Task SubscribeAllAsync()
    {
        var client = _client;
        if (client == null || _symbolIds.Count == 0) return;
        try
        {
            _idToSymbol.Clear();
            _idToAskSymbol.Clear();
            _lastQuotes.Clear();
            _live.Clear();
            foreach (var (name, id) in _symbolIds)
            {
                if (IsAskSymbol(name)) _idToAskSymbol[id] = name;
                else _idToSymbol[id] = name;
                if (_baseInfo.TryGetValue(name, out var bi))
                    _live[name] = new LiveState
                    {
                        BaseLastUnix = bi.LastUnix,
                        MirrorBase = bi.MirrorBase,
                        PipPoints = bi.PipPoints,
                    };
            }
            await client.SubscribeSpotsAsync(
                _accountId, _symbolIds.Values.Distinct().ToList(), CancellationToken.None);
            await SubscribeDepthProbeAsync(client);
            SetStatus($"Streaming {_live.Count} pairs");
            SetConnState(ConnState.Online);
            _liveFlushTimer.Start();
            _liveRepairTimer.Start();
        }
        catch (Exception ex)
        {
            AppendLog("Subscribe failed: " + ex.Message);
        }
    }

    private async Task MergeRecentTailAsync()
    {
        var db = _db;
        if (db == null || _live.Count == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ranges = _live
            .Where(kv => kv.Value.BaseLastUnix != long.MinValue && kv.Value.BaseLastUnix + 60 <= now)
            .Select(kv => (Symbol: kv.Key, FromUnix: kv.Value.BaseLastUnix + 60))
            .ToArray();
        if (ranges.Length == 0) return;
        var swMerge = Stopwatch.StartNew();
        try
        {
            var gaps = await Task.Run(() =>
            {
                var result = new List<(string Symbol, List<Candle> Candles)>();
                var to = DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime;
                foreach (var (symbol, fromUnix) in ranges)
                {
                    var from = DateTimeOffset.FromUnixTimeSeconds(fromUnix).UtcDateTime;
                    var candles = db.ReadRange(symbol, from, to);
                    if (candles.Count > 0) result.Add((symbol, candles));
                }
                return result;
            });
            int total = 0;
            foreach (var (symbol, candles) in gaps)
            {
                if (!_live.TryGetValue(symbol, out var s)) continue;
                var fresh = candles.Where(c => c.MinuteUnixSeconds > s.BaseLastUnix).ToList();
                if (fresh.Count == 0) continue;
                s.Closed.RemoveAll(c => c.MinuteUnixSeconds <= fresh[^1].MinuteUnixSeconds);
                s.Closed.InsertRange(0, fresh);
                s.BaseLastUnix = fresh[^1].MinuteUnixSeconds;
                s.Dirty = true;
                total += fresh.Count;
            }
            if (total > 0)
            {
                FlushLive();
                AppendLog($"Merged recent tail: {total:N0} minutes, {gaps.Count} symbols in {swMerge.ElapsedMilliseconds} ms (no full reload)");
            }
        }
        catch (Exception ex)
        {
            AppendLog("Tail merge failed: " + ex.Message);
        }
    }

    private async Task PrimeLiveMarksAsync()
    {
        if (_symbolIds.Count == 0) return;
        var db = GetDb();
        var symbols = _symbolIds.Keys.ToArray();
        var now = DateTimeOffset.UtcNow;
        try
        {
            var loaded = await Task.Run(() =>
            {
                var map = new Dictionary<string, (long ConfirmedTo, long LastWritten)>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var symbol in symbols) map[symbol] = LiveDbWriter.ReadMark(db, symbol, now);
                return map;
            });
            GetLiveDb().InstallMarks(loaded);
        }
        catch (Exception ex)
        {
            AppendLog("Live marks load failed: " + ex.Message);
        }
    }

    private async Task RepairProvisionalAsync()
    {
        var client = _client;
        if (client == null || _symbolIds.Count == 0) return;
        if (_repairCts != null || _historyCts != null || _dbBusy) return;
        var liveDb = GetLiveDb();
        var plan = liveDb.PlanRepair(DateTimeOffset.UtcNow);
        if (plan.Count == 0) return;
        var cts = new CancellationTokenSource();
        _repairCts = cts;
        var ct = cts.Token;
        var db = GetDb();
        try
        {
            foreach (var range in plan)
            {
                if (!_symbolIds.TryGetValue(range.Symbol, out var symbolId)) continue;
                (int Total, long EarliestUnix) written;
                try
                {
                    written = await Task.Run(() => IsAskSymbol(range.Symbol)
                        ? AskHistoryDownloader.DownloadRangeToDbAsync(
                            client, _accountId, symbolId, range.Symbol, db,
                            range.FromUtc, range.ToUtc, _ => { }, ct, SymbolPriceDiv(range.Symbol))
                        : HistoryDownloader.DownloadRangeToDbAsync(
                            client, _accountId, symbolId, range.Symbol, db,
                            range.FromUtc, range.ToUtc, _ => { }, ct, SymbolPriceDiv(range.Symbol)), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    liveDb.MarkFailed(range.Symbol, range.ToUtc);
                    AppendLog($"{range.Symbol}: repair failed: {ex.Message}");
                    continue;
                }
                if (!ReferenceEquals(_client, client)) return;
                liveDb.MarkRepaired(range.Symbol, range.ToUtc);
                if (written.Total == 0) continue;
                AppendLog($"{range.Symbol}: repaired {written.Total} minutes " +
                    $"{range.FromUtc:yyyy-MM-dd HH:mm}..{range.ToUtc:HH:mm} UTC");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog("Live repair failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_repairCts, cts)) _repairCts = null;
            cts.Dispose();
        }
    }

    private long ConfirmedEndUnix(IReadOnlyList<string>? pairs, long configuredEndUnix)
    {
        var liveDb = _liveDb;
        if (liveDb == null || pairs == null) return configuredEndUnix;
        long clamp = 0;
        foreach (var pair in pairs)
        {
            if (!liveDb.HasOpenRange(pair)) continue;
            long confirmed = liveDb.ConfirmedToUnix(pair);
            if (clamp == 0 || confirmed < clamp) clamp = confirmed;
        }
        if (clamp == 0) return configuredEndUnix;
        return configuredEndUnix > 0 ? Math.Min(configuredEndUnix, clamp) : clamp;
    }

    private async void HistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        await DownloadHistoryAsync();
        if (_live.Count > 0) SetConnState(ConnState.Online);
    }

    private async Task DownloadHistoryAsync(bool recentOnly = false)
    {
        var client = _client;
        if (client == null || _symbolIds.Count == 0)
        {
            AppendLog("Connect first");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download already running");
            return;
        }
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        _historyCts = new CancellationTokenSource();
        var ct = _historyCts.Token;
        SetStatus("Downloading history...");
        SetConnState(ConnState.Downloading);
        bool reload = false;
        bool newSymbolData = false;
        try
        {
            var db = GetDb();
            foreach (var (symbol, _, _, _, priceDiv) in SymbolConfigs)
            {
                if (!_symbolIds.TryGetValue(symbol, out var symbolId)) continue;
                if (IsAskSymbol(symbol) && !recentOnly)
                    AppendLog($"{symbol}: server has no ask trendbars, downloading recent ask ticks instead");
                var (written, earliest) = await Task.Run(() => IsAskSymbol(symbol)
                    ? AskHistoryDownloader.DownloadTailToDbAsync(
                        client, _accountId, symbolId, symbol, db,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, priceDiv)
                    : recentOnly
                        ? HistoryDownloader.DownloadTailToDbAsync(
                            client, _accountId, symbolId, symbol, db,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, priceDiv)
                        : HistoryDownloader.DownloadM1ToDbAsync(
                            client, _accountId, symbolId, symbol, db,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, priceDiv), ct);
                AppendLog($"{symbol}: {written} minutes written to DB");
                if (written == 0 || earliest == 0) continue;
                if (!_baseInfo.ContainsKey(symbol)) newSymbolData = true;
            }
            reload = !recentOnly || _baseInfo.Count == 0 || newSymbolData;
        }
        catch (OperationCanceledException)
        {
            AppendLog("History download cancelled");
        }
        catch (Exception ex)
        {
            AppendLog("History failed: " + ex.Message);
        }
        finally
        {
            _historyCts.Dispose();
            _historyCts = null;
        }
        if (reload) await LoadChartAsync();
    }

    private const int SpreadBackfillDays = 7;

    private async void BackfillSpreadBtn_Click(object sender, RoutedEventArgs e)
    {
        var client = _client;
        if (client == null || _symbolIds.Count == 0)
        {
            AppendLog("Connect first");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("Another download already running");
            return;
        }
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        _historyCts = new CancellationTokenSource();
        var ct = _historyCts.Token;
        SetStatus("Backfilling spread...");
        SetConnState(ConnState.Downloading);
        var db = GetDb();
        bool wrote = false;
        try
        {
            foreach (var (symbol, _, _, pipPoints, priceDiv) in SymbolConfigs)
            {
                if (IsAskSymbol(symbol)) continue;
                if (!_symbolIds.TryGetValue(symbol, out var symbolId)) continue;
                var years = db.ExistingYears(symbol);
                if (years.Count == 0) continue;
                DateTime? first = null;
                foreach (var y in years)
                {
                    first = db.FilledRangeUtc(symbol, y)?.FirstUtc;
                    if (first != null) break;
                }
                if (first == null) continue;
                var floorUtc = DateTime.UtcNow.AddDays(-SpreadBackfillDays);
                var fromUtc = first.Value < floorUtc ? floorUtc : first.Value;
                if (fromUtc >= DateTime.UtcNow) continue;
                AppendLog($"{symbol}: backfilling spread from bid/ask ticks, " +
                    $"back to {fromUtc:yyyy-MM-dd HH:mm} UTC");
                try
                {
                    var res = await Task.Run(() => SpreadBackfill.RunAsync(
                        client, _accountId, symbolId, symbol, db,
                        new DateTimeOffset(fromUtc, TimeSpan.Zero), DateTimeOffset.UtcNow,
                        priceDiv, pipPoints, m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct), ct);
                    if (res.Written > 0) wrote = true;
                    AppendLog($"{symbol}: spread written for {res.Written:N0} minutes, " +
                        $"max {res.MaxTenths / 10.0:F1} pips" +
                        (res.ReachedStart ? "" : ", older minutes have no tick data on the server"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"{symbol}: spread backfill failed: {ex.Message}");
                }
            }
            SetStatus("Spread backfill done");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Spread backfill cancelled");
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            AppendLog("Spread backfill failed: " + ex.Message);
            SetStatus("Backfill failed");
        }
        finally
        {
            db.FlushAll();
            _historyCts.Dispose();
            _historyCts = null;
            if (_live.Count > 0) SetConnState(ConnState.Online);
        }
        if (wrote) await LoadChartAsync();
    }

    private async void BackfillWideSpreadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        _dbBusy = true;
        SetStatus("Flagging wide spread minutes...");
        var db = GetDb();
        try
        {
            var total = await Task.Run(() =>
            {
                var sum = default(WideSpreadStats);
                foreach (var (symbol, _, _, _, _) in SymbolConfigs)
                {
                    var years = db.ExistingYears(symbol);
                    if (years.Count == 0) continue;
                    var perSymbol = default(WideSpreadStats);
                    foreach (var y in years)
                        perSymbol = perSymbol.Add(db.RecomputeWideSpread(symbol, y));
                    db.FlushSymbol(symbol);
                    sum = sum.Add(perSymbol);
                    Dispatcher.BeginInvoke(() => AppendLog(
                        $"{symbol} wide spread: {perSymbol.Wide:N0} of {perSymbol.Scanned:N0} minutes, " +
                        $"{perSymbol.Changed:N0} flags changed"));
                }
                return sum;
            });
            AppendLog($"Wide spread backfill done: {total.Wide:N0} of {total.Scanned:N0} minutes flagged, " +
                $"{total.Changed:N0} changed");
            SetStatus("Wide spread backfill done");
            if (total.Changed > 0 && WideSpreadRule.Hide) await LoadChartAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Wide spread backfill failed: " + ex.Message);
            SetStatus("Backfill failed");
        }
        finally
        {
            _dbBusy = false;
        }
    }

    private async void ExportDealsBtn_Click(object sender, RoutedEventArgs e)
    {
        var client = _client;
        if (client == null)
        {
            AppendLog("Connect first");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("Another download already running");
            return;
        }
        _historyCts = new CancellationTokenSource();
        var ct = _historyCts.Token;
        SetStatus("Exporting deals...");
        try
        {
            var symbols = await client.GetSymbolsAsync(_accountId, ct);
            var names = new Dictionary<long, string>();
            foreach (var s in symbols) names[s.SymbolId] = Normalize(s.SymbolName);

            const long WeekMs = 7L * 24 * 3600 * 1000;
            var requestDelay = TimeSpan.FromMilliseconds(250);
            async Task<ProtoOADealListRes> FetchPageAsync(long a, long b)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        var res = await client.GetDealsAsync(_accountId, a, b, ct);
                        await Task.Delay(requestDelay, ct);
                        return res;
                    }
                    catch (InvalidOperationException ex) when (attempt < 5
                        && (ex.Message.Contains("rate limited", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("BLOCKED_PAYLOAD_TYPE", StringComparison.OrdinalIgnoreCase)))
                    {
                        int waitSeconds = 10 * (attempt + 1);
                        AppendLog($"Deals: rate limited, waiting {waitSeconds} s...");
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
                    }
                }
            }
            long toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long fromMs = DateTimeOffset.UtcNow.AddYears(-3).ToUnixTimeMilliseconds();
            var seen = new HashSet<long>();
            var deals = new List<ProtoOADeal>();
            int weeks = 0;
            for (long a = fromMs; a < toMs; a += WeekMs)
            {
                ct.ThrowIfCancellationRequested();
                long b = Math.Min(a + WeekMs, toMs);
                long cursor = a;
                while (cursor < b)
                {
                    var res = await FetchPageAsync(cursor, b);
                    long lastTs = cursor;
                    foreach (var d in res.Deal)
                    {
                        if (seen.Add(d.DealId)) deals.Add(d);
                        if (d.ExecutionTimestamp > lastTs) lastTs = d.ExecutionTimestamp;
                    }
                    if (!res.HasMore) break;
                    if (lastTs <= cursor) break;
                    cursor = lastTs;
                }
                weeks++;
                if (weeks % 13 == 0)
                    AppendLog($"Deals scan: {UnixMsToUtc(b):yyyy-MM-dd}, {deals.Count} deals so far");
            }

            var records = BuildDealRecords(deals, names);
            var path = Path.Combine(AppContext.BaseDirectory, "deals", $"deals-{_accountId}.json");
            DealsStore.Save(path, new DealsFileModel
            {
                Account = _accountId,
                ExportedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Deals = records,
            });
            int open = records.Count(r => !r.IsClosed);
            AppendLog($"Deals exported: {records.Count - open} closed, {open} open, from {deals.Count} raw deals -> {path}");
            SetStatus("Deals exported");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Deals export cancelled");
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            AppendLog("Deals export failed: " + ex.Message);
            SetStatus("Export failed");
        }
        finally
        {
            _historyCts.Dispose();
            _historyCts = null;
            if (_live.Count > 0) SetConnState(ConnState.Online);
        }
    }

    private static List<DealRecord> BuildDealRecords(List<ProtoOADeal> deals, Dictionary<long, string> names)
    {
        var filled = deals
            .Where(d => d.DealStatus is ProtoOADealStatus.Filled or ProtoOADealStatus.PartiallyFilled)
            .OrderBy(d => d.ExecutionTimestamp)
            .ToList();
        var openTime = new Dictionary<long, long>();
        var openVolume = new Dictionary<long, long>();
        var openPriceVolume = new Dictionary<long, double>();
        var openSide = new Dictionary<long, ProtoOATradeSide>();
        var closedVolume = new Dictionary<long, long>();
        var records = new List<DealRecord>();
        foreach (var d in filled)
        {
            string symbol = names.TryGetValue(d.SymbolId, out var n) ? n : d.SymbolId.ToString();
            double pipSize = symbol.EndsWith("JPY", StringComparison.OrdinalIgnoreCase) ? 0.01 : 0.0001;
            if (d.ClosePositionDetail == null)
            {
                if (!openTime.ContainsKey(d.PositionId)) openTime[d.PositionId] = d.ExecutionTimestamp;
                openVolume[d.PositionId] = openVolume.GetValueOrDefault(d.PositionId) + d.FilledVolume;
                openPriceVolume[d.PositionId] =
                    openPriceVolume.GetValueOrDefault(d.PositionId) + d.ExecutionPrice * d.FilledVolume;
                openSide[d.PositionId] = d.TradeSide;
                continue;
            }
            var detail = d.ClosePositionDetail;
            bool buy = d.TradeSide == ProtoOATradeSide.Sell;
            double money = Math.Pow(10, detail.MoneyDigits);
            long volume = detail.HasClosedVolume ? detail.ClosedVolume : d.FilledVolume;
            closedVolume[d.PositionId] = closedVolume.GetValueOrDefault(d.PositionId) + volume;
            records.Add(new DealRecord
            {
                PositionId = d.PositionId,
                Symbol = symbol,
                Side = buy ? "Buy" : "Sell",
                Lots = volume / 10_000_000.0,
                OpenTimeUnix = openTime.GetValueOrDefault(d.PositionId) / 1000,
                OpenPrice = detail.EntryPrice,
                CloseTimeUnix = d.ExecutionTimestamp / 1000,
                ClosePrice = d.ExecutionPrice,
                Profit = (detail.GrossProfit + detail.Swap + detail.Commission) / money,
                Pips = Math.Round((d.ExecutionPrice - detail.EntryPrice) * (buy ? 1 : -1) / pipSize, 1),
            });
        }
        foreach (var (positionId, volume) in openVolume)
        {
            long remaining = volume - closedVolume.GetValueOrDefault(positionId);
            if (remaining <= 0) continue;
            var first = filled.First(d => d.PositionId == positionId && d.ClosePositionDetail == null);
            string symbol = names.TryGetValue(first.SymbolId, out var n) ? n : first.SymbolId.ToString();
            records.Add(new DealRecord
            {
                PositionId = positionId,
                Symbol = symbol,
                Side = openSide[positionId] == ProtoOATradeSide.Buy ? "Buy" : "Sell",
                Lots = remaining / 10_000_000.0,
                OpenTimeUnix = openTime[positionId] / 1000,
                OpenPrice = openPriceVolume[positionId] / volume,
            });
        }
        return records.OrderBy(r => r.OpenTimeUnix).ToList();
    }

    private static DateTime UnixMsToUtc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private async void DisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        _autoReconnect = false;
        _historyCts?.Cancel();
        await DisconnectAsync();
        SetStatus("Not connected");
        SetConnState(ConnState.Offline);
        AppendLog("Disconnected");
    }

    private async Task ResumeAfterLossAsync()
    {
        await DisconnectAsync();
        if (_autoReconnect) StartReconnectLoop();
    }

    private async Task DisconnectAsync()
    {
        StopLive();
        var repair = _repairTask;
        if (repair != null)
        {
            try { await repair; } catch { }
            _repairTask = null;
        }
        var client = _client;
        _client = null;
        _symbolIds.Clear();
        _lastBid = null;
        _lastAsk = null;
        if (client != null) await client.DisposeAsync();
    }

    private static readonly string[] DepthProbePairs = { "EURUSD", "GBPUSD" };

    private async Task SubscribeDepthProbeAsync(CTraderClient client)
    {
        var ids = new List<long>();
        foreach (var pair in DepthProbePairs)
            foreach (var (name, id) in _symbolIds)
                if (SymbolNameEquals(name, pair) && !ids.Contains(id))
                    ids.Add(id);
        if (ids.Count == 0)
        {
            AppendLog("depth: none of the probe pairs are in the symbol list");
            return;
        }
        try
        {
            await client.SubscribeDepthAsync(_accountId, ids, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog("depth: subscribe failed: " + ex.Message);
        }
    }

    private void OnDepth(ProtoOADepthEvent depth)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _depthProbe ??= new DepthProbe(
                id =>
                {
                    foreach (var (name, sid) in _symbolIds)
                        if (sid == id) return name;
                    return null;
                },
                AppendLog);
            _depthProbe.Apply(depth);
        });
    }

    private void OnSpot(ProtoOASpotEvent spot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool hasBidName = _idToSymbol.TryGetValue(spot.SymbolId, out var symbol);
            bool hasAskName = _idToAskSymbol.TryGetValue(spot.SymbolId, out var askSymbol);
            if (!hasBidName && !hasAskName) return;
            if (!_firstSpotLogged)
            {
                _firstSpotLogged = true;
                AppendLog($"First live spot received ({(hasBidName ? symbol : askSymbol)})");
            }
            long spreadRaw = TrackQuote(spot);
            if (spreadRaw >= 0)
            {
                if (hasBidName && !spot.HasBid)
                    BumpLiveSpread(symbol!, SpreadTenths(symbol!, spreadRaw));
                if (hasAskName && !spot.HasAsk)
                    BumpLiveSpread(askSymbol!, SpreadTenths(askSymbol!, spreadRaw));
            }
            if (hasBidName && spot.HasBid)
            {
                int div = SymbolPriceDiv(symbol!);
                int bidPoints = (int)(((long)spot.Bid + div / 2) / div);
                FeedLive(symbol!, bidPoints, SpreadTenths(symbol!, spreadRaw));
                SymbolBar.SetLastPrice(symbol!, bidPoints);
                if (symbol == "EURUSD")
                {
                    _lastBid = spot.Bid;
                    BidText.Text = FormatPrice(spot.Bid);
                }
            }
            if (hasAskName && spot.HasAsk)
            {
                int div = SymbolPriceDiv(askSymbol!);
                int askPoints = (int)(((long)spot.Ask + div / 2) / div);
                FeedLive(askSymbol!, askPoints, SpreadTenths(askSymbol!, spreadRaw));
                SymbolBar.SetLastPrice(askSymbol!, askPoints);
            }
            if (hasBidName && spot.HasAsk && symbol == "EURUSD")
            {
                _lastAsk = spot.Ask;
                AskText.Text = FormatPrice(spot.Ask);
            }
            if (hasBidName && symbol == "EURUSD" && _lastBid.HasValue && _lastAsk.HasValue)
            {
                var pips = ((long)_lastAsk.Value - (long)_lastBid.Value) / 10.0;
                SpreadText.Text = pips.ToString("0.0", CultureInfo.InvariantCulture);
            }
        });
    }

    private long TrackQuote(ProtoOASpotEvent spot)
    {
        _lastQuotes.TryGetValue(spot.SymbolId, out var q);
        if (spot.HasBid) q.Bid = (long)spot.Bid;
        if (spot.HasAsk) q.Ask = (long)spot.Ask;
        _lastQuotes[spot.SymbolId] = q;
        if (q.Bid <= 0 || q.Ask <= 0 || q.Ask < q.Bid) return -1;
        return q.Ask - q.Bid;
    }

    private static int SpreadTenths(string symbol, long spreadRaw)
    {
        if (spreadRaw < 0) return -1;
        int div = SymbolPriceDiv(symbol);
        long points = (spreadRaw + div / 2) / div;
        return SpreadCodes.TenthsFromPoints(points, SourcePipPoints(symbol));
    }

    private void BumpLiveSpread(string symbol, int spreadTenths)
    {
        if (spreadTenths < 0) return;
        if (!_live.TryGetValue(symbol, out var s)) return;
        if (s.MinuteUnix == long.MinValue) return;
        if (spreadTenths <= s.MaxSpreadTenths) return;
        s.MaxSpreadTenths = spreadTenths;
        s.Dirty = true;
    }

    private void FeedLive(string symbol, int bidPoints, int spreadTenths = -1)
    {
        if (!_live.TryGetValue(symbol, out var s)) return;
        Chart.SetLastTick(symbol, TransformLivePoint(s, bidPoints
            + AskViewRule.TickShift(symbol, spreadTenths >= 0 ? spreadTenths : s.MaxSpreadTenths)));
        s.Dirty = true;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long minute = now - now % 60;
        if (minute <= s.BaseLastUnix) return;
        if (minute == s.MinuteUnix)
        {
            if (bidPoints < s.Low) s.Low = bidPoints;
            if (bidPoints > s.High) s.High = bidPoints;
            s.Close = bidPoints;
        }
        else if (minute > s.MinuteUnix)
        {
            if (s.MinuteUnix != long.MinValue)
            {
                int avg = (int)Math.Round(((long)s.Open + s.High + s.Low + s.Close) / 4.0,
                    MidpointRounding.AwayFromZero);
                bool hasSpread = s.MaxSpreadTenths >= 0;
                int code = hasSpread ? SpreadCodes.FromTenths(s.MaxSpreadTenths) : 0;
                s.Closed.Add(new Candle(s.MinuteUnix, s.Low, s.High, avg, true, hasSpread, code));
                GetLiveDb().RecordMinute(symbol, s.MinuteUnix, s.Low, s.High, avg,
                    hasSpread ? code : SpreadCodes.Keep);
            }
            s.MaxSpreadTenths = -1;
            s.MinuteUnix = minute;
            s.Open = bidPoints;
            s.Low = bidPoints;
            s.High = bidPoints;
            s.Close = bidPoints;
        }
        else
        {
            return;
        }
        if (spreadTenths > s.MaxSpreadTenths) s.MaxSpreadTenths = spreadTenths;
        s.Dirty = true;
    }

    private void FlushLive()
    {
        foreach (var (symbol, s) in _live)
        {
            if (!s.Dirty) continue;
            s.Dirty = false;
            PushLiveTail(symbol, s);
        }
    }

    private void PushLiveTail(string symbol, LiveState s)
    {
        bool hasCurrent = s.MinuteUnix != long.MinValue;
        var tail = new List<Candle>(s.Closed.Count + 1);
        var hidden = new List<SpreadMark>();
        foreach (var c in s.Closed)
        {
            if (WideSpreadRule.Hidden(symbol, c.MinuteUnixSeconds, c.HasSpread, c.SpreadCode))
            {
                if (c.HasSpread) hidden.Add(new SpreadMark(c.MinuteUnixSeconds, c.SpreadTenths));
                continue;
            }
            if (!AskViewRule.TryShift(symbol, c.HasSpread, c.SpreadCode, out int ask)) continue;
            tail.Add(MakeLiveCandle(s, c.MinuteUnixSeconds, c.Min + ask, c.Max + ask, c.Avg + ask,
                c.HasSpread, c.SpreadCode, c.HasVolume, c.Volume));
        }
        if (hasCurrent)
        {
            bool hasSpread = s.MaxSpreadTenths >= 0;
            int code = hasSpread ? SpreadCodes.FromTenths(s.MaxSpreadTenths) : 0;
            if (WideSpreadRule.Hidden(symbol, s.MinuteUnix, hasSpread, code))
            {
                if (hasSpread) hidden.Add(new SpreadMark(s.MinuteUnix, SpreadCodes.ToTenths(code)));
            }
            else if (AskViewRule.TryShift(symbol, hasSpread, code, out int ask))
                tail.Add(MakeLiveCandle(s, s.MinuteUnix, s.Low + ask, s.High + ask, s.Close + ask,
                    hasSpread, code));
        }
        Chart.SetLiveTail(symbol, tail.ToArray(), hidden.ToArray());
        PushShiftLiveTails(symbol, s);
    }

    private void PushShiftLiveTails(string targetSymbol, LiveState s)
    {
        foreach (var ind in _config.Indicators)
        {
            if (!IndicatorTypes.IsShift(ind.Type)) continue;
            if (!SymbolNameEquals(ind.ShiftTarget(), targetSymbol)) continue;
            var series = Chart.GetSeries(ind.Name);
            if (series?.Transform == null) continue;
            long delta = ShiftedSymbol.VirtualDelta(ind.SourceTimeUnix, ind.ChartTimeUnix);
            bool hasCurrent = s.MinuteUnix != long.MinValue;
            var tail = new List<Candle>(s.Closed.Count + 1);
            foreach (var c in s.Closed)
            {
                if (!AskViewRule.TryShift(targetSymbol, c.HasSpread, c.SpreadCode, out int ask)) continue;
                tail.Add(MakeShiftLiveCandle(series.Transform, delta, c.MinuteUnixSeconds,
                    c.Min + ask, c.Max + ask, c.Avg + ask));
            }
            if (hasCurrent)
            {
                bool hasSpread = s.MaxSpreadTenths >= 0;
                int code = hasSpread ? SpreadCodes.FromTenths(s.MaxSpreadTenths) : 0;
                if (AskViewRule.TryShift(targetSymbol, hasSpread, code, out int ask))
                    tail.Add(MakeShiftLiveCandle(series.Transform, delta, s.MinuteUnix,
                        s.Low + ask, s.High + ask, s.Close + ask));
            }
            Chart.SetLiveTail(ind.Name, tail.ToArray());
        }
    }

    private void RefreshShiftLiveTails(string targetSymbol)
    {
        foreach (var (symbol, s) in _live)
            if (SymbolNameEquals(symbol, targetSymbol))
                PushShiftLiveTails(symbol, s);
    }

    private static Candle MakeShiftLiveCandle(SeriesTransform transform, long virtualDelta,
        long minute, int rawLow, int rawHigh, int rawClose)
    {
        var w = WeekendCompressor.Instance;
        int lo = transform.ToDisplay(rawLow);
        int hi = transform.ToDisplay(rawHigh);
        int close = transform.ToDisplay(rawClose);
        return new Candle(w.ToReal(w.ToVirtual(minute) + virtualDelta),
            Math.Min(lo, hi), Math.Max(lo, hi), close, true);
    }

    private static Candle MakeLiveCandle(LiveState s, long minute, int rawLow, int rawHigh, int rawClose,
        bool hasSpread = false, int spreadCode = 0, bool hasVolume = false, int volume = 0)
    {
        int lo = TransformLivePoint(s, rawLow);
        int hi = TransformLivePoint(s, rawHigh);
        int close = TransformLivePoint(s, rawClose);
        return new Candle(minute, Math.Min(lo, hi), Math.Max(lo, hi), close, true, hasSpread, spreadCode,
            hasVolume, volume);
    }

    private static int TransformLivePoint(LiveState s, int raw)
    {
        long scaled = s.PipPoints == 10 ? raw : CandleTransforms.ScalePoints(raw, s.PipPoints);
        return (int)(s.MirrorBase == 0 ? scaled : s.MirrorBase - scaled);
    }

    private void ReconcileLive()
    {
        foreach (var (symbol, bi) in _baseInfo)
        {
            if (_live.ContainsKey(symbol)) continue;
            if (!_symbolIds.ContainsKey(symbol)) continue;
            if (_idToSymbol.Count == 0 && _idToAskSymbol.Count == 0) continue;
            _live[symbol] = new LiveState
            {
                BaseLastUnix = bi.LastUnix,
                MirrorBase = bi.MirrorBase,
                PipPoints = bi.PipPoints,
            };
        }
        foreach (var (symbol, s) in _live)
        {
            if (_baseInfo.TryGetValue(symbol, out var bi))
            {
                s.BaseLastUnix = bi.LastUnix;
                s.MirrorBase = bi.MirrorBase;
                s.PipPoints = bi.PipPoints;
            }
            s.Closed.RemoveAll(c => c.MinuteUnixSeconds <= s.BaseLastUnix);
            if (s.MinuteUnix != long.MinValue && s.MinuteUnix <= s.BaseLastUnix)
                s.MinuteUnix = long.MinValue;
            PushLiveTail(symbol, s);
        }
    }

    private void StopLive()
    {
        _liveFlushTimer.Stop();
        _liveRepairTimer.Stop();
        _repairCts?.Cancel();
        foreach (var symbol in _live.Keys)
        {
            Chart.ClearLastTick(symbol);
            Chart.SetLiveTail(symbol, Array.Empty<Candle>());
        }
        foreach (var ind in _config.Indicators)
            if (IndicatorTypes.IsShift(ind.Type))
                Chart.SetLiveTail(ind.Name, Array.Empty<Candle>());
        _live.Clear();
        _idToSymbol.Clear();
        _idToAskSymbol.Clear();
        _lastQuotes.Clear();
        _firstSpotLogged = false;
    }

    private static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static int PipDigits(int pipPoints)
    {
        int digits = 5;
        for (int p = pipPoints; p >= 10; p /= 10) digits--;
        return digits;
    }

    private double[]? ToTruePrices(double[]? prices)
    {
        if (prices == null) return null;
        var transforms = _seriesTransforms;
        for (int i = 0; i < prices.Length && i < transforms.Length; i++)
        {
            var (symbol, mirrorBase, pipPoints) = transforms[i];
            if (_spreadSeriesKeys.Contains(IndicatorSymbol.NameKey(symbol))
                || _volumeSeriesKeys.Contains(IndicatorSymbol.NameKey(symbol))) continue;
            double drawn = mirrorBase == 0 ? prices[i] : mirrorBase - prices[i];
            prices[i] = drawn * pipPoints / 10.0;
        }
        return prices;
    }

    private static string FormatPrice(ulong points) =>
        (points / 100000m).ToString("0.00000", CultureInfo.InvariantCulture);

    private void RenameChartStateKeys(string oldName, string newName)
    {
        if (oldName == newName) return;
        Chart.RenameSeriesKeys(oldName, newName);
        var states = new List<ChartViewState?> { _pendingChartState };
        foreach (var tab in _config.Tabs)
        {
            states.Add(tab.State);
            foreach (var placement in tab.Shifts)
                if (SymbolNameEquals(placement.Name, oldName)) placement.Name = newName;
        }
        foreach (var note in NoteList)
        {
            states.Add(note.State);
            foreach (var placement in note.Shifts)
                if (SymbolNameEquals(placement.Name, oldName)) placement.Name = newName;
            note.RenameSymbol(oldName, newName);
        }
        SaveNotes();
        foreach (var state in states)
        {
            if (state == null) continue;
            if (state.SymbolOffsetPoints != null
                && state.SymbolOffsetPoints.Remove(oldName, out var offset))
                state.SymbolOffsetPoints[newName] = offset;
            int i = state.HiddenSymbols?.IndexOf(oldName) ?? -1;
            if (i >= 0) state.HiddenSymbols![i] = newName;
            if (state.FlattenSymbol == oldName) state.FlattenSymbol = newName;
        }
    }

    private void SaveChartState()
    {
        var state = _pendingChartState;
        if (state == null && !_gameDirty) return;
        if (state != null)
        {
            _pendingChartState = null;
            _activeTab.State = state;
        }
        _gameDirty = false;
        _config.Save();
    }

    private void RefreshZoomLevels() =>
        ZoomLevels.SetLevels(_config.ZoomLevels, Chart.ZoomLevelIndex, Chart.ZoomLevelDirty,
            Chart.CurrentZoom(), Chart.FitPixelsPerDay, Chart.CustomZoom);

    private void RefreshZoomLevelState() =>
        ZoomLevels.SetCurrent(Chart.ZoomLevelIndex, Chart.ZoomLevelDirty, Chart.CurrentZoom(),
            Chart.FitPixelsPerDay, Chart.CustomZoom);

    private void CommitZoomLevels(bool changed)
    {
        RefreshZoomLevels();
        if (changed) _config.Save();
    }

    private void HideZoomLevelsUnderMeasureLabel(Rect? label) =>
        ZoomLevels.Visibility = label is { } rect && ZoomLevelsBounds() is { } zoom
            && rect.IntersectsWith(zoom)
            ? Visibility.Hidden
            : Visibility.Visible;

    private Rect? ZoomLevelsBounds()
    {
        if (ZoomLevels.ActualWidth <= 0 || ZoomLevels.ActualHeight <= 0) return null;
        if (!ZoomLevels.IsDescendantOf(ChartPanel) || !Chart.IsDescendantOf(ChartPanel)) return null;
        var origin = ZoomLevels.TranslatePoint(new Point(0, 0), Chart);
        return new Rect(origin, new Size(ZoomLevels.ActualWidth, ZoomLevels.ActualHeight));
    }

    private ChartTab _activeTab = null!;
    private bool _tabsReady;
    private readonly TabPropertiesView _tabProperties = new();

    private ChartViewState? ActiveState => _activeTab.State;

    private void BuildTabs()
    {
        ChartPanelHost.Children.Remove(ChartPanel);
        for (int i = 0; i < _config.Tabs.Count; i++)
            Tabs.Items.Insert(i, MakeTabItem(_config.Tabs[i]));
        _activeTab = _config.Tabs[_config.ActiveTab];
        Chart.SetCustomZoom(_activeTab.CustomZoom);
        ApplyShiftPlacements(_activeTab);
        var item = TabItemOf(_activeTab);
        item.Content = ChartPanel;
        Tabs.SelectedItem = item;
        _tabsReady = true;
    }

    private TabItem TabItemOf(ChartTab tab) => (TabItem)Tabs.Items[_config.Tabs.IndexOf(tab)];

    private TabItem MakeTabItem(ChartTab tab)
    {
        var item = new TabItem { Header = tab.Name, Tag = tab };
        var menu = new ContextMenu();
        menu.Items.Add(TabMenuItem("Notes...", () => OpenNotes(tab)));
        menu.Items.Add(new Separator());
        menu.Items.Add(TabMenuItem("Properties...", () => OpenTabProperties(tab, item)));
        menu.Items.Add(TabMenuItem("Rename...", () => RenameTab(tab)));
        menu.Items.Add(TabMenuItem("Duplicate", () => DuplicateTab(tab)));
        menu.Items.Add(new Separator());
        menu.Items.Add(TabMenuItem("Delete", () => DeleteTab(tab)));
        item.ContextMenu = menu;
        return item;
    }

    private void OpenTabProperties(ChartTab tab, TabItem item) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => _tabProperties.Show(item, tab)));

    private void ApplyTabCustomZoom(ChartTab tab, double value)
    {
        tab.CustomZoom = value;
        if (ReferenceEquals(tab, _activeTab)) Chart.ChangeCustomZoom(value);
        _config.Save();
    }

    private static MenuItem TabMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_tabsReady || !ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (ReferenceEquals(Tabs.SelectedItem, NewTabButton))
        {
            AddTab();
            return;
        }
        if (Tabs.SelectedItem is not TabItem item || item.Tag is not ChartTab tab) return;
        if (ReferenceEquals(tab, _activeTab)) return;
        ActivateTab(tab, item);
    }

    private void NewTabButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AddTab();
    }

    private void ActivateTab(ChartTab tab, TabItem item)
    {
        _stateSaveTimer.Stop();
        SaveChartState();
        TabItemOf(_activeTab).Content = null;
        _activeTab = tab;
        _config.ActiveTab = _config.Tabs.IndexOf(tab);
        item.Content = ChartPanel;
        Chart.SetCustomZoom(tab.CustomZoom);
        var shiftChanges = new List<ShiftReapply>();
        ApplyShiftPlacements(tab, shiftChanges);
        ApplyTabDrawings(tab);
        if (tab.State != null) Chart.RestoreState(tab.State);
        SymbolBar.SetFlattenRow(tab.State?.FlattenSymbol != null);
        SyncSymbolBar();
        RefreshGame();
        RefreshZoomLevelState();
        _config.Save();
        _notesWindow?.Refresh();
        if (shiftChanges.Count == 0) return;
        if (_dbBusy || _historyCts != null)
        {
            AppendLog($"{tab.Name}: shift settings are applied after the running DB operation");
            return;
        }
        QueueShiftReapply(shiftChanges, tab.Name);
    }

    private void SyncSymbolBar()
    {
        SymbolBar.SetCollapsedSources(Chart.CollapsedSources);
        foreach (var (symbol, _, _) in _seriesTransforms)
            SymbolBar.SetSymbolEnabled(symbol, !Chart.HiddenSymbols.Contains(symbol));
        ChartTools.SetCalendarRow(_calendarEntries.Length > 0, Chart.CalendarVisible);
            ChartTools.SetForecastRow(Chart.HasForecasts, Chart.ForecastVisible);
        ChartTools.SetWeekendsRow(Chart.WeekendsHidden);
        ChartTools.SetSessionsRow(Chart.SessionsVisible);
        ChartTools.SetCommentsRow(Chart.CommentsVisible);
        SymbolBar.SetTiltedGridRow(Chart.TiltedUpGridIndex, Chart.TiltedDownGridIndex);
    }

    private void AddTab()
    {
        _stateSaveTimer.Stop();
        SaveChartState();
        InsertTab(_activeTab.Clone(), _config.Tabs.Count);
    }

    private void DuplicateTab(ChartTab tab)
    {
        _stateSaveTimer.Stop();
        SaveChartState();
        int index = _config.Tabs.IndexOf(tab);
        if (index < 0) return;
        InsertTab(tab.Clone(), index + 1);
    }

    private void InsertTab(ChartTab tab, int index)
    {
        tab.Name = NextTabName();
        _config.Tabs.Insert(index, tab);
        var item = MakeTabItem(tab);
        Tabs.Items.Insert(index, item);
        _config.Save();
        Tabs.SelectedItem = item;
    }

    private string NextTabName()
    {
        for (int i = 2; ; i++)
        {
            var name = $"{AppConfig.DefaultTabName} {i}";
            if (!_config.Tabs.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private void RenameTab(ChartTab tab)
    {
        if (_config.Tabs.IndexOf(tab) < 0) return;
        var dlg = new TabNameWindow(tab.Name) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        tab.Name = dlg.TabName;
        TabItemOf(tab).Header = tab.Name;
        _tabProperties.Hide(tab);
        _config.Save();
    }

    private void DeleteTab(ChartTab tab)
    {
        if (_config.Tabs.Count <= 1)
        {
            AppendLog("The last chart tab cannot be deleted");
            return;
        }
        if (MessageBox.Show(this, $"Delete tab {tab.Name}?", "Delete tab",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int index = _config.Tabs.IndexOf(tab);
        if (index < 0) return;
        _tabProperties.Hide(tab);
        var item = TabItemOf(tab);
        if (ReferenceEquals(tab, _activeTab))
            Tabs.SelectedItem = Tabs.Items[index + 1 < _config.Tabs.Count ? index + 1 : index - 1];
        _config.Tabs.RemoveAt(index);
        Tabs.Items.Remove(item);
        _config.ActiveTab = _config.Tabs.IndexOf(_activeTab);
        _config.Save();
    }

    private sealed record ShiftReapply(string Name, long OldDelta, long NewDelta, bool Retarget);

    private bool ApplyShiftPlacements(ChartTab tab, List<ShiftReapply>? changes = null)
    {
        bool changed = false;
        var known = new HashSet<string>();
        foreach (var ind in _config.Indicators)
        {
            if (!IndicatorTypes.IsShift(ind.Type)) continue;
            known.Add(IndicatorSymbol.NameKey(ind.Name));
            var placement = tab.Shifts.FirstOrDefault(x => SymbolNameEquals(x.Name, ind.Name));
            if (placement == null)
            {
                tab.Shifts.Add(ShiftPlacement.From(ind));
                continue;
            }
            if (placement.Matches(ind)) continue;
            bool newTarget = !SymbolNameEquals(placement.Target(), ind.ShiftTarget());
            bool retarget = newTarget || placement.Flip != ind.Flip;
            if (newTarget) ClearMirrorBase(ind.Name);
            long oldDelta = ShiftedSymbol.VirtualDelta(ind.SourceTimeUnix, ind.ChartTimeUnix);
            placement.ApplyTo(ind);
            changes?.Add(new ShiftReapply(ind.Name, oldDelta,
                ShiftedSymbol.VirtualDelta(ind.SourceTimeUnix, ind.ChartTimeUnix), retarget));
            changed = true;
        }
        tab.Shifts.RemoveAll(x => !known.Contains(IndicatorSymbol.NameKey(x.Name)));
        return changed;
    }

    private Task _shiftReapply = Task.CompletedTask;

    private void QueueShiftReapply(List<ShiftReapply> changes, string tabName)
    {
        var previous = _shiftReapply;
        _shiftReapply = RunAsync();
        return;

        async Task RunAsync()
        {
            try { await previous; }
            catch { }
            await ReapplyShiftsAsync(changes, tabName);
        }
    }

    private async Task ReapplyShiftsAsync(List<ShiftReapply> changes, string tabName)
    {
        var pendingLoad = _chartLoadTask;
        if (pendingLoad != null)
        {
            try { await pendingLoad; }
            catch { }
        }
        var sw = Stopwatch.StartNew();
        var fallback = new List<string>();
        foreach (var change in changes)
        {
            try
            {
                if (!await ReapplyShiftAsync(change)) fallback.Add(change.Name);
            }
            catch (Exception ex)
            {
                AppendLog($"{change.Name}: shift not re-applied ({ex.Message})");
                fallback.Add(change.Name);
            }
        }
        if (fallback.Count == 0)
        {
            AppendLog($"{tabName}: {changes.Count} shift(s) re-applied in {sw.ElapsedMilliseconds} ms");
            return;
        }
        AppendLog($"{tabName}: full chart reload for {string.Join(", ", fallback)}");
        if (_dbBusy || _historyCts != null) return;
        await LoadChartAsync();
    }

    private async Task<bool> ReapplyShiftAsync(ShiftReapply change)
    {
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, change.Name));
        if (ind == null || !IndicatorTypes.IsShift(ind.Type)) return true;
        if (Chart.GetSeries(ind.Name) == null) return false;
        var loader = _loader;
        var target = ind.ShiftTarget();
        bool pauseTarget = change.Retarget && !SymbolNameEquals(target, ind.Name);
        if (loader != null)
        {
            await loader.PauseAsync(ind.Name);
            if (pauseTarget) await loader.PauseAsync(target);
        }
        try
        {
            return change.Retarget
                ? await RetargetShiftAsync(ind, change.NewDelta, loader)
                : await RestampShiftAsync(ind, change.NewDelta - change.OldDelta, loader);
        }
        finally
        {
            loader?.Resume(ind.Name);
            if (pauseTarget) loader?.Resume(target);
        }
    }

    private async Task<bool> RestampShiftAsync(IndicatorSymbol ind, long step, SeriesDataLoader? loader)
    {
        var series = Chart.GetSeries(ind.Name);
        if (series == null) return false;
        var old = series.History;
        if (step != 0 && old.Minutes.Length > 0)
        {
            var history = await Task.Run(() =>
                CandleHistory.Build(ShiftedSymbol.Restamp(old.Minutes, step)));
            if (!ReferenceEquals(Chart.GetSeries(ind.Name)?.History, old)) return false;
            history.SetHiddenSpreads(RestampMarks(old.HiddenSpreads, step));
            Chart.ReplaceSeries(ind.Name, history);
        }
        loader?.SetShiftDelta(ind.Name,
            ShiftedSymbol.VirtualDelta(ind.SourceTimeUnix, ind.ChartTimeUnix));
        RefreshShiftLiveTails(ind.ShiftTarget());
        return true;
    }

    private async Task<bool> RetargetShiftAsync(IndicatorSymbol ind, long delta,
        SeriesDataLoader? loader)
    {
        var target = ind.ShiftTarget();
        var tgt = Chart.GetSeries(target);
        if (tgt?.Transform == null) return false;
        if (PairDisplay(target) is not { } pair) return false;
        if (pair.PipPoints != tgt.Transform.PipPoints) return false;
        bool mirror = pair.Mirror ^ ind.Flip;
        long? fixedBase = mirror ? StoredMirrorBase(ind.Name) : null;
        var minutes = tgt.History.Minutes;
        var sourceTransform = tgt.Transform;
        var sourceHidden = tgt.History.HiddenSpreads;
        var (history, count, mirrorBase) = await Task.Run(() =>
        {
            var retargeted = ShiftedSymbol.Retarget(
                minutes, sourceTransform, mirror, fixedBase, out long computed);
            var moved = ShiftedSymbol.Shift(retargeted, delta).ToArray();
            if (moved.Length == 0) return ((CandleHistory?)null, 0, computed);
            var built = CandleHistory.Build(moved);
            built.SetHiddenSpreads(RestampMarks(sourceHidden, delta));
            return ((CandleHistory?)built, moved.Length, computed);
        });
        if (history == null)
            return Chart.GetSeries(ind.Name)?.History.Minutes.Length == 0;
        var transform = new SeriesTransform(mirror && mirrorBase != 0, mirrorBase, pair.PipPoints);
        Chart.ReplaceSeries(ind.Name, history, transform);
        UpdateSeriesTransform(ind.Name, transform.Mirror ? mirrorBase : 0, pair.PipPoints);
        if (mirror && fixedBase == null && mirrorBase != 0) OnMirrorBaseComputed(ind.Name, mirrorBase);
        loader?.MarkShiftRetargeted(ind.Name, target, delta, mirror, mirrorBase, pair.PipPoints);
        RefreshShiftLiveTails(target);
        AppendLog($"{ind.Name}: rebuilt from {target} in memory, {count:N0} candles");
        return true;
    }

    private long? StoredMirrorBase(string symbol)
    {
        if (_config.MirrorBases == null) return null;
        foreach (var (key, value) in _config.MirrorBases)
            if (SymbolNameEquals(key, symbol)) return value;
        return null;
    }

    private static SpreadMark[] RestampMarks(SpreadMark[] marks, long virtualDelta)
    {
        if (marks.Length == 0) return marks;
        var w = WeekendCompressor.Instance;
        var result = new SpreadMark[marks.Length];
        for (int i = 0; i < marks.Length; i++)
            result[i] = marks[i] with
            {
                UnixSeconds = w.ToReal(w.ToVirtual(marks[i].UnixSeconds) + virtualDelta),
            };
        return result;
    }

    private void RecordShiftPlacements()
    {
        _activeTab.Shifts.Clear();
        foreach (var ind in _config.Indicators)
            if (IndicatorTypes.IsShift(ind.Type)) _activeTab.Shifts.Add(ShiftPlacement.From(ind));
    }

    private List<Note>? _notes;
    private NotesWindow? _notesWindow;
    private string _appliedNoteId = "";

    private List<Note> NoteList => _notes ??= NotesStore.Load();

    public IReadOnlyList<Note> Notes => NoteList;

    public string ActiveNoteId => _activeTab.NoteId;

    public string ActiveTabName => _activeTab.Name;

    private Note? NoteById(string id) =>
        id.Length == 0 ? null : NoteList.FirstOrDefault(x => x.Id == id);

    private Note? ActiveNote() => NoteById(_activeTab.NoteId);

    private void SaveNotes()
    {
        try { NotesStore.Save(NoteList); }
        catch (Exception ex) { AppendLog("Notes save failed: " + ex.Message); }
    }

    private void OpenNotes(ChartTab tab)
    {
        if (!ReferenceEquals(tab, _activeTab) && _config.Tabs.Contains(tab))
            Tabs.SelectedItem = TabItemOf(tab);
        if (_notesWindow is { IsLoaded: true })
        {
            _notesWindow.Activate();
            return;
        }
        var win = new NotesWindow(this) { Owner = this };
        win.Closed += (_, _) => _notesWindow = null;
        _notesWindow = win;
        win.Show();
    }

    public string NextNoteName()
    {
        for (int i = 1; ; i++)
        {
            var name = $"Note {i}";
            if (!NoteList.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    public string CreateNote(string name)
    {
        _stateSaveTimer.Stop();
        SaveChartState();
        var note = new Note { Id = Note.NewId(), Name = name };
        FillNote(note);
        NoteList.Add(note);
        SaveNotes();
        AppendLog($"Note {name} created from {_activeTab.Name}");
        return note.Id;
    }

    public void UpdateNote(string id)
    {
        var note = NoteById(id);
        if (note == null) return;
        _stateSaveTimer.Stop();
        SaveChartState();
        FillNote(note);
        SaveNotes();
        AppendLog($"Note {note.Name} updated from {_activeTab.Name}");
    }

    public void RenameNote(string id, string name)
    {
        var note = NoteById(id);
        if (note == null) return;
        note.Name = name;
        SaveNotes();
    }

    public void DeleteNote(string id)
    {
        var note = NoteById(id);
        if (note == null) return;
        NoteList.Remove(note);
        SaveNotes();
        foreach (var tab in _config.Tabs)
            if (tab.NoteId == id) tab.NoteId = "";
        _config.Save();
        ApplyTabDrawings(_activeTab);
        AppendLog($"Note {note.Name} deleted");
    }

    public void OpenNote(string id)
    {
        var note = NoteById(id);
        if (note == null) return;
        _stateSaveTimer.Stop();
        SaveChartState();
        _activeTab.NoteId = note.Id;
        _activeTab.State = note.State?.Clone();
        _activeTab.Shifts = note.Shifts.Select(x => x.Clone()).ToList();
        var shiftChanges = new List<ShiftReapply>();
        ApplyShiftPlacements(_activeTab, shiftChanges);
        ApplyTabDrawings(_activeTab);
        if (_activeTab.State != null) Chart.RestoreState(_activeTab.State);
        SymbolBar.SetFlattenRow(_activeTab.State?.FlattenSymbol != null);
        SyncSymbolBar();
        _config.Save();
        AppendLog($"{_activeTab.Name}: note {note.Name} opened");
        if (shiftChanges.Count == 0) return;
        if (_dbBusy || _historyCts != null)
        {
            AppendLog($"{_activeTab.Name}: shift settings are applied after the running DB operation");
            return;
        }
        QueueShiftReapply(shiftChanges, _activeTab.Name);
    }

    private void FillNote(Note note)
    {
        note.State = _activeTab.State?.Clone();
        note.Shifts = _activeTab.Shifts.Select(x => x.Clone()).ToList();
        note.Drawings.Clear();
        foreach (var ind in _config.Indicators)
        {
            if (!IndicatorTypes.IsDrawing(ind.Type)) continue;
            note.Drawings[ind.Name] =
                DrawingStore.ToRaw(Chart.GetSeries(ind.Name)?.DrawingLines ?? LiveDrawing(ind.Name));
        }
        var range = Chart.VisibleRealRange();
        if (range == null) return;
        note.StartUnix = range.Value.Lo;
        note.EndUnix = range.Value.Hi;
    }

    private void ApplyTabDrawings(ChartTab tab)
    {
        var note = NoteById(tab.NoteId);
        if (tab.NoteId.Length > 0 && note == null) tab.NoteId = "";
        if (tab.NoteId == _appliedNoteId) return;
        var lines = new Dictionary<string, PivotPoint[][]>();
        foreach (var ind in _config.Indicators)
        {
            if (!IndicatorTypes.IsDrawing(ind.Type)) continue;
            lines[ind.Name] = note?.LinesOf(ind.Name) ?? LiveDrawing(ind.Name);
        }
        _appliedNoteId = tab.NoteId;
        Chart.ReplaceDrawings(lines);
    }

    private PivotPoint[][] LiveDrawing(string symbol)
    {
        try { return DrawingStore.Load(GetDb().SymbolDirectory(symbol)); }
        catch { return Array.Empty<PivotPoint[]>(); }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetConnState(ConnState state)
    {
        _connState = state;
        (int color, string text) = state switch
        {
            ConnState.Online => (unchecked((int)0xFF2E8B57), $"Online ({_live.Count})"),
            ConnState.Connecting => (unchecked((int)0xFFFF8C00), "Connecting"),
            ConnState.Downloading => (unchecked((int)0xFFFF8C00), "Downloading"),
            _ => (unchecked((int)0xFFB22222), "Offline"),
        };
        ChartTools.SetConnStatus(text, color);
    }

    private const int LogBoxMaxLines = 2000;
    private const int LogBoxTrimLines = 500;

    private string _lastLogMessage = "";
    private int _repeatedLogCount;

    private void AppendLog(string message)
    {
        using var _ = Perf.Step(Perf.LogName);
        if (message == _lastLogMessage)
        {
            _repeatedLogCount++;
            return;
        }
        FlushRepeatedLog();
        _lastLogMessage = message;
        WriteLog(message);
    }

    private void FlushRepeatedLog()
    {
        if (_repeatedLogCount == 0) return;
        int repeats = _repeatedLogCount;
        _repeatedLogCount = 0;
        WriteLog($"... previous line repeated {repeats} more time(s)");
    }

    private void WriteLog(string message)
    {
        LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        TrimLogBox();
        LogBox.ScrollToEnd();
        try { File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine); }
        catch { }
    }

    private void TrimLogBox()
    {
        int lines = LogBox.LineCount;
        if (lines < 0 || lines <= LogBoxMaxLines) return;
        int cut = LogBox.GetCharacterIndexFromLineIndex(lines - LogBoxMaxLines + LogBoxTrimLines);
        if (cut > 0 && cut < LogBox.Text.Length) LogBox.Text = LogBox.Text[cut..];
    }

    private async void ImportDbBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        var histDir = Path.Combine(AppConfig.Dir, "history");
        var csvFiles = Directory.Exists(histDir)
            ? Directory.GetFiles(histDir, "*.csv").OrderBy(File.GetLastWriteTimeUtc).ToArray()
            : Array.Empty<string>();
        if (csvFiles.Length == 0)
        {
            AppendLog("No history CSV found in " + histDir);
            return;
        }
        _dbBusy = true;
        try
        {
            var db = GetDb();
            AppendLog("DB folder: " + DbRoot);
            foreach (var csv in csvFiles)
            {
                var name = Path.GetFileName(csv);
                var symbol = Path.GetFileNameWithoutExtension(csv).Split('_')[0].ToUpperInvariant();
                if (!SymbolConfigs.Any(c => c.Symbol == symbol))
                {
                    AppendLog($"Skipping {name}: unknown symbol '{symbol}'");
                    continue;
                }
                AppendLog($"Importing {name} into DB as {symbol}...");
                var written = await Task.Run(() => HistoryImporter.ImportOhlcCsv(
                    db, symbol, csv, m => Dispatcher.BeginInvoke(() => AppendLog(m)),
                    priceDiv: SymbolPriceDiv(symbol)));
                AppendLog($"Import done: {written} minutes written to DB");
            }
            db.FlushAll();
            await LoadChartAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Import failed: " + ex.Message);
        }
        finally
        {
            _dbBusy = false;
        }
    }

    private void ReloadForecasts()
    {
        var marks = LoadForecastMarks();
        Chart.SetForecasts(marks);
        ChartTools.SetForecastRow(Chart.HasForecasts, Chart.ForecastVisible);
        AppendLog(marks.Length == 0
            ? $"Forecasts: nothing to show from {ForecastFolder}"
            : $"Forecasts: {marks.Length} level(s) from {ForecastFolder}, showing day {Chart.ForecastDay}");
    }

    private async Task LoadCalendarEntriesAsync()
    {
        try
        {
            var store = GetCalendar();
            _calendarSummaries = null;
            _calendarEntries = await Task.Run(() =>
            {
                store.MigrateLegacy(m => Dispatcher.BeginInvoke(() => AppendLog(m)));
                store.UpgradeOutdatedIndexes(m => Dispatcher.BeginInvoke(() => AppendLog(m)));
                return store.Load();
            });
            Chart.SetCalendar(_calendarEntries);
            ChartTools.SetCalendarRow(_calendarEntries.Length > 0, Chart.CalendarVisible);
            ChartTools.SetForecastRow(Chart.HasForecasts, Chart.ForecastVisible);
            AppendLog($"Calendar: {_calendarEntries.Length:N0} tracked events loaded");
        }
        catch (Exception ex)
        {
            AppendLog("Calendar load failed: " + ex.Message);
        }
    }

    private async void ImportCalendarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_calendarCts != null)
        {
            AppendLog("Calendar operation already running");
            return;
        }
        _dbBusy = true;
        _calendarCts = new CancellationTokenSource();
        var ct = _calendarCts.Token;
        try
        {
            var dir = Path.Combine(AppConfig.Dir, "calendar-import");
            Directory.CreateDirectory(dir);
            var csv = Path.Combine(dir, "forex_factory_cache.csv");
            if (!File.Exists(csv))
            {
                AppendLog("Calendar history CSV not found, downloading ~68 MB from Hugging Face...");
                await CalendarDownloader.DownloadHistoryCsvAsync(
                    csv, m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct);
            }
            AppendLog("Importing calendar history into store...");
            var store = GetCalendar();
            int tracked = await Task.Run(() =>
            {
                using var reader = new StreamReader(csv);
                var events = ForexFactoryParser.ReadCsv(reader).ToList();
                return store.Merge(events, m => Dispatcher.BeginInvoke(() => AppendLog(m)));
            }, ct);
            await LoadCalendarEntriesAsync();
            AppendLog($"Calendar import done: {tracked:N0} tracked events indexed");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Calendar import cancelled");
        }
        catch (Exception ex)
        {
            AppendLog("Calendar import failed: " + ex.Message);
        }
        finally
        {
            _calendarCts?.Dispose();
            _calendarCts = null;
            _dbBusy = false;
        }
    }

    private async void DownloadCalendarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_calendarCts != null)
        {
            AppendLog("Calendar operation already running");
            return;
        }
        _dbBusy = true;
        _calendarCts = new CancellationTokenSource();
        try
        {
            AppendLog("Downloading calendar (this week)...");
            await DownloadCalendarWeekAsync(_calendarCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Calendar download cancelled");
        }
        catch (Exception ex)
        {
            AppendLog("Calendar download failed: " + ex.Message);
        }
        finally
        {
            _calendarCts?.Dispose();
            _calendarCts = null;
            _dbBusy = false;
        }
    }

    private async Task DownloadCalendarWeekAsync(CancellationToken ct)
    {
        var fresh = await CalendarDownloader.DownloadWeeklyAsync(
            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct);
        if (fresh.Count == 0)
        {
            AppendLog("No calendar events fetched");
            return;
        }
        var store = GetCalendar();
        int tracked = await Task.Run(() =>
            store.Merge(fresh, m => Dispatcher.BeginInvoke(() => AppendLog(m))), ct);
        await LoadCalendarEntriesAsync();
        AppendLog($"Calendar updated: {fresh.Count} fetched, {tracked:N0} tracked events total");
    }

    private void OpenCalendarSettings()
    {
        var dlg = new CalendarSettingsWindow(_config.Calendar) { Owner = this };
        dlg.SettingsChanged += settings =>
        {
            _config.Calendar = settings;
            _config.Save();
            Chart.SetCalendarSettings(_config.Calendar);
        };
        dlg.ShowDialog();
    }

    private async void OpenAppSettings()
    {
        var pairs = SymbolConfigs
            .Select(c => (c.Symbol, c.ColorArgb, PairColorOf(c.Symbol, c.ColorArgb)))
            .ToArray();
        var dlg = new AppSettingsWindow(pairs, _config.HideWideSpread, _config.ShowAsk,
            _config.CommentSpotDiameterPx, _config.CommentSpotColorArgb,
            _config.CommentSpotOpacityPercent)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;
        bool changed = false;
        foreach (var (symbol, defaultColor, currentColor) in pairs)
        {
            int picked = dlg.ColorOf(symbol);
            if (picked == currentColor) continue;
            changed = true;
            if (picked == defaultColor) _config.PairColors.Remove(symbol);
            else _config.PairColors[symbol] = picked;
            Chart.SetSeriesColor(symbol, picked);
            SymbolBar.SetSymbolColor(symbol, picked);
        }
        bool hideChanged = dlg.HideWideSpread != _config.HideWideSpread;
        if (hideChanged)
        {
            _config.HideWideSpread = dlg.HideWideSpread;
            WideSpreadRule.Hide = _config.HideWideSpread;
            changed = true;
        }
        if (dlg.SpotDiameterPx != _config.CommentSpotDiameterPx
            || dlg.SpotColorArgb != _config.CommentSpotColorArgb
            || dlg.SpotOpacityPercent != _config.CommentSpotOpacityPercent)
        {
            _config.CommentSpotDiameterPx = dlg.SpotDiameterPx;
            _config.CommentSpotColorArgb = dlg.SpotColorArgb;
            _config.CommentSpotOpacityPercent = dlg.SpotOpacityPercent;
            Chart.SetCommentStyle(_config.CommentSpotStyle());
            changed = true;
        }
        bool askChanged = dlg.ShowAsk != _config.ShowAsk;
        if (askChanged)
        {
            _config.ShowAsk = dlg.ShowAsk;
            AskViewRule.Show = _config.ShowAsk;
            UpdateAskTitle();
            changed = true;
        }
        if (changed) _config.Save();
        if (hideChanged)
            AppendLog(_config.HideWideSpread
                ? "Wide spread minutes are hidden, reloading chart"
                : "Wide spread minutes are shown again, reloading chart");
        if (askChanged)
            AppendLog(_config.ShowAsk
                ? "Showing ask (bid plus spread), reloading chart"
                : "Showing bid again, reloading chart");
        if (!hideChanged && !askChanged) return;
        await LoadChartAsync();
    }

    private void UpdateAskTitle() => Title = _config.ShowAsk ? "FXViewer - ASK" : "FXViewer";

    private async Task OpenCalendarFindAsync()
    {
        if (_calendarFindWindow is { IsLoaded: true })
        {
            _calendarFindWindow.Activate();
            return;
        }
        if (_calendarSummaries == null)
        {
            if (_calendarSummariesLoading) return;
            _calendarSummariesLoading = true;
            AppendLog("Calendar: reading event list...");
            try
            {
                var store = GetCalendar();
                _calendarSummaries = await Task.Run(() => store.LoadSummaries());
                AppendLog($"Calendar: {_calendarSummaries.Count:N0} events available for search");
            }
            catch (Exception ex)
            {
                AppendLog("Calendar search list failed: " + ex.Message);
                return;
            }
            finally
            {
                _calendarSummariesLoading = false;
            }
        }
        if (_calendarSummaries.Count == 0)
        {
            AppendLog("Calendar is empty");
            return;
        }
        _calendarFindWindow = new CalendarFindWindow(_calendarSummaries, NavigateToCalendarEvent)
        {
            Owner = this,
        };
        _calendarFindWindow.Closed += (_, _) => _calendarFindWindow = null;
        _calendarFindWindow.Show();
    }

    private (DateTime From, DateTime To) SuggestedBackfillRange()
    {
        var now = DateTime.UtcNow;
        var entries = _calendarEntries;
        if (entries.Length == 0) return (now, now);
        long gapFrom = 0;
        long gapTo = 0;
        long widest = 0;
        for (int i = 1; i < entries.Length; i++)
        {
            long gap = entries[i].UnixSeconds - entries[i - 1].UnixSeconds;
            if (gap <= widest) continue;
            widest = gap;
            gapFrom = entries[i - 1].UnixSeconds;
            gapTo = entries[i].UnixSeconds;
        }
        if (widest >= MinCalendarGapSeconds)
            return (Utc(gapFrom), Utc(gapTo));
        return (Utc(entries[^1].UnixSeconds), now);
    }

    private static DateTime Utc(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

    private void NavigateToCalendarEvent(long unixSeconds)
    {
        var symbol = Chart.FirstVisibleSeries(CalendarCenterSymbol);
        if (!Chart.ShowTimeCentered(unixSeconds, symbol))
            AppendLog("Chart is not ready for navigation");
    }

    private async void BackfillCalendarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_calendarBackfillRunning)
        {
            AppendLog("Stopping calendar backfill...");
            _calendarCts?.Cancel();
            return;
        }
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_calendarCts != null)
        {
            AppendLog("Calendar operation already running");
            return;
        }
        if (_calendarEntries.Length == 0)
        {
            AppendLog("Calendar is empty - run Import calendar history first");
            return;
        }
        var (suggestedFrom, suggestedTo) = SuggestedBackfillRange();
        var dlg = new CalendarBackfillWindow(suggestedFrom, suggestedTo) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var firstWeek = dlg.FirstWeekStart;
        var lastWeek = dlg.LastWeekStart;
        _dbBusy = true;
        _calendarCts = new CancellationTokenSource();
        var ct = _calendarCts.Token;
        var store = GetCalendar();
        var batch = new List<CalendarDetail>();
        int fetched = 0;
        try
        {
            int weeks = (int)((lastWeek - firstWeek).TotalDays / 7) + 1;
            _calendarBackfillRunning = true;
            BackfillCalendarBtn.Content = "Stop backfill";
            AppendLog($"Backfilling calendar from {firstWeek:yyyy-MM-dd} to {lastWeek:yyyy-MM-dd} ({weeks} weeks)...");
            await CalendarDownloader.DownloadWeekRangeAsync(
                firstWeek, lastWeek,
                async (_, events) =>
                {
                    batch.AddRange(events);
                    fetched += events.Count;
                    if (batch.Count < 500) return;
                    var pending = batch.ToList();
                    batch.Clear();
                    await Task.Run(() => store.Merge(pending), CancellationToken.None);
                },
                m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct);
            AppendLog($"Calendar backfill done: {fetched} events fetched");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"Calendar backfill stopped: {fetched} events fetched");
        }
        catch (Exception ex)
        {
            AppendLog("Calendar backfill failed: " + ex.Message);
        }
        finally
        {
            if (batch.Count > 0)
                await Task.Run(() => store.Merge(batch), CancellationToken.None);
            if (_calendarBackfillRunning) await LoadCalendarEntriesAsync();
            _calendarBackfillRunning = false;
            BackfillCalendarBtn.Content = "Backfill calendar gap";
            _calendarCts?.Dispose();
            _calendarCts = null;
            _dbBusy = false;
        }
    }

    private async Task AutoRefreshCalendarAsync()
    {
        if (_calendarCts != null) return;
        _calendarCts = new CancellationTokenSource();
        try
        {
            await DownloadCalendarWeekAsync(_calendarCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog("Calendar auto-update failed: " + ex.Message);
        }
        finally
        {
            _calendarCts?.Dispose();
            _calendarCts = null;
        }
    }

    private async void ComputeDerivedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, wait for it to finish");
            return;
        }
        var indicators = _config.Indicators
            .Where(x => IndicatorTypes.IsIndex(x.Type))
            .Concat(_config.Indicators.Where(x => !IndicatorTypes.IsIndex(x.Type)))
            .ToArray();
        if (indicators.Length == 0)
        {
            AppendLog("No indicators to compute");
            return;
        }
        _dbBusy = true;
        var cts = new CancellationTokenSource();
        _computeCts = cts;
        try
        {
            CancelFind();
            _chartCts?.Cancel();
            StopLoader();
            var pendingLoad = _chartLoadTask;
            if (pendingLoad != null)
            {
                try { await pendingLoad; } catch { }
            }
            var db = GetDb();
            foreach (var ind in indicators)
            {
                if (!IndicatorTypes.HasStorage(ind.Type))
                {
                    AppendLog($"{ind.Name}: {ind.Type.ToLowerInvariant()} symbol, nothing to compute");
                    continue;
                }
                long endUnix = ConfirmedEndUnix(new[] { ind.Source }, 0);
                var (_, minutes) = await Task.Run(
                    () => GenerateIndicatorData(db, ind, cts.Token, null, endUnix), cts.Token);
                AppendLog(IndicatorTypes.IsZigZag(ind.Type)
                    ? $"{ind.Name} rebuilt: {minutes:N0} points"
                    : $"{ind.Name} rebuilt: {minutes:N0} minutes");
            }
            await LoadChartAsync();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Compute cancelled");
        }
        catch (Exception ex)
        {
            AppendLog("Compute failed: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_computeCts, cts)) _computeCts = null;
            cts.Dispose();
            _dbBusy = false;
        }
    }

    private async Task ApplyPivotEditAsync(PivotEditRequest req)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running, edit ignored");
            Chart.ClearHiddenPivotSegments();
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, edit ignored");
            Chart.ClearHiddenPivotSegments();
            return;
        }
        var pendingLoad = _chartLoadTask;
        if (pendingLoad != null && !pendingLoad.IsCompleted)
        {
            AppendLog("Chart load is running, edit ignored");
            Chart.ClearHiddenPivotSegments();
            return;
        }
        var series = Chart.GetSeries(req.Symbol);
        if (series?.Pivots == null || series.Transform == null)
        {
            AppendLog($"Edit failed: series {req.Symbol} is not editable");
            Chart.ClearHiddenPivotSegments();
            return;
        }
        var points = ZigZagSymbol.NormalizeEditedPoints(req.Points).ToArray();
        if (points.Length < 2)
        {
            AppendLog("Edit ignored: less than 2 points left");
            Chart.ClearHiddenPivotSegments();
            return;
        }
        _dbBusy = true;
        Chart.EditLocked = true;
        try
        {
            var dir = GetDb().SymbolDirectory(req.Symbol);
            var sw = Stopwatch.StartNew();
            await Task.Run(() => ZigZagStore.Save(dir, points));
            Chart.ReplacePivots(req.Symbol, points);
            AppendLog($"{req.Symbol}: {points.Length:N0} points saved in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            AppendLog("Edit failed: " + ex.Message);
        }
        finally
        {
            _dbBusy = false;
            Chart.EditLocked = false;
            Chart.ClearHiddenPivotSegments();
        }
    }

    private void DropLegacyZigZagCandles(CandleDatabase db, IReadOnlyList<IndicatorSymbol> indicators)
    {
        foreach (var ind in indicators)
        {
            if (!IndicatorTypes.IsZigZag(ind.Type)) continue;
            if (db.ExistingYears(ind.Name).Count == 0) continue;
            try
            {
                var dir = db.SymbolDirectory(ind.Name);
                var points = ZigZagStore.Load(dir);
                db.DeleteSymbol(ind.Name);
                ZigZagStore.Save(dir, points);
                AppendLog($"{ind.Name}: old ZigZag candle files removed, rebuild the indicator");
            }
            catch (Exception ex)
            {
                AppendLog($"{ind.Name}: cannot remove old ZigZag candles: {ex.Message}");
            }
        }
    }

    private void DropStoredAverageCandles(CandleDatabase db, IReadOnlyList<IndicatorSymbol> indicators)
    {
        foreach (var ind in indicators)
        {
            if (!IndicatorTypes.IsAverage(ind.Type)) continue;
            if (db.ExistingYears(ind.Name).Count == 0) continue;
            try
            {
                db.DeleteSymbol(ind.Name);
                AppendLog($"{ind.Name}: stored average candles removed, the line is computed in memory now");
            }
            catch (Exception ex)
            {
                AppendLog($"{ind.Name}: cannot remove stored average candles: {ex.Message}");
            }
        }
    }

    private void SeedIndicators()
    {
        if (_config.IndicatorsInitialized) return;
        _config.IndicatorsInitialized = true;
        if (_config.Indicators.Count == 0)
        {
            foreach (var (symbol, color, limit) in DerivedSymbolConfigs)
                _config.Indicators.Add(new IndicatorSymbol
                {
                    Name = symbol,
                    Source = "EURUSD",
                    Type = IndicatorTypes.ZigZag,
                    Limit1Pips = limit,
                    ColorArgb = color,
                });
        }
        _config.Save();
    }

    private void OnDrawingCommitted(string symbol, PivotPoint[] points)
    {
        if (TryCommitGameLine(symbol, points)) return;
        if (_dbBusy)
        {
            AppendLog("DB operation already running, line discarded");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, line discarded");
            return;
        }
        var pendingLoad = _chartLoadTask;
        if (pendingLoad != null && !pendingLoad.IsCompleted)
        {
            AppendLog("Chart load is running, line discarded");
            return;
        }
        try
        {
            var s = Chart.GetSeries(symbol);
            if (s?.DrawingLines == null)
            {
                AppendLog($"{symbol}: not a drawing symbol, line discarded");
                return;
            }
            var note = ActiveNote();
            var dir = GetDb().SymbolDirectory(symbol);
            var stored = note != null ? s.DrawingLines : DrawingStore.Load(dir);
            var lines = new PivotPoint[stored.Length + 1][];
            Array.Copy(stored, lines, stored.Length);
            lines[^1] = points;
            if (note != null)
            {
                note.SetLines(symbol, lines);
                SaveNotes();
            }
            else DrawingStore.Save(dir, lines);
            Chart.ReplaceDrawing(symbol, lines);
            AppendLog($"{symbol}: line added ({points.Length} points, {lines.Length} lines total)"
                + (note != null ? $" into note {note.Name}" : ""));
        }
        catch (Exception ex)
        {
            AppendLog("Drawing save failed: " + ex.Message);
        }
    }

    private void OnDrawingLinesChanged(string symbol, PivotPoint[][] lines)
    {
        if (TrySaveGameLines(symbol, lines)) return;
        if (_dbBusy)
        {
            AppendLog("DB operation already running, line edit discarded");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, line edit discarded");
            return;
        }
        var pendingLoad = _chartLoadTask;
        if (pendingLoad != null && !pendingLoad.IsCompleted)
        {
            AppendLog("Chart load is running, line edit discarded");
            return;
        }
        try
        {
            var note = ActiveNote();
            if (note != null)
            {
                note.SetLines(symbol, lines);
                SaveNotes();
                Chart.ReplaceDrawing(symbol, lines);
                AppendLog($"{symbol}: drawing saved into note {note.Name} ({lines.Length} lines)");
                return;
            }
            DrawingStore.Save(GetDb().SymbolDirectory(symbol), lines);
            Chart.ReplaceDrawing(symbol, lines);
            AppendLog($"{symbol}: drawing saved ({lines.Length} lines)");
        }
        catch (Exception ex)
        {
            AppendLog("Drawing save failed: " + ex.Message);
        }
    }

    private void OpenSymbolEditor(string? sourcePrefill, string? editName)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, wait for it to finish");
            return;
        }
        IndicatorSymbol? editing = editName == null
            ? null
            : _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, editName));
        if (editName != null && editing == null)
        {
            AppendLog($"Indicator {editName} not found");
            return;
        }
        var indexSources = _config.Indicators
            .Where(x => IsStandaloneIndicator(x) && !SymbolNameEquals(x.Name, editName ?? ""))
            .Select(x => x.Name)
            .ToList();
        var zigzagSources = _config.Indicators
            .Where(x => IndicatorTypes.IsZigZag(x.Type) && !SymbolNameEquals(x.Name, editName ?? ""))
            .Select(x => x.Name)
            .ToList();
        var sources = SymbolConfigs.Select(c => c.Symbol).Concat(indexSources).ToList();
        var names = new List<string>();
        foreach (var c in SymbolConfigs) names.Add(c.Symbol);
        foreach (var ind in _config.Indicators) names.Add(ind.Name);
        Func<IProgress<double>, CancellationToken, Task>? refresh =
            editing == null || !IndicatorTypes.HasStorage(editing.Type)
                || IndicatorTypes.IsZigZag(editing.Type)
                ? null
                : (progress, ct) => RefreshIndicatorAsync(editing, progress, ct);
        var dlg = new SymbolEditorWindow(sources, names, editing?.Clone(), sourcePrefill, ApplyIndicatorAsync,
            refresh, SymbolConfigs.Select(c => c.Symbol).ToList(), indexSources, zigzagSources)
        {
            Owner = this,
        };
        dlg.ShowDialog();
    }

    private (int Weeks, int Minutes) GenerateIndicatorData(
        CandleDatabase db, IndicatorSymbol ind, CancellationToken ct, IProgress<double>? progress,
        long sourceConfirmedEndUnix = 0)
    {
        void Log(string m) => Dispatcher.BeginInvoke(() => AppendLog(m));
        if (IndicatorTypes.IsIndex(ind.Type))
            return DollarIndexSymbol.Generate(db, ind.Name, ind.IndexPairs, ind.StartTimeUnix,
                ind.EndTimeUnix, ind.IndexMethod, ind.IndexAlgorithm, SourcePipPoints, Log, ct, progress);
        if (IndicatorTypes.IsCurrency(ind.Type))
            return CurrencyIndexSymbol.Generate(db, ind.Name, ind.Source, ind.IndexPair,
                IndexAlgorithmOf(ind.Source), SourcePipPoints(ind.IndexPair), Log, ct, progress);
        if (IndicatorTypes.IsEntryPoints(ind.Type))
        {
            int pip = SourcePipPoints(ind.Source);
            return EntryPointsSymbol.Generate(db, ind.Source, ind.Name,
                ind.StopLossPips * pip, ind.TakeProfitPips * pip, Log, ct, progress);
        }
        if (IndicatorTypes.IsPriceAge(ind.Type))
            return PriceAgeSymbol.Generate(db, ind.Source, ind.Name, SourcePipPoints(ind.Source),
                sourceConfirmedEndUnix, Log, ct, progress);
        return (0, ZigZagSymbol.Generate(db, ind.Source, ind.Name, ZigZagLimitsOf(ind),
            Log, ct, progress));
    }

    private static ZigZagLimits ZigZagLimitsOf(IndicatorSymbol ind)
    {
        int pip = SourcePipPoints(ind.Source);
        return new ZigZagLimits(ind.Limit1Pips * pip, ind.Limit2Pips * pip, ind.Limit2DelayMinutes);
    }

    private sealed record CurrencyRefreshJob(
        string Name, string IndexSymbol, string Pair, string Algorithm, int PairPipPoints, long EndUnix);

    private CurrencyRefreshJob[] DependentCurrencyJobs(IndicatorSymbol saved)
    {
        if (!IndicatorTypes.IsIndex(saved.Type)) return Array.Empty<CurrencyRefreshJob>();
        return _config.Indicators
            .Where(x => IndicatorTypes.IsCurrency(x.Type) && SymbolNameEquals(x.Source, saved.Name))
            .Select(x => new CurrencyRefreshJob(
                x.Name, x.Source, x.IndexPair, saved.IndexAlgorithm, SourcePipPoints(x.IndexPair),
                ConfirmedEndUnix(new[] { x.IndexPair }, 0)))
            .ToArray();
    }

    private async Task RefreshIndicatorFromMenuAsync(string name)
    {
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
        if (ind == null) return;
        try
        {
            AppendLog($"Refreshing {ind.Name}...");
            await RefreshIndicatorAsync(ind.Clone(), new Progress<double>(_ => { }), CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"Refresh {name} failed: " + ex.Message);
        }
    }

    private async Task RebuildIndicatorFromMenuAsync(string name)
    {
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
        if (ind == null) return;
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, wait for it to finish");
            return;
        }
        if (MessageBox.Show(this,
                $"Rebuild indicator {ind.Name} from scratch?\n\n" +
                "Its stored data is thrown away and computed again over the whole history. " +
                "This can take a while.",
                "Rebuild indicator", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, wait for it to finish");
            return;
        }
        var saved = ind.Clone();
        var jobKey = RebuildJobKey + saved.Name;
        bool running = true;
        int shownPercent = -1;
        var progress = new Progress<double>(p =>
        {
            if (!running) return;
            int percent = (int)Math.Clamp(p * 100, 0, 100);
            if (percent == shownPercent) return;
            shownPercent = percent;
            _startupJobs[jobKey] = $"{saved.Name} · rebuilding {percent}%";
            RefreshLoadIndicator();
        });
        _dbBusy = true;
        try
        {
            AppendLog($"Rebuilding {saved.Name} from scratch...");
            CancelFind();
            _chartCts?.Cancel();
            StopLoader();
            var pendingLoad = _chartLoadTask;
            if (pendingLoad != null)
            {
                try { await pendingLoad; } catch { }
            }
            _startupJobs[jobKey] = $"{saved.Name} · rebuilding";
            RefreshLoadIndicator();
            var db = GetDb();
            long endUnix = ConfirmedEndUnix(new[] { saved.Source }, 0);
            var (_, minutes) = await Task.Run(
                () => GenerateIndicatorData(db, saved, CancellationToken.None, progress, endUnix));
            running = false;
            _startupJobs.TryRemove(jobKey, out _);
            RefreshLoadIndicator();
            await LoadChartAsync();
            AppendLog(IndicatorTypes.IsZigZag(saved.Type)
                ? $"{saved.Name} rebuilt: {minutes:N0} points"
                : $"{saved.Name} rebuilt: {minutes:N0} minutes");
        }
        catch (Exception ex)
        {
            AppendLog($"Rebuild {name} failed: " + ex.Message);
        }
        finally
        {
            running = false;
            _startupJobs.TryRemove(jobKey, out _);
            RefreshLoadIndicator();
            _dbBusy = false;
        }
    }

    private async Task RefreshIndicatorAsync(
        IndicatorSymbol saved, IProgress<double> progress, CancellationToken ct)
    {
        if (_dbBusy) throw new InvalidOperationException("DB operation already running");
        if (_historyCts != null) throw new InvalidOperationException("History download is running");
        if (!IndicatorTypes.HasStorage(saved.Type))
            throw new InvalidOperationException($"{saved.Type} symbols have nothing to refresh");
        _dbBusy = true;
        try
        {
            CancelFind();
            _chartCts?.Cancel();
            StopLoader();
            var pendingLoad = _chartLoadTask;
            if (pendingLoad != null)
            {
                try { await pendingLoad; } catch { }
            }
            var db = GetDb();
            long indexEndUnix = ConfirmedEndUnix(saved.IndexPairs, saved.EndTimeUnix);
            long currencyEndUnix = ConfirmedEndUnix(new[] { saved.IndexPair }, 0);
            long sourceEndUnix = ConfirmedEndUnix(new[] { saved.Source }, 0);
            string currencyAlgorithm = IndexAlgorithmOf(saved.Source);
            var dependents = DependentCurrencyJobs(saved);
            await Task.Run(() =>
            {
                if (IndicatorTypes.IsIndex(saved.Type))
                {
                    DollarIndexSymbol.Refresh(db, saved.Name, saved.IndexPairs, saved.StartTimeUnix,
                        indexEndUnix, saved.IndexMethod, saved.IndexAlgorithm, SourcePipPoints,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    foreach (var dep in dependents)
                        CurrencyIndexSymbol.Refresh(db, dep.Name, dep.IndexSymbol, dep.Pair,
                            dep.Algorithm, dep.PairPipPoints, dep.EndUnix,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct);
                }
                else if (IndicatorTypes.IsCurrency(saved.Type))
                {
                    CurrencyIndexSymbol.Refresh(db, saved.Name, saved.Source, saved.IndexPair,
                        currencyAlgorithm, SourcePipPoints(saved.IndexPair), currencyEndUnix,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                }
                else if (IndicatorTypes.IsEntryPoints(saved.Type))
                {
                    int pip = SourcePipPoints(saved.Source);
                    EntryPointsSymbol.Refresh(db, saved.Source, saved.Name,
                        saved.StopLossPips * pip, saved.TakeProfitPips * pip,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                }
                else if (IndicatorTypes.IsPriceAge(saved.Type))
                {
                    PriceAgeSymbol.Refresh(db, saved.Source, saved.Name, SourcePipPoints(saved.Source),
                        sourceEndUnix, m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                }
                else
                {
                    ZigZagSymbol.Generate(db, saved.Source, saved.Name, ZigZagLimitsOf(saved),
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                }
            }, ct);
            await LoadChartAsync();
            AppendLog($"Indicator {saved.Name} refreshed");
        }
        finally
        {
            _dbBusy = false;
        }
    }

    private async Task ApplyIndicatorAsync(
        IndicatorSymbol def, IndicatorSymbol? replacing, IProgress<double> progress, CancellationToken ct)
    {
        if (_dbBusy) throw new InvalidOperationException("DB operation already running");
        if (_historyCts != null) throw new InvalidOperationException("History download is running");
        _dbBusy = true;
        try
        {
            CancelFind();
            _chartCts?.Cancel();
            StopLoader();
            var pendingLoad = _chartLoadTask;
            if (pendingLoad != null)
            {
                try { await pendingLoad; } catch { }
            }
            var db = GetDb();
            bool recompute = replacing == null || !replacing.SameData(def);
            bool nameChanged = replacing != null && !SymbolNameEquals(replacing.Name, def.Name);
            string currencyAlgorithm = IndexAlgorithmOf(def.Source);
            long sourceEndUnix = ConfirmedEndUnix(new[] { def.Source }, 0);
            var dependentCurrencies = replacing != null && IndicatorTypes.IsIndex(def.Type)
                ? _config.Indicators
                    .Where(x => IndicatorTypes.IsCurrency(x.Type)
                        && SymbolNameEquals(x.Source, replacing.Name))
                    .Select(x => (x.Name, x.IndexPair))
                    .ToArray()
                : Array.Empty<(string Name, string IndexPair)>();
            await Task.Run(() =>
            {
                if (recompute)
                {
                    if (nameChanged) db.DeleteSymbol(replacing!.Name);
                    if (IndicatorTypes.IsDrawing(def.Type))
                    {
                        db.DeleteSymbol(def.Name);
                        DrawingStore.Save(db.SymbolDirectory(def.Name), Array.Empty<PivotPoint[]>());
                        progress.Report(1.0);
                    }
                    else if (IndicatorTypes.IsShift(def.Type) || IndicatorTypes.IsDeals(def.Type)
                        || IndicatorTypes.IsDensity(def.Type) || IndicatorTypes.IsSpread(def.Type)
                        || IndicatorTypes.IsVolume(def.Type) || IndicatorTypes.IsLevels(def.Type)
                        || IndicatorTypes.IsOrderBook(def.Type) || IndicatorTypes.IsAverage(def.Type))
                    {
                        db.DeleteSymbol(def.Name);
                        progress.Report(1.0);
                    }
                    else if (IndicatorTypes.IsIndex(def.Type))
                    {
                        DollarIndexSymbol.Generate(db, def.Name, def.IndexPairs, def.StartTimeUnix,
                            def.EndTimeUnix, def.IndexMethod, def.IndexAlgorithm, SourcePipPoints,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                        foreach (var dep in dependentCurrencies)
                            CurrencyIndexSymbol.Generate(db, dep.Name, def.Name, dep.IndexPair,
                                def.IndexAlgorithm, SourcePipPoints(dep.IndexPair),
                                m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct);
                    }
                    else if (IndicatorTypes.IsCurrency(def.Type))
                    {
                        CurrencyIndexSymbol.Generate(db, def.Name, def.Source, def.IndexPair,
                            currencyAlgorithm, SourcePipPoints(def.IndexPair),
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    }
                    else if (IndicatorTypes.IsEntryPoints(def.Type))
                    {
                        int pip = SourcePipPoints(def.Source);
                        EntryPointsSymbol.Generate(db, def.Source, def.Name,
                            def.StopLossPips * pip, def.TakeProfitPips * pip,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    }
                    else if (IndicatorTypes.IsPriceAge(def.Type))
                    {
                        PriceAgeSymbol.Generate(db, def.Source, def.Name, SourcePipPoints(def.Source),
                            sourceEndUnix, m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    }
                    else
                    {
                        ZigZagSymbol.Generate(db, def.Source, def.Name, ZigZagLimitsOf(def),
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    }
                }
                else if (nameChanged)
                {
                    db.RenameSymbol(replacing!.Name, def.Name);
                    progress.Report(1.0);
                }
                else
                {
                    progress.Report(1.0);
                }
            }, ct);
            int idx = replacing == null
                ? -1
                : _config.Indicators.FindIndex(x => SymbolNameEquals(x.Name, replacing.Name));
            if (idx < 0
                && (IndicatorTypes.IsDensity(def.Type) || IndicatorTypes.IsVolume(def.Type)))
            {
                var densityPeer = _config.Indicators.FirstOrDefault(x =>
                    IndicatorTypes.IsDensity(x.Type) || IndicatorTypes.IsVolume(x.Type));
                if (densityPeer != null) def.DensitySelected = densityPeer.DensitySelected;
            }
            if (idx >= 0) _config.Indicators[idx] = def;
            else _config.Indicators.Add(def);
            if (nameChanged)
                foreach (var child in _config.Indicators)
                    if (SymbolNameEquals(child.Source, replacing!.Name)) child.Source = def.Name;
            if (nameChanged && IndicatorTypes.IsZigZag(def.Type))
                foreach (var child in _config.Indicators)
                    for (int i = 0; i < child.FindZigZagTargets.Count; i++)
                        if (SymbolNameEquals(child.FindZigZagTargets[i], replacing!.Name))
                            child.FindZigZagTargets[i] = def.Name;
            if (replacing != null && IndicatorTypes.IsShift(def.Type)
                && !SymbolNameEquals(replacing.ShiftTarget(), def.ShiftTarget()))
                ClearMirrorBase(def.Name);
            if (replacing != null && replacing.Name != def.Name)
                RenameChartStateKeys(replacing.Name, def.Name);
            RecordShiftPlacements();
            _config.Save();
            await LoadChartAsync();
            AppendLog(recompute ? $"Indicator {def.Name} computed" : $"Indicator {def.Name} updated");
        }
        finally
        {
            _dbBusy = false;
        }
    }

    private FindResultsWindow? _findWindow;
    private bool _findBusy;
    private CancellationTokenSource? _findCts;
    private (string Name, FindData Data, FindResult Result)? _pendingFindApply;
    private bool _findApplyBusy;

    private void CancelFind()
    {
        _findCts?.Cancel();
        if (_findWindow is { IsLoaded: true }) _findWindow.Close();
    }

    private FindResultsWindow OpenFindWindow(string indicatorName)
    {
        if (_findWindow is { IsLoaded: true }) _findWindow.Close();
        var win = new FindResultsWindow(indicatorName) { Owner = this };
        _findWindow = win;
        win.Show();
        return win;
    }

    private async Task RunFindAsync(string name)
    {
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
        if (ind == null || !IndicatorTypes.IsShift(ind.Type)) return;
        if (Chart.SelectedRange is not { } sel)
        {
            AppendLog("Find: select a range on the chart first (Shift+drag)");
            return;
        }
        if (_findBusy)
        {
            AppendLog("Find: search already running");
            return;
        }
        if (Chart.GetSeries(ind.Source) == null)
        {
            AppendLog($"Find: source {ind.Source} is not loaded");
            return;
        }
        string fragmentText = $"Fragment: {FmtUtc(sel.StartUnix)} .. {FmtUtc(sel.EndUnix)} UTC";
        var zigzagTargets = ZigZagFindTargets();
        var paramsDlg = new SearchParamsWindow(
            ind.Name, fragmentText, SymbolConfigs.Select(c => c.Symbol).ToList(),
            zigzagTargets, sel.StartUnix, sel.EndUnix, ind)
        {
            Owner = this,
        };
        if (paramsDlg.ShowDialog() != true) return;
        var searchParams = paramsDlg.Params;
        var targetNames = paramsDlg.Targets;
        bool zigzag = FindSearchTypes.IsZigZag(paramsDlg.SearchType);
        var zzSelected = new List<ZigZagFindTarget>();
        if (zigzag)
        {
            foreach (var zzName in paramsDlg.ZigZagTargetNames)
            {
                var t = zigzagTargets.FirstOrDefault(x => SymbolNameEquals(x.Name, zzName));
                if (t != null) zzSelected.Add(t);
            }
            if (zzSelected.Count == 0)
            {
                AppendLog("Find: no ZigZag indicators selected");
                return;
            }
            ind.FindSearchType = FindSearchTypes.ZigZag;
            ind.FindZigZagTargets = zzSelected.Select(t => t.Name).ToList();
            ind.FindZoomPercent = searchParams.ZoomPercent;
        }
        else
        {
            ind.FindSearchType = FindSearchTypes.Similarity;
            ind.FindSmoothMinutes = searchParams.SmoothMinutes;
            ind.FindZoomPercent = searchParams.ZoomPercent;
            ind.FindStepMinutes = searchParams.StepMinutes;
            ind.FindTargets = new List<string>(targetNames);
        }
        _config.Save();
        _findBusy = true;
        var cts = new CancellationTokenSource();
        _findCts = cts;
        var win = OpenFindWindow(ind.Name);
        win.CancelRequested += cts.Cancel;
        win.ShowSearching(fragmentText);
        var progress = new Progress<double>(win.SetProgress);
        var sw = Stopwatch.StartNew();
        try
        {
            var loader = _loader;
            if (loader != null)
            {
                var loadNames = zigzag ? zzSelected.Select(t => t.Source).ToList() : targetNames;
                foreach (var symbol in loadNames.Prepend(ind.Source).Distinct(SymbolNameComparer))
                {
                    var loadTask = loader.EnsureFullAsync(symbol, "find");
                    if (loadTask.IsCompleted) continue;
                    AppendLog($"Find: loading full {symbol} history first...");
                    await loadTask;
                }
            }
            if (cts.IsCancellationRequested) throw new OperationCanceledException();
            var src = Chart.GetSeries(ind.Source);
            if (src == null || src.History.Minutes.Length == 0)
            {
                AppendLog($"Find: source {ind.Source} is not loaded");
                if (win.IsLoaded) win.Close();
                return;
            }
            var minutes = src.History.Minutes;
            FindData data;
            if (zigzag)
            {
                var zzSearchTargets = new List<ZigZagSearchTarget>();
                foreach (var t in zzSelected)
                {
                    var series = Chart.GetSeries(t.Source);
                    if (series == null || series.History.Minutes.Length == 0)
                    {
                        AppendLog($"Find: target {t.Source} is not loaded");
                        if (win.IsLoaded) win.ShowFailed($"Target {t.Source} is not loaded");
                        return;
                    }
                    zzSearchTargets.Add(new ZigZagSearchTarget(t, series.History.Minutes));
                }
                var pattern = paramsDlg.Pattern;
                var patternSource = paramsDlg.PatternZigZagName;
                long anchorUnix = paramsDlg.AnchorUnix;
                data = await Task.Run(
                    () => ZigZagSearch.Search(ind.Source, minutes, zzSearchTargets, patternSource,
                        pattern, sel.StartUnix, sel.EndUnix, anchorUnix, searchParams.ZoomPercent,
                        progress, cts.Token),
                    cts.Token);
            }
            else
            {
                var searchTargets = new List<SearchTarget>();
                foreach (var symbol in targetNames)
                {
                    var series = Chart.GetSeries(symbol);
                    if (series == null || series.History.Minutes.Length == 0)
                    {
                        AppendLog($"Find: target {symbol} is not loaded");
                        if (win.IsLoaded) win.ShowFailed($"Target {symbol} is not loaded");
                        return;
                    }
                    searchTargets.Add(new SearchTarget(series.Symbol, series.History.Minutes));
                }
                data = await Task.Run(
                    () => SimilarSearch.Search(ind.Source, minutes, searchTargets, sel.StartUnix,
                        sel.EndUnix, searchParams, progress, cts.Token),
                    cts.Token);
            }
            var current = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, ind.Name));
            if (current == null || !IndicatorTypes.IsShift(current.Type)
                || !SymbolNameEquals(current.Source, ind.Source))
            {
                AppendLog($"Find {ind.Name}: indicator changed during search, results discarded");
                if (win.IsLoaded) win.Close();
                return;
            }
            if (data.Results.Count == 0)
            {
                AppendLog($"Find {ind.Name}: no matches, previous results kept");
                if (win.IsLoaded) win.ShowResults(data, _ => { });
                return;
            }
            FindStore.Save(GetDb().SymbolDirectory(ind.Name), data);
            AppendLog($"Find {ind.Name}: {data.Results.Count} matches in {sw.ElapsedMilliseconds} ms");
            if (win.IsLoaded) win.ShowResults(data, r => QueueFindApply(ind.Name, data, r));
        }
        catch (OperationCanceledException)
        {
            AppendLog("Find cancelled");
            if (win.IsLoaded) win.Close();
        }
        catch (Exception ex)
        {
            AppendLog("Find failed: " + ex.Message);
            if (win.IsLoaded) win.ShowFailed(ex.Message);
        }
        finally
        {
            _findBusy = false;
            if (ReferenceEquals(_findCts, cts)) _findCts = null;
            cts.Dispose();
        }
    }

    private List<ZigZagFindTarget> ZigZagFindTargets()
    {
        var list = new List<ZigZagFindTarget>();
        foreach (var zz in _config.Indicators)
        {
            if (!IndicatorTypes.IsZigZag(zz.Type)) continue;
            var points = Chart.GetSeries(zz.Name)?.Pivots;
            if (points == null || points.Length == 0)
                points = ZigZagStore.Load(GetDb().SymbolDirectory(zz.Name));
            if (points.Length < 2) continue;
            list.Add(new ZigZagFindTarget(zz.Name, zz.Source, SourcePipPoints(zz.Source), points));
        }
        return list;
    }

    private FindData? LoadFindResults(IndicatorSymbol ind)
    {
        var data = FindStore.Load(GetDb().SymbolDirectory(ind.Name));
        return data != null && SymbolNameEquals(data.Source, ind.Source) ? data : null;
    }

    private void ShowFindResults(string name)
    {
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
        if (ind == null || !IndicatorTypes.IsShift(ind.Type)) return;
        if (_findBusy)
        {
            AppendLog("Find: search is running, wait for it to finish");
            return;
        }
        var data = LoadFindResults(ind);
        if (data == null)
        {
            AppendLog($"{ind.Name}: no saved find results for source {ind.Source}, run Find first");
            return;
        }
        var win = OpenFindWindow(ind.Name);
        win.ShowResults(data, r => QueueFindApply(ind.Name, data, r));
    }

    private void QueueFindApply(string name, FindData data, FindResult result)
    {
        _pendingFindApply = (name, data, result);
        if (!_findApplyBusy) _ = DrainFindApplyAsync();
    }

    private async Task DrainFindApplyAsync()
    {
        _findApplyBusy = true;
        try
        {
            while (_pendingFindApply is { } job)
            {
                _pendingFindApply = null;
                try
                {
                    await ApplyFindResultAsync(job.Name, job.Data, job.Result);
                }
                catch (Exception ex)
                {
                    AppendLog("Apply find result failed: " + ex.Message);
                }
            }
        }
        finally
        {
            _findApplyBusy = false;
        }
    }

    private async Task ApplyFindResultAsync(string name, FindData data, FindResult result)
    {
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
        if (ind == null || !IndicatorTypes.IsShift(ind.Type))
        {
            AppendLog($"{name}: indicator no longer exists, result not applied");
            return;
        }
        if (!SymbolNameEquals(data.Source, ind.Source))
        {
            AppendLog($"{name}: results are for source {data.Source}, indicator now uses {ind.Source}, run Find again");
            return;
        }
        if (_dbBusy || _historyCts != null)
        {
            AppendLog($"{name}: DB operation is running, try again when it finishes");
            return;
        }
        var pendingLoad = _chartLoadTask;
        if (pendingLoad != null)
        {
            try { await pendingLoad; } catch { }
        }
        string target = result.Symbol.Length > 0 ? result.Symbol : data.Source;
        var loader = _loader;
        if (loader != null)
        {
            try
            {
                await loader.EnsureFullAsync(target, "find");
            }
            catch (Exception ex)
            {
                AppendLog($"{name}: loading target {target} failed: {ex.Message}");
                return;
            }
            await loader.PauseAsync(name);
        }
        try
        {
            var tgt = Chart.GetSeries(target);
            if (tgt?.Transform == null || tgt.History.Minutes.Length == 0)
            {
                AppendLog($"{name}: target {target} is not loaded");
                return;
            }
            bool flip = result.Mirrored;
            bool targetChanged = !SymbolNameEquals(ind.ShiftTarget(), target);
            ind.SourceTimeUnix = result.SourceUnix;
            ind.ChartTimeUnix = data.FragStartUnix;
            ind.Flip = flip;
            ind.TargetSymbol = target;
            if (targetChanged) ClearMirrorBase(name);
            RecordShiftPlacements();
            _config.Save();
            long delta = ShiftedSymbol.VirtualDelta(result.SourceUnix, data.FragStartUnix);
            var minutes = tgt.History.Minutes;
            var transform = tgt.Transform;
            var (history, offset, newTransform) = await Task.Run(() =>
            {
                var shifted = ShiftedSymbol.Shift(minutes, delta).ToArray();
                if (!flip)
                    return (CandleHistory.Build(shifted), result.MeanA - result.MeanB, transform);
                long flipBase = transform.Mirror
                    ? transform.MirrorBase
                    : ShiftedSymbol.FlipBase(shifted);
                ShiftedSymbol.FlipAround(shifted, flipBase);
                var flipped = transform.Mirror
                    ? new SeriesTransform(false, 0, transform.PipPoints)
                    : new SeriesTransform(true, flipBase, transform.PipPoints);
                return (CandleHistory.Build(shifted), result.MeanA + result.MeanB - flipBase, flipped);
            });
            Chart.SetSeriesOffset(name, offset);
            Chart.ReplaceSeries(name, history, newTransform);
            UpdateSeriesTransform(name, newTransform.Mirror ? newTransform.MirrorBase : 0,
                newTransform.PipPoints);
            RefreshShiftLiveTails(target);
            loader?.MarkShiftApplied(name, target, delta, newTransform.Mirror, newTransform.MirrorBase,
                newTransform.PipPoints);
            string simText = result.Sim.ToString("F4", CultureInfo.InvariantCulture);
            AppendLog($"{name}: {target} shifted to {FmtUtc(result.SourceUnix)} " +
                $"(sim {simText}{(flip ? ", flipped" : "")})");
        }
        finally
        {
            loader?.Resume(name);
        }
    }

    private void UpdateSeriesTransform(string symbol, long mirrorBase, int pipPoints)
    {
        var transforms = _seriesTransforms;
        for (int i = 0; i < transforms.Length; i++)
            if (SymbolNameEquals(transforms[i].Symbol, symbol))
                transforms[i] = (transforms[i].Symbol, mirrorBase, pipPoints);
    }

    private void ClearMirrorBase(string symbol)
    {
        if (_config.MirrorBases == null) return;
        var key = _config.MirrorBases.Keys.FirstOrDefault(k => SymbolNameEquals(k, symbol));
        if (key != null) _config.MirrorBases.Remove(key);
    }

    private readonly Dictionary<string, long> _pendingShiftNudge = new();
    private bool _shiftNudgeBusy;

    private void QueueShiftNudge(string name, int wheelDelta, int stepMinutes)
    {
        int notches = wheelDelta / 120;
        if (notches == 0) notches = Math.Sign(wheelDelta);
        if (notches == 0) return;
        QueueShiftStep(name, (long)notches * stepMinutes * 60);
    }

    private void QueueShiftStep(string name, long stepSeconds)
    {
        if (stepSeconds == 0) return;
        _pendingShiftNudge[name] = _pendingShiftNudge.GetValueOrDefault(name) + stepSeconds;
        if (!_shiftNudgeBusy) _ = DrainShiftNudgeAsync();
    }

    private async Task DrainShiftNudgeAsync()
    {
        _shiftNudgeBusy = true;
        try
        {
            while (_pendingShiftNudge.Count > 0)
            {
                var name = _pendingShiftNudge.Keys.First();
                long step = _pendingShiftNudge[name];
                _pendingShiftNudge.Remove(name);
                if (step == 0) continue;
                try
                {
                    await NudgeShiftTimeAsync(name, step);
                }
                catch (Exception ex)
                {
                    AppendLog($"{name}: time shift failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _shiftNudgeBusy = false;
        }
    }

    private async Task NudgeShiftTimeAsync(string name, long stepSeconds)
    {
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
        if (ind == null || !IndicatorTypes.IsShift(ind.Type)) return;
        if (_dbBusy || _historyCts != null)
        {
            AppendLog($"{name}: DB operation is running, try again when it finishes");
            return;
        }
        var pendingLoad = _chartLoadTask;
        if (pendingLoad != null)
        {
            try { await pendingLoad; } catch { }
        }
        var w = WeekendCompressor.Instance;
        long newChartTime = w.ToReal(w.ToVirtual(ind.ChartTimeUnix) + stepSeconds);
        long newDelta = ShiftedSymbol.VirtualDelta(ind.SourceTimeUnix, newChartTime);
        var loader = _loader;
        if (loader != null) await loader.PauseAsync(name);
        try
        {
            var series = Chart.GetSeries(name);
            if (series == null) return;
            var old = series.History;
            if (old.Minutes.Length > 0)
            {
                var history = await Task.Run(() =>
                    CandleHistory.Build(ShiftedSymbol.Restamp(old.Minutes, stepSeconds)));
                if (!ReferenceEquals(Chart.GetSeries(name)?.History, old)) return;
                Chart.ReplaceSeries(name, history);
            }
            ind.ChartTimeUnix = newChartTime;
            RecordShiftPlacements();
            _config.Save();
            RefreshShiftLiveTails(ind.ShiftTarget());
            loader?.SetShiftDelta(name, newDelta);
            AppendLog($"{name}: chart time {FmtUtc(newChartTime)}");
        }
        finally
        {
            loader?.Resume(name);
        }
    }

    private static string FmtUtc(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private async void DeleteIndicator(string name)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, wait for it to finish");
            return;
        }
        var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
        if (ind == null) return;
        string deletePrompt = IndicatorTypes.IsShift(ind.Type) || IndicatorTypes.IsDeals(ind.Type)
            || IndicatorTypes.IsDensity(ind.Type) || IndicatorTypes.IsSpread(ind.Type)
            || IndicatorTypes.IsVolume(ind.Type) || IndicatorTypes.IsOrderBook(ind.Type)
            || IndicatorTypes.IsLevels(ind.Type)
            ? $"Delete indicator {ind.Name}?"
            : $"Delete indicator {ind.Name}? Its data files will be removed.";
        int children = _config.Indicators.Count(x => SymbolNameEquals(x.Source, ind.Name));
        if (children > 0)
            deletePrompt += $"\n\n{children} indicator(s) built on it will lose their source.";
        if (MessageBox.Show(this, deletePrompt,
                "Delete indicator", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        if (_historyCts != null)
        {
            AppendLog("History download is running, wait for it to finish");
            return;
        }
        _dbBusy = true;
        try
        {
            CancelFind();
            _chartCts?.Cancel();
            StopLoader();
            var pendingLoad = _chartLoadTask;
            if (pendingLoad != null)
            {
                try { await pendingLoad; } catch { }
            }
            var db = GetDb();
            await Task.Run(() => db.DeleteSymbol(ind.Name));
            _config.Indicators.Remove(ind);
            _config.Save();
            if (IndicatorTypes.IsDrawing(ind.Type))
            {
                foreach (var note in NoteList) note.RemoveSymbol(ind.Name);
                SaveNotes();
            }
            await LoadChartAsync();
            AppendLog($"Indicator {ind.Name} deleted");
        }
        catch (Exception ex)
        {
            AppendLog("Delete failed: " + ex.Message);
        }
        finally
        {
            _dbBusy = false;
        }
    }

    private async void VerifyDbBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_dbBusy)
        {
            AppendLog("DB operation already running");
            return;
        }
        _dbBusy = true;
        try
        {
            var db = GetDb();
            var indicators = _config.Indicators.ToArray();
            var lines = await Task.Run(() =>
            {
                var res = new List<string>();
                foreach (var cfg in DisplayConfigs(indicators))
                {
                    var symbol = cfg.Symbol;
                    if (cfg.IsShift || cfg.IsDeals || cfg.IsDensity || cfg.IsSpread
                        || cfg.IsVolume || cfg.IsOrderBook || cfg.IsAverage || cfg.IsLevels) continue;
                    var years = db.ExistingYears(symbol);
                    if (years.Count == 0)
                    {
                        res.Add($"{symbol}: no data");
                        continue;
                    }
                    var spread = default(SpreadStats);
                    foreach (var y in years)
                    {
                        var stats = db.ReadSpreadStats(symbol, y);
                        spread = spread.Add(stats);
                        res.Add($"{symbol} {y}: {stats.Filled:N0} filled minutes");
                    }
                    if (spread.WithSpread > 0)
                        res.Add($"{symbol} spread: {spread.WithSpread:N0} of {spread.Filled:N0} minutes " +
                            $"({100.0 * spread.WithSpread / Math.Max(1, spread.Filled):F1}%), " +
                            $"avg {spread.AvgPips:F1}, max {spread.MaxPips:F1} pips");
                    if (spread.Wide > 0)
                        res.Add($"{symbol} wide spread: {spread.Wide:N0} of {spread.Filled:N0} minutes " +
                            $"({100.0 * spread.Wide / Math.Max(1, spread.Filled):F2}%)" +
                            (WideSpreadRule.Hide ? ", hidden" : ""));
                    if (spread.WithVolume > 0)
                        res.Add($"{symbol} volume: {spread.WithVolume:N0} of {spread.Filled:N0} minutes " +
                            $"({100.0 * spread.WithVolume / Math.Max(1, spread.Filled):F1}%), " +
                            $"avg {spread.AvgVolume:N0}, max {spread.MaxVolume:N0} contracts");
                    var last = db.LastFilledMinuteUtc(symbol);
                    if (last != null)
                        res.Add($"{symbol} last minute: {last:yyyy-MM-dd HH:mm} UTC");
                }
                return res;
            });
            foreach (var line in lines) AppendLog(line);
            var cal = _calendarEntries;
            if (cal.Length > 0)
            {
                var firstUtc = DateTimeOffset.FromUnixTimeSeconds(cal[0].UnixSeconds).UtcDateTime;
                var lastUtc = DateTimeOffset.FromUnixTimeSeconds(cal[^1].UnixSeconds).UtcDateTime;
                int high = cal.Count(x => x.Impact == (byte)CalendarImpact.High);
                int medium = cal.Count(x => x.Impact == (byte)CalendarImpact.Medium);
                AppendLog($"Calendar: {cal.Length:N0} tracked events, {firstUtc:yyyy-MM-dd} to {lastUtc:yyyy-MM-dd}, {high:N0} high / {medium:N0} medium");
            }
            else
            {
                AppendLog("Calendar: no events (use Import calendar history / Download calendar)");
            }
        }
        catch (Exception ex)
        {
            AppendLog("Verify failed: " + ex.Message);
        }
        finally
        {
            _dbBusy = false;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _autoReconnect = false;
        _gameWindow?.Panel.FlushComment();
        EndTimeSession();
        _stateSaveTimer.Stop();
        SaveChartState();
        _authCts?.Cancel();
        _calendarCts?.Cancel();
        _historyCts?.Cancel();
        _repairCts?.Cancel();
        _chartCts?.Cancel();
        _computeCts?.Cancel();
        _findCts?.Cancel();
        _loader?.Dispose();
        await DisconnectAsync();
        _db?.Dispose();
    }
}
