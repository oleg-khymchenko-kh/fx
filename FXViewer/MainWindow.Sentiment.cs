using System.IO;
using System.Windows.Threading;
using FXViewer.Chart;
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow
{
    private static readonly TimeSpan SentimentReloadDelay = TimeSpan.FromSeconds(2);
    private const string SentimentFilePattern = "*.csv";

    private FileSystemWatcher? _sentimentWatcher;
    private DispatcherTimer? _sentimentTimer;
    private bool _sentimentReloading;
    private bool _sentimentReloadPending;

    private void InitSentiment()
    {
        var timer = new DispatcherTimer { Interval = SentimentReloadDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = ReloadSentimentAsync();
        };
        _sentimentTimer = timer;
        try
        {
            Directory.CreateDirectory(DbRoot);
            var watcher = new FileSystemWatcher(DbRoot, SentimentFilePattern)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnSentimentFileEvent;
            watcher.Changed += OnSentimentFileEvent;
            watcher.Deleted += OnSentimentFileEvent;
            watcher.Renamed += OnSentimentFileEvent;
            watcher.Error += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                AppendLog("Sentiment: folder watch error, reloading: " + e.GetException().Message);
                ScheduleSentimentReload();
            });
            watcher.EnableRaisingEvents = true;
            _sentimentWatcher = watcher;
        }
        catch (Exception ex)
        {
            AppendLog("Sentiment: cannot watch " + DbRoot + ": " + ex.Message);
        }
    }

    private void StopSentimentWatch()
    {
        _sentimentTimer?.Stop();
        _sentimentWatcher?.Dispose();
        _sentimentWatcher = null;
    }

    private void OnSentimentFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsSentimentFile(e.FullPath)) return;
        Dispatcher.BeginInvoke((Action)ScheduleSentimentReload);
    }

    private static bool IsSentimentFile(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder == null) return false;
        string? parent = Path.GetDirectoryName(folder);
        return parent != null
            && string.Equals(Path.GetFileName(parent), "sentiment", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleSentimentReload()
    {
        if (_sentimentTimer == null) return;
        _sentimentTimer.Stop();
        _sentimentTimer.Start();
    }

    private sealed record SentimentTarget(string Name, string Source, bool BigPlayers, double MinLots);

    private async Task ReloadSentimentAsync()
    {
        if (_sentimentReloading)
        {
            _sentimentReloadPending = true;
            return;
        }
        _sentimentReloading = true;
        try
        {
            do
            {
                _sentimentReloadPending = false;
                if (_db is not { } db) return;
                var targets = _config.Indicators
                    .Where(x => IndicatorTypes.IsSentiment(x.Type) || IndicatorTypes.IsBigPlayers(x.Type))
                    .Select(x => new SentimentTarget(
                        x.Name, x.Source, IndicatorTypes.IsBigPlayers(x.Type), x.BigMinLots))
                    .ToList();
                if (targets.Count == 0) return;
                var loaded = await Task.Run(() => targets
                    .Select(t => (Target: t, Data: LoadSentimentSeries(db, t.Source)))
                    .ToList());
                foreach (var (target, data) in loaded)
                {
                    if (ReferenceEquals(Chart.GetSeries(target.Name)?.Sentiment, data)) continue;
                    if (target.BigPlayers)
                    {
                        var bigPlayers = BigPlayersSeries.Build(data, target.MinLots);
                        if (!Chart.SetBigPlayers(target.Name, data, bigPlayers)) continue;
                        AppendLog(BigPlayersLogLine(target.Name, target.Source, bigPlayers).TrimStart()
                            + " (reloaded)");
                        continue;
                    }
                    if (!Chart.SetSentiment(target.Name, data)) continue;
                    var (lots, positions) = SentimentLastShares(data);
                    SymbolBar.SetSentimentLast(target.Name, lots, positions);
                    AppendLog(SentimentLogLine(target.Name, target.Source, data).TrimStart() + " (reloaded)");
                }
            }
            while (_sentimentReloadPending);
        }
        catch (Exception ex)
        {
            AppendLog("Sentiment: reload failed: " + ex.Message);
        }
        finally
        {
            _sentimentReloading = false;
        }
    }

    private SentimentSeries LoadSentimentSeries(CandleDatabase db, string? source)
    {
        if (string.IsNullOrEmpty(source)) return SentimentSeries.Empty;
        string folder = SentimentStore.Directory(db.SymbolDirectory(source));
        return (SentimentSeries?)CachedStore(
            $"sentiment:{IndicatorSymbol.NameKey(source)}",
            FolderStamp(folder, SentimentFilePattern),
            () => SentimentSeries.Load(folder))
            ?? SentimentSeries.Empty;
    }

    private static (double Lots, double Positions) SentimentLastShares(SentimentSeries data) =>
        data.Last is { } last
            ? (last.LongByLots * 100.0, float.IsNaN(last.LongByPositions) ? double.NaN : last.LongByPositions * 100.0)
            : (double.NaN, double.NaN);

    private static string SentimentLogLine(string symbol, string? source, SentimentSeries data)
    {
        if (data.Count == 0) return $"  {symbol}: no sentiment files for {source}";
        var last = data.Samples[^1];
        var steps = data.Samples.Select(s => s.StepMinutes).Distinct().OrderBy(x => x)
            .Select(FormatSentimentStep);
        return $"  {symbol}: {data.Count:N0} sentiment sample(s) for {source} " +
            $"({string.Join(", ", steps)}), last {FmtUtc(last.FromUnix)} UTC " +
            $"{Math.Round(last.LongByLots * 100)}% long by lots, " +
            $"{Math.Round(last.LongByPositions * 100)}% by positions";
    }

    private static string BigPlayersLogLine(string symbol, string? source, BigPlayersSeries data)
    {
        if (data.Count == 0)
            return $"  {symbol}: no big player events for {source} at {data.MinLots:0.#} lots or more";
        var last = data.Events[^1];
        double buy = 0;
        double sell = 0;
        foreach (var e in data.Events)
        {
            buy += e.Buy;
            sell += e.Sell;
        }
        return $"  {symbol}: {data.Count:N0} big player event(s) for {source} at {data.MinLots:0.#} lots or more, " +
            $"{buy:N0} lots bought, {sell:N0} sold, last {FmtUtc(last.TimeUnix)} UTC " +
            $"{last.Buy:0.#} buy / {last.Sell:0.#} sell";
    }

    private static string FormatSentimentStep(int minutes) =>
        minutes % 43200 == 0 ? minutes / 43200 + "mn"
        : minutes % 10080 == 0 ? minutes / 10080 + "w"
        : minutes % 1440 == 0 ? minutes / 1440 + "d"
        : minutes % 60 == 0 ? minutes / 60 + "h"
        : minutes + "m";
}
