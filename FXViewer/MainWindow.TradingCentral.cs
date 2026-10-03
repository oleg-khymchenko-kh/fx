using System.IO;
using System.Windows.Threading;
using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow
{
    private static readonly TimeSpan TradingCentralReloadDelay = TimeSpan.FromMilliseconds(500);

    private FileSystemWatcher? _tradingCentralWatcher;
    private DispatcherTimer? _tradingCentralTimer;
    private bool _tradingCentralReloading;
    private bool _tradingCentralReloadPending;

    private static string TradingCentralFolder =>
        Path.Combine(DbRoot, TradingCentralStore.DefaultFolder);

    private void InitTradingCentral()
    {
        var timer = new DispatcherTimer { Interval = TradingCentralReloadDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = ReloadTradingCentralAsync();
        };
        _tradingCentralTimer = timer;
        Chart.TradingCentralFolder = TradingCentralFolder;
        try
        {
            Directory.CreateDirectory(TradingCentralFolder);
            var watcher = new FileSystemWatcher(TradingCentralFolder, TradingCentralStore.FilePattern)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnTradingCentralFileEvent;
            watcher.Changed += OnTradingCentralFileEvent;
            watcher.Deleted += OnTradingCentralFileEvent;
            watcher.Renamed += OnTradingCentralFileEvent;
            watcher.Error += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                AppendLog("Trading Central: folder watch error, reloading: " + e.GetException().Message);
                ScheduleTradingCentralReload();
            });
            watcher.EnableRaisingEvents = true;
            _tradingCentralWatcher = watcher;
        }
        catch (Exception ex)
        {
            AppendLog("Trading Central: cannot watch " + TradingCentralFolder + ": " + ex.Message);
        }
    }

    private void StopTradingCentralWatch()
    {
        _tradingCentralTimer?.Stop();
        _tradingCentralWatcher?.Dispose();
        _tradingCentralWatcher = null;
    }

    private void OnTradingCentralFileEvent(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke((Action)ScheduleTradingCentralReload);

    private void ScheduleTradingCentralReload()
    {
        if (_tradingCentralTimer == null) return;
        _tradingCentralTimer.Stop();
        _tradingCentralTimer.Start();
    }

    private async Task ReloadTradingCentralAsync()
    {
        if (_tradingCentralReloading)
        {
            _tradingCentralReloadPending = true;
            return;
        }
        _tradingCentralReloading = true;
        try
        {
            do
            {
                _tradingCentralReloadPending = false;
                var targets = _config.Indicators
                    .Where(x => IndicatorTypes.IsTradingCentral(x.Type))
                    .Select(x => (x.Name, x.Source))
                    .ToList();
                if (targets.Count == 0) return;
                var loaded = await Task.Run(() => targets
                    .Select(t => (t.Name, t.Source, Marks: LoadTradingCentralMarks(t.Source)))
                    .ToList());
                foreach (var (name, source, marks) in loaded)
                {
                    if (ReferenceEquals(Chart.GetSeries(name)?.TradingCentralMarks, marks)) continue;
                    if (!Chart.SetTradingCentralMarks(name, marks)) continue;
                    if (TradingCentralMarks.LastPivot(marks) is { } lastPivot)
                        SymbolBar.SetLastPrice(name, lastPivot.Value);
                    AppendLog(TradingCentralLogLine(name, source, marks).TrimStart() + " (reloaded)");
                }
            }
            while (_tradingCentralReloadPending);
        }
        catch (Exception ex)
        {
            AppendLog("Trading Central: reload failed: " + ex.Message);
        }
        finally
        {
            _tradingCentralReloading = false;
        }
    }

    private TradingCentralMark[] LoadTradingCentralMarks(string? source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<TradingCentralMark>();
        return (TradingCentralMark[]?)CachedStore(
            $"tradingcentral:{IndicatorSymbol.NameKey(source)}",
            FolderStamp(TradingCentralFolder, TradingCentralStore.FilePattern),
            () => TradingCentralMarks.Build(
                TradingCentralStore.LoadFolder(TradingCentralFolder), source, SymbolPriceDiv(source)))
            ?? Array.Empty<TradingCentralMark>();
    }

    private static string TradingCentralLogLine(
        string symbol, string? source, TradingCentralMark[] marks)
    {
        int sets = TradingCentralMarks.SetCount(marks);
        if (sets == 0) return $"  {symbol}: no Trading Central levels for {source}";
        var last = TradingCentralMarks.LastPivot(marks)!;
        return $"  {symbol}: {marks.Length} Trading Central level(s) for {source} in {sets} set(s), " +
            $"last set {FmtUtc(last.FromUnix)} UTC";
    }
}
