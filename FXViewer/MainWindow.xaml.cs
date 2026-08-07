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
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow : Window
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
        public bool IsDeals { get; init; }
        public string? ShiftReadSymbol { get; init; }
        public int PriceDiv { get; init; } = 1;
    }

    private sealed record SeriesSlot(
        SymbolSeries Series, long MirrorBase, int PipPoints, int LastAvg, bool IsBase, long LastUnix,
        string ReadSymbol, bool Mirror, bool IsShift, long ShiftDelta,
        int MinYear, int MaxYear, int? LoadedLo, int? LoadedHi);

    private const int IndexPipPoints = 10;
    private const string RebuildJobKey = "rebuild:";
    private const int ShiftNudgeMinutes = 15;
    private const int ShiftNudgeFineMinutes = 1;

    private static DisplayConfig IndicatorConfig(
        IndicatorSymbol ind, string sourceSymbol, bool sourceMirror, int sourcePipPoints,
        int sourcePriceDiv)
    {
        bool isShift = IndicatorTypes.IsShift(ind.Type);
        bool isEntry = IndicatorTypes.IsEntryPoints(ind.Type);
        string target = isShift ? ind.ShiftTarget() : "";
        var pair = isShift ? PairDisplay(target) : null;
        bool targetMirror = pair?.Mirror ?? sourceMirror;
        int targetPipPoints = pair?.PipPoints ?? sourcePipPoints;
        int targetPriceDiv = pair?.PriceDiv ?? sourcePriceDiv;
        return new DisplayConfig(ind.Name, ind.ColorArgb,
            isEntry ? false : isShift ? targetMirror ^ ind.Flip : sourceMirror,
            isEntry ? IndexPipPoints : isShift ? targetPipPoints : sourcePipPoints,
            IndicatorTypes.IsZigZag(ind.Type), sourceSymbol, IndicatorTypes.IsDrawing(ind.Type),
            isShift, ShiftedSymbol.VirtualDelta(ind.SourceTimeUnix, ind.ChartTimeUnix))
        {
            IsEntryPanel = isEntry,
            IsDeals = IndicatorTypes.IsDeals(ind.Type),
            ShiftReadSymbol = isShift ? target : null,
            PriceDiv = isEntry ? 1 : isShift ? targetPriceDiv : sourcePriceDiv,
        };
    }

    private static (bool Mirror, int PipPoints, int PriceDiv)? PairDisplay(string symbol)
    {
        foreach (var c in SymbolConfigs)
            if (SymbolNameEquals(c.Symbol, symbol)) return (c.Mirror, c.PipPoints, c.PriceDiv);
        return null;
    }

    private static IEnumerable<DisplayConfig> DisplayConfigs(IReadOnlyList<IndicatorSymbol> indicators)
    {
        var emitted = new HashSet<string>();
        foreach (var c in SymbolConfigs)
        {
            yield return new DisplayConfig(c.Symbol, c.ColorArgb, c.Mirror, c.PipPoints, false, null, false)
            {
                PriceDiv = c.PriceDiv,
            };
            foreach (var ind in indicators)
            {
                if (IndicatorTypes.IsIndex(ind.Type)) continue;
                if (!SymbolNameEquals(ind.Source, c.Symbol)) continue;
                emitted.Add(IndicatorSymbol.NameKey(ind.Name));
                yield return IndicatorConfig(ind, c.Symbol, c.Mirror, c.PipPoints, c.PriceDiv);
            }
        }
        foreach (var index in indicators)
        {
            if (!IndicatorTypes.IsIndex(index.Type)) continue;
            emitted.Add(IndicatorSymbol.NameKey(index.Name));
            yield return new DisplayConfig(
                index.Name, index.ColorArgb, index.Flip, IndexPipPoints, false, null, false);
            foreach (var ind in indicators)
            {
                if (IndicatorTypes.IsIndex(ind.Type)) continue;
                if (!SymbolNameEquals(ind.Source, index.Name)) continue;
                emitted.Add(IndicatorSymbol.NameKey(ind.Name));
                yield return IndicatorConfig(ind, index.Name, index.Flip, IndexPipPoints, 1);
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
                IsDeals = IndicatorTypes.IsDeals(ind.Type),
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
    private ChartViewState? _pendingChartState;
    private (string Symbol, long MirrorBase, int PipPoints)[] _seriesTransforms =
        Array.Empty<(string, long, int)>();
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
        public bool Dirty;
    }

    private readonly DispatcherTimer _liveFlushTimer;
    private readonly DispatcherTimer _liveRepairTimer;
    private CancellationTokenSource? _repairCts;
    private Task? _repairTask;
    private LiveDbWriter? _liveDb;
    private readonly Dictionary<string, long> _pendingAverageRedo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveState> _live = new();
    private readonly Dictionary<long, string> _idToSymbol = new();
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

    public MainWindow()
    {
        InitializeComponent();
        ClientIdBox.Text = _config.ClientId;
        ClientSecretBox.Text = _config.ClientSecret;
        LiveCheck.IsChecked = _config.IsLive;
        AppendLog("Config folder: " + AppConfig.Dir);
        if (!string.IsNullOrEmpty(_config.AccessToken))
            AppendLog("Access token found in config, Authorize can be skipped");
        Chart.Info += AppendLog;
        Chart.ViewChanged += (k, startBucket, widthPx, map) =>
        {
            TimeAxis.Update(k, startBucket, widthPx, map);
            OnViewRangeChanged(k, startBucket, widthPx, map);
        };
        Chart.CursorTimeChanged += (unix, xDip) => TimeAxis.SetCursor(unix, xDip);
        Chart.CursorPricesChanged += p => SymbolBar.SetCursorPrices(ToTruePrices(p));
        SymbolBar.PriceOffsetWheel += Chart.ShiftSeriesOffset;
        SymbolBar.TimeShiftWheel += (symbol, delta, fine) =>
            QueueShiftNudge(symbol, delta, fine ? ShiftNudgeFineMinutes : ShiftNudgeMinutes);
        Chart.SeriesTimeShiftRequested += QueueShiftStep;
        SymbolBar.SymbolClick += symbol =>
        {
            bool enabled = Chart.ToggleSeries(symbol);
            SymbolBar.SetSymbolEnabled(symbol, enabled);
            if (!enabled || _loader == null) return;
            if (Chart.IsFitView || _lastViewRealRange == null)
                _ = _loader.EnsureFullAsync(symbol, "toggle");
            else
                _loader.EnsureVisibleRange(
                    _lastViewRealRange.Value.Lo, _lastViewRealRange.Value.Hi, Chart.HiddenSymbols);
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
        SymbolBar.FindRequested += name => _ = RunFindAsync(name);
        SymbolBar.ShowResultsRequested += ShowFindResults;
        SymbolBar.HasChartSelection = () => Chart.SelectedRange != null && !_findBusy;
        SymbolBar.HasFindResults = name =>
        {
            if (_findBusy) return false;
            var ind = _config.Indicators.FirstOrDefault(x => SymbolNameEquals(x.Name, name));
            return ind != null && LoadFindResults(ind) != null;
        };
        SymbolBar.CalendarClick += () =>
            SymbolBar.SetCalendarRow(Chart.HasCalendar, Chart.ToggleCalendar());
        SymbolBar.CalendarSettingsRequested += OpenCalendarSettings;
        SymbolBar.CalendarFindRequested += () => _ = OpenCalendarFindAsync();
        SymbolBar.WeekendsClick += () => SymbolBar.SetWeekendsRow(Chart.ToggleWeekends());
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
        Chart.PivotEditRequested += req => _ = ApplyPivotEditAsync(req);
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
        _liveFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _liveFlushTimer.Tick += (_, _) => FlushLive();
        _liveRepairTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _liveRepairTimer.Tick += (_, _) =>
        {
            if (_repairTask is { IsCompleted: false }) return;
            _repairTask = RepairProvisionalAsync();
        };
        BuildTabs();
        Loaded += async (_, _) => await StartupAsync();
    }

    private async Task StartupAsync()
    {
        AppendLog($"=== Startup === (window ready in {_bootSw.ElapsedMilliseconds} ms since ctor)");
        SetConnState(ConnState.Offline);
        await LoadChartAsync();
        await LoadCalendarEntriesAsync();
        _ = AutoRefreshCalendarAsync();
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
            var indicatorNames = new HashSet<string>(indicators.Select(i => IndicatorSymbol.NameKey(i.Name)));
            var hiddenSymbols = new HashSet<string>(ActiveState?.HiddenSymbols ?? new List<string>());
            var viewRange = StartupRealRange();
            var knownBases = new Dictionary<string, long>(
                _config.MirrorBases ?? new Dictionary<string, long>());
            var newBases = new ConcurrentDictionary<string, long>();
            AppendLog(viewRange == null
                ? "Chart load: no saved view - reading full history of enabled symbols..."
                : "Chart load: reading only the visible range, the rest loads on demand...");
            var swBg = Stopwatch.StartNew();
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
                    if (isDrawing || editable || configs[i].IsDeals) return;
                    bool entryPanel = configs[i].IsEntryPanel;
                    var swSym = Stopwatch.StartNew();
                    var readSymbol = isShift ? configs[i].ShiftReadSymbol ?? source! : symbol;
                    var years = db.ExistingYears(readSymbol);
                    if (years.Count == 0)
                    {
                        if (!indicatorNames.Contains(IndicatorSymbol.NameKey(symbol))) return;
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, false, null, new SeriesTransform(false, 0, pipPoints), source,
                                null, entryPanel)
                            { PriceMul = configs[i].PriceDiv, TimeShift = isShift },
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
                        hiddenSymbols.Contains(symbol), viewRange, isShift, shiftDelta, minYear, maxYear);
                    var candles = new List<Candle>();
                    if (loadYears is { } ly)
                    {
                        _startupJobs[symbol] =
                            $"{symbol} {SeriesDataLoader.YearSpanText(ly.Lo, ly.Hi)} · reading (startup)";
                        Dispatcher.BeginInvoke((Action)RefreshLoadIndicator);
                        candles = SeriesDataLoader.ReadYears(db, readSymbol, ly.Lo, ly.Hi);
                        if (isShift) candles = ShiftedSymbol.Shift(candles, shiftDelta);
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
                    slots[i] = new SeriesSlot(
                        new SymbolSeries(symbol, CandleHistory.Build(transformed), color, pipPoints,
                            false, null, transform, source, null, entryPanel)
                        { PriceMul = configs[i].PriceDiv, TimeShift = isShift },
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
                for (int i = 0; i < configs.Length; i++)
                {
                    var (symbol, color, mirror, pipPoints, editable, source, isDrawing, _, _) = configs[i];
                    bool isDeals = configs[i].IsDeals;
                    if (!isDrawing && !isDeals && !editable) continue;
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
                        continue;
                    }
                    if (editable)
                    {
                        var points = ZigZagStore.Load(db.SymbolDirectory(symbol));
                        if (points.Length > 0)
                        {
                            lastVal = points[^1].Value;
                            lastUnix = points[^1].UnixSeconds;
                        }
                        Dispatcher.BeginInvoke(() =>
                            AppendLog($"  {symbol}: {points.Length:N0} ZigZag points"));
                        slots[i] = new SeriesSlot(
                            new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                                pipPoints, true, points, transform, source)
                            { PriceMul = configs[i].PriceDiv },
                            mirrorBase, pipPoints, lastVal, false, lastUnix,
                            "", mirror, false, 0, 0, -1, null, null);
                        continue;
                    }
                    var drawingLines = DrawingStore.Load(db.SymbolDirectory(symbol));
                    if (drawingLines.Length > 0 && drawingLines[^1].Length > 0)
                    {
                        lastVal = drawingLines[^1][^1].Value;
                        lastUnix = drawingLines[^1][^1].UnixSeconds;
                    }
                    slots[i] = new SeriesSlot(
                        new SymbolSeries(symbol, CandleHistory.Build(Array.Empty<Candle>()), color,
                            pipPoints, false, null, transform, source, drawingLines)
                        { PriceMul = configs[i].PriceDiv },
                        mirrorBase, pipPoints, lastVal, false, lastUnix,
                        "", mirror, false, 0, 0, -1, null, null);
                }
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
            var swUi = Stopwatch.StartNew();
            Chart.SetSeries(series);
            var alignExcludedNames = new HashSet<string>(indicators
                .Where(x => IndicatorTypes.IsAverage(x.Type) || IndicatorTypes.IsEntryPoints(x.Type)
                    || IndicatorTypes.IsDeals(x.Type))
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
            SymbolBar.SetCollapsedSources(Chart.CollapsedSources);
            foreach (var s in series)
                SymbolBar.SetSymbolEnabled(s.Symbol, !Chart.HiddenSymbols.Contains(s.Symbol));
            SymbolBar.SetCalendarRow(_calendarEntries.Length > 0, Chart.CalendarVisible);
            SymbolBar.SetWeekendsRow(Chart.WeekendsHidden);
            SymbolBar.SetTiltedGridRow(Chart.TiltedUpGridIndex, Chart.TiltedDownGridIndex);
            _baseInfo = baseInfo;
            ReconcileLive();
            var loader = new SeriesDataLoader(db, Chart, Dispatcher, loaderSeries,
                AppendLog, OnMirrorBaseComputed);
            loader.JobsChanged += () => Dispatcher.BeginInvoke((Action)RefreshLoadIndicator);
            _loader = loader;
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

    private static DealMark[] LoadDealMarks(IndicatorSymbol? ind, string? source)
    {
        if (ind == null || source == null) return Array.Empty<DealMark>();
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
        if (state == null || state.MinutesPerColumn <= 0) return null;
        int k = Math.Max(1, ChartColumns.SnapK(state.MinutesPerColumn));
        long startBucket = state.ViewStartBucket * state.MinutesPerColumn / k;
        long bucketSec = k * 60L;
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
        var candles = db.ReadRange(symbol, last.Value, last.Value);
        return candles.Count > 0 ? candles[0] : null;
    }

    private void OnViewRangeChanged(int k, long startBucket, int widthPx, WeekendCompressor? map)
    {
        long bucketSec = k * 60L;
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
        if (Chart.IsFitView) loader.EnsureFullVisible(Chart.HiddenSymbols);
        else loader.EnsureVisibleRange(lo, hi, Chart.HiddenSymbols);
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
            var (access, refresh) = await OAuthService.ExchangeCodeAsync(_config.ClientId, _config.ClientSecret, code);
            _config.AccessToken = access;
            _config.RefreshToken = refresh;
            _config.Save();
            AppendLog("Access token saved to config");
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
                var found = symbols.FirstOrDefault(s => Normalize(s.SymbolName) == name);
                if (found == null && BrokerAliases.TryGetValue(name, out var aliases))
                    found = aliases
                        .Select(a => symbols.FirstOrDefault(s => Normalize(s.SymbolName) == a))
                        .FirstOrDefault(s => s != null);
                if (found == null)
                {
                    var letters = new string(name.Where(char.IsLetter).Take(3).ToArray());
                    var digits = new string(name.Where(char.IsDigit).ToArray());
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
                AppendLog(Normalize(found.SymbolName) == name
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

    private async Task SubscribeAllAsync()
    {
        var client = _client;
        if (client == null || _symbolIds.Count == 0) return;
        try
        {
            _idToSymbol.Clear();
            _live.Clear();
            foreach (var (name, id) in _symbolIds)
            {
                _idToSymbol[id] = name;
                if (_baseInfo.TryGetValue(name, out var bi))
                    _live[name] = new LiveState
                    {
                        BaseLastUnix = bi.LastUnix,
                        MirrorBase = bi.MirrorBase,
                        PipPoints = bi.PipPoints,
                    };
            }
            await client.SubscribeSpotsAsync(_accountId, _symbolIds.Values.ToList(), CancellationToken.None);
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
        bool queued = false;
        try
        {
            foreach (var range in plan)
            {
                if (!_symbolIds.TryGetValue(range.Symbol, out var symbolId)) continue;
                (int Total, long EarliestUnix) written;
                try
                {
                    written = await Task.Run(() => HistoryDownloader.DownloadRangeToDbAsync(
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
                queued = true;
                QueueAverageRedo(range.Symbol,
                    written.EarliestUnix > 0 ? written.EarliestUnix : range.FromUtc.ToUnixTimeSeconds());
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
        if (queued) await RefreshAveragesAsync();
    }

    private sealed record AverageRefreshJob(
        string Name, string Source, int WindowBars, bool FromFuture, long RedoFromUnix);

    private void QueueAverageRedo(string sourceSymbol, long fromUnix)
    {
        var key = IndicatorSymbol.NameKey(sourceSymbol);
        if (!_pendingAverageRedo.TryGetValue(key, out var earlier) || fromUnix < earlier)
            _pendingAverageRedo[key] = fromUnix;
    }

    private async Task RefreshAveragesAsync()
    {
        if (_dbBusy || _historyCts != null) return;
        var jobs = _config.Indicators
            .Where(x => IndicatorTypes.IsAverage(x.Type))
            .Select(x => new AverageRefreshJob(
                x.Name, x.Source, MovingAverageSymbol.WindowBars(x.Period, x.Unit), x.FromFuture,
                _pendingAverageRedo.TryGetValue(IndicatorSymbol.NameKey(x.Source), out var from) ? from : 0))
            .ToArray();
        var taken = new Dictionary<string, long>(_pendingAverageRedo);
        _pendingAverageRedo.Clear();
        if (jobs.Length == 0) return;
        _dbBusy = true;
        try
        {
            var db = GetDb();
            var done = await Task.Run(() =>
            {
                var result = new List<(string Name, int Minutes)>();
                foreach (var job in jobs)
                {
                    if (db.LastFilledMinuteUtc(job.Name) == null) continue;
                    int written = MovingAverageSymbol.Refresh(
                        db, job.Source, job.Name, job.WindowBars, job.FromFuture, job.RedoFromUnix);
                    if (written > 0) result.Add((job.Name, written));
                }
                return result;
            });
            foreach (var (name, minutes) in done)
                AppendLog($"{name}: auto refreshed {minutes:N0} minutes");
        }
        catch (Exception ex)
        {
            foreach (var (key, from) in taken) QueueAverageRedo(key, from);
            AppendLog("Average auto refresh failed: " + ex.Message);
        }
        finally
        {
            _dbBusy = false;
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
        bool wrote = false;
        bool reload = false;
        try
        {
            var db = GetDb();
            foreach (var (symbol, _, _, _, priceDiv) in SymbolConfigs)
            {
                if (!_symbolIds.TryGetValue(symbol, out var symbolId)) continue;
                var (written, earliest) = await Task.Run(() => recentOnly
                    ? HistoryDownloader.DownloadTailToDbAsync(
                        client, _accountId, symbolId, symbol, db,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, priceDiv)
                    : HistoryDownloader.DownloadM1ToDbAsync(
                        client, _accountId, symbolId, symbol, db,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, priceDiv), ct);
                AppendLog($"{symbol}: {written} minutes written to DB");
                if (written == 0 || earliest == 0) continue;
                wrote = true;
                QueueAverageRedo(symbol, earliest);
            }
            reload = !recentOnly || _baseInfo.Count == 0;
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
        if (wrote) await RefreshAveragesAsync();
        if (reload) await LoadChartAsync();
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

    private void OnSpot(ProtoOASpotEvent spot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_idToSymbol.TryGetValue(spot.SymbolId, out var symbol)) return;
            if (!_firstSpotLogged)
            {
                _firstSpotLogged = true;
                AppendLog($"First live spot received ({symbol})");
            }
            if (spot.HasBid)
            {
                int div = SymbolPriceDiv(symbol);
                int bidPoints = (int)(((long)spot.Bid + div / 2) / div);
                FeedLive(symbol, bidPoints);
                SymbolBar.SetLastPrice(symbol, bidPoints);
                if (symbol == "EURUSD")
                {
                    _lastBid = spot.Bid;
                    BidText.Text = FormatPrice(spot.Bid);
                }
            }
            if (spot.HasAsk && symbol == "EURUSD")
            {
                _lastAsk = spot.Ask;
                AskText.Text = FormatPrice(spot.Ask);
            }
            if (symbol == "EURUSD" && _lastBid.HasValue && _lastAsk.HasValue)
            {
                var pips = ((long)_lastAsk.Value - (long)_lastBid.Value) / 10.0;
                SpreadText.Text = pips.ToString("0.0", CultureInfo.InvariantCulture);
            }
        });
    }

    private void FeedLive(string symbol, int bidPoints)
    {
        if (!_live.TryGetValue(symbol, out var s)) return;
        Chart.SetLastTick(symbol, TransformLivePoint(s, bidPoints));
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
                s.Closed.Add(new Candle(s.MinuteUnix, s.Low, s.High, avg, true));
                GetLiveDb().RecordMinute(symbol, s.MinuteUnix, s.Low, s.High, avg);
            }
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
        var tail = new Candle[s.Closed.Count + (hasCurrent ? 1 : 0)];
        for (int i = 0; i < s.Closed.Count; i++)
        {
            var c = s.Closed[i];
            tail[i] = MakeLiveCandle(s, c.MinuteUnixSeconds, c.Min, c.Max, c.Avg);
        }
        if (hasCurrent)
            tail[^1] = MakeLiveCandle(s, s.MinuteUnix, s.Low, s.High, s.Close);
        Chart.SetLiveTail(symbol, tail);
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
            var tail = new Candle[s.Closed.Count + (hasCurrent ? 1 : 0)];
            for (int i = 0; i < s.Closed.Count; i++)
            {
                var c = s.Closed[i];
                tail[i] = MakeShiftLiveCandle(
                    series.Transform, delta, c.MinuteUnixSeconds, c.Min, c.Max, c.Avg);
            }
            if (hasCurrent)
                tail[^1] = MakeShiftLiveCandle(
                    series.Transform, delta, s.MinuteUnix, s.Low, s.High, s.Close);
            Chart.SetLiveTail(ind.Name, tail);
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

    private static Candle MakeLiveCandle(LiveState s, long minute, int rawLow, int rawHigh, int rawClose)
    {
        int lo = TransformLivePoint(s, rawLow);
        int hi = TransformLivePoint(s, rawHigh);
        int close = TransformLivePoint(s, rawClose);
        return new Candle(minute, Math.Min(lo, hi), Math.Max(lo, hi), close, true);
    }

    private static int TransformLivePoint(LiveState s, int raw)
    {
        long scaled = s.PipPoints == 10 ? raw : CandleTransforms.ScalePoints(raw, s.PipPoints);
        return (int)(s.MirrorBase == 0 ? scaled : s.MirrorBase - scaled);
    }

    private void ReconcileLive()
    {
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
            var (_, mirrorBase, pipPoints) = transforms[i];
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
        if (state == null) return;
        _pendingChartState = null;
        _activeTab.State = state;
        _config.Save();
    }

    private ChartTab _activeTab = null!;
    private bool _tabsReady;

    private ChartViewState? ActiveState => _activeTab.State;

    private void BuildTabs()
    {
        ChartPanelHost.Children.Remove(ChartPanel);
        for (int i = 0; i < _config.Tabs.Count; i++)
            Tabs.Items.Insert(i, MakeTabItem(_config.Tabs[i]));
        _activeTab = _config.Tabs[_config.ActiveTab];
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
        menu.Items.Add(TabMenuItem("Rename...", () => RenameTab(tab)));
        menu.Items.Add(TabMenuItem("Duplicate", () => DuplicateTab(tab)));
        menu.Items.Add(new Separator());
        menu.Items.Add(TabMenuItem("Delete", () => DeleteTab(tab)));
        item.ContextMenu = menu;
        return item;
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
        bool shiftsChanged = ApplyShiftPlacements(tab);
        if (tab.State != null) Chart.RestoreState(tab.State);
        SymbolBar.SetFlattenRow(tab.State?.FlattenSymbol != null);
        SyncSymbolBar();
        _config.Save();
        if (!shiftsChanged) return;
        if (_dbBusy || _historyCts != null)
        {
            AppendLog($"{tab.Name}: shift settings are applied after the running DB operation");
            return;
        }
        _ = LoadChartAsync();
    }

    private void SyncSymbolBar()
    {
        SymbolBar.SetCollapsedSources(Chart.CollapsedSources);
        foreach (var (symbol, _, _) in _seriesTransforms)
            SymbolBar.SetSymbolEnabled(symbol, !Chart.HiddenSymbols.Contains(symbol));
        SymbolBar.SetCalendarRow(_calendarEntries.Length > 0, Chart.CalendarVisible);
        SymbolBar.SetWeekendsRow(Chart.WeekendsHidden);
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
        var item = TabItemOf(tab);
        if (ReferenceEquals(tab, _activeTab))
            Tabs.SelectedItem = Tabs.Items[index + 1 < _config.Tabs.Count ? index + 1 : index - 1];
        _config.Tabs.RemoveAt(index);
        Tabs.Items.Remove(item);
        _config.ActiveTab = _config.Tabs.IndexOf(_activeTab);
        _config.Save();
    }

    private bool ApplyShiftPlacements(ChartTab tab)
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
            if (!SymbolNameEquals(placement.Target(), ind.ShiftTarget())) ClearMirrorBase(ind.Name);
            placement.ApplyTo(ind);
            changed = true;
        }
        tab.Shifts.RemoveAll(x => !known.Contains(IndicatorSymbol.NameKey(x.Name)));
        return changed;
    }

    private void RecordShiftPlacements()
    {
        _activeTab.Shifts.Clear();
        foreach (var ind in _config.Indicators)
            if (IndicatorTypes.IsShift(ind.Type)) _activeTab.Shifts.Add(ShiftPlacement.From(ind));
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
        SymbolBar.SetConnStatus(text, color);
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
        LogBox.ScrollToEnd();
        try { File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine); }
        catch { }
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
            SymbolBar.SetCalendarRow(_calendarEntries.Length > 0, Chart.CalendarVisible);
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
        if (dlg.ShowDialog() != true) return;
        _config.Calendar = dlg.Settings;
        _config.Save();
        Chart.SetCalendarSettings(_config.Calendar);
    }

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
                var (_, minutes) = await Task.Run(
                    () => GenerateIndicatorData(db, ind, cts.Token, null), cts.Token);
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
            var dir = GetDb().SymbolDirectory(symbol);
            var stored = DrawingStore.Load(dir);
            var lines = new PivotPoint[stored.Length + 1][];
            Array.Copy(stored, lines, stored.Length);
            lines[^1] = points;
            DrawingStore.Save(dir, lines);
            Chart.ReplaceDrawing(symbol, lines);
            AppendLog($"{symbol}: line added ({points.Length} points, {lines.Length} lines total)");
        }
        catch (Exception ex)
        {
            AppendLog("Drawing save failed: " + ex.Message);
        }
    }

    private void OnDrawingLinesChanged(string symbol, PivotPoint[][] lines)
    {
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
            refresh, SymbolConfigs.Select(c => c.Symbol).ToList(), indexSources)
        {
            Owner = this,
        };
        dlg.ShowDialog();
    }

    private (int Weeks, int Minutes) GenerateIndicatorData(
        CandleDatabase db, IndicatorSymbol ind, CancellationToken ct, IProgress<double>? progress)
    {
        void Log(string m) => Dispatcher.BeginInvoke(() => AppendLog(m));
        if (IndicatorTypes.IsAverage(ind.Type))
            return MovingAverageSymbol.Generate(db, ind.Source, ind.Name,
                MovingAverageSymbol.WindowBars(ind.Period, ind.Unit), ind.FromFuture, Log, ct, progress);
        if (IndicatorTypes.IsIndex(ind.Type))
            return DollarIndexSymbol.Generate(db, ind.Name, ind.IndexPairs, ind.StartTimeUnix,
                ind.EndTimeUnix, ind.IndexMethod, Log, ct, progress);
        if (IndicatorTypes.IsCurrency(ind.Type))
            return CurrencyIndexSymbol.Generate(db, ind.Name, ind.Source, ind.IndexPair, Log, ct, progress);
        if (IndicatorTypes.IsEntryPoints(ind.Type))
        {
            int pip = SourcePipPoints(ind.Source);
            return EntryPointsSymbol.Generate(db, ind.Source, ind.Name,
                ind.StopLossPips * pip, ind.TakeProfitPips * pip, Log, ct, progress);
        }
        return (0, ZigZagSymbol.Generate(db, ind.Source, ind.Name, ZigZagLimitsOf(ind),
            Log, ct, progress));
    }

    private static ZigZagLimits ZigZagLimitsOf(IndicatorSymbol ind)
    {
        int pip = SourcePipPoints(ind.Source);
        return new ZigZagLimits(ind.Limit1Pips * pip, ind.Limit2Pips * pip, ind.Limit2DelayMinutes);
    }

    private sealed record CurrencyRefreshJob(string Name, string IndexSymbol, string Pair, long EndUnix);

    private CurrencyRefreshJob[] DependentCurrencyJobs(IndicatorSymbol saved)
    {
        if (!IndicatorTypes.IsIndex(saved.Type)) return Array.Empty<CurrencyRefreshJob>();
        return _config.Indicators
            .Where(x => IndicatorTypes.IsCurrency(x.Type) && SymbolNameEquals(x.Source, saved.Name))
            .Select(x => new CurrencyRefreshJob(
                x.Name, x.Source, x.IndexPair, ConfirmedEndUnix(new[] { x.IndexPair }, 0)))
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
            var (_, minutes) = await Task.Run(
                () => GenerateIndicatorData(db, saved, CancellationToken.None, progress));
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
            var dependents = DependentCurrencyJobs(saved);
            await Task.Run(() =>
            {
                if (IndicatorTypes.IsAverage(saved.Type))
                {
                    int window = MovingAverageSymbol.WindowBars(saved.Period, saved.Unit);
                    MovingAverageSymbol.Refresh(db, saved.Source, saved.Name, window, saved.FromFuture, 0,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                }
                else if (IndicatorTypes.IsIndex(saved.Type))
                {
                    DollarIndexSymbol.Refresh(db, saved.Name, saved.IndexPairs, saved.StartTimeUnix,
                        indexEndUnix, saved.IndexMethod,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    foreach (var dep in dependents)
                        CurrencyIndexSymbol.Refresh(db, dep.Name, dep.IndexSymbol, dep.Pair, dep.EndUnix,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct);
                }
                else if (IndicatorTypes.IsCurrency(saved.Type))
                {
                    CurrencyIndexSymbol.Refresh(db, saved.Name, saved.Source, saved.IndexPair,
                        currencyEndUnix,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                }
                else if (IndicatorTypes.IsEntryPoints(saved.Type))
                {
                    int pip = SourcePipPoints(saved.Source);
                    EntryPointsSymbol.Refresh(db, saved.Source, saved.Name,
                        saved.StopLossPips * pip, saved.TakeProfitPips * pip,
                        m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
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
                    else if (IndicatorTypes.IsShift(def.Type) || IndicatorTypes.IsDeals(def.Type))
                    {
                        db.DeleteSymbol(def.Name);
                        progress.Report(1.0);
                    }
                    else if (IndicatorTypes.IsAverage(def.Type))
                    {
                        int window = MovingAverageSymbol.WindowBars(def.Period, def.Unit);
                        MovingAverageSymbol.Generate(db, def.Source, def.Name, window, def.FromFuture,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    }
                    else if (IndicatorTypes.IsIndex(def.Type))
                    {
                        DollarIndexSymbol.Generate(db, def.Name, def.IndexPairs, def.StartTimeUnix,
                            def.EndTimeUnix, def.IndexMethod,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    }
                    else if (IndicatorTypes.IsCurrency(def.Type))
                    {
                        CurrencyIndexSymbol.Generate(db, def.Name, def.Source, def.IndexPair,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
                    }
                    else if (IndicatorTypes.IsEntryPoints(def.Type))
                    {
                        int pip = SourcePipPoints(def.Source);
                        EntryPointsSymbol.Generate(db, def.Source, def.Name,
                            def.StopLossPips * pip, def.TakeProfitPips * pip,
                            m => Dispatcher.BeginInvoke(() => AppendLog(m)), ct, progress);
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
            if (idx >= 0) _config.Indicators[idx] = def;
            else _config.Indicators.Add(def);
            if (nameChanged && IsStandaloneIndicator(def))
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
                    if (cfg.IsShift || cfg.IsDeals) continue;
                    var years = db.ExistingYears(symbol);
                    if (years.Count == 0)
                    {
                        res.Add($"{symbol}: no data");
                        continue;
                    }
                    foreach (var y in years)
                        res.Add($"{symbol} {y}: {db.CountFilled(symbol, y):N0} filled minutes");
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
