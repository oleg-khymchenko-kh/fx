using System.Globalization;
using System.Security.Cryptography;
using System.Windows.Threading;
using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Game;
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow
{
    private const string GameDrawingSeparator = " · ";
    private const double DayDrawingShade = 0.55;

    private readonly Dictionary<string, GameDayStore> _gameDayStores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GameLogEntry>> _gameLogs = new(StringComparer.Ordinal);
    private DispatcherTimer? _gameClock;
    private bool _dayStartBusy;

    private GameDayStore DayStore(string mode)
    {
        if (!_gameDayStores.TryGetValue(mode, out var store))
        {
            store = GameDayStore.Load(mode);
            _gameDayStores[mode] = store;
        }
        return store;
    }

    private List<GameLogEntry> GameLogOf(string mode)
    {
        if (!_gameLogs.TryGetValue(mode, out var log))
        {
            log = GameLogStore.Load(mode);
            _gameLogs[mode] = log;
        }
        return log;
    }

    private static string RandomMenuName(string mode) => "Play random " + GameModes.Title(mode);

    private Task PlayRandomDayAsync(string mode) =>
        RunDayStartAsync(RandomMenuName(mode), () => StartRandomDayAsync(mode));

    private Task PlayNextDayAsync() => RunDayStartAsync("Play next day", StartNextDayAsync);

    private Task ReplayDayAsync() => RunDayStartAsync("Replay day", StartReplayDayAsync);

    private async Task RunDayStartAsync(string name, Func<Task> start)
    {
        if (_dayStartBusy) return;
        _dayStartBusy = true;
        try
        {
            await start();
        }
        catch (Exception ex)
        {
            AppendLog(name + " failed: " + ex.Message);
        }
        finally
        {
            _dayStartBusy = false;
        }
    }

    private async Task<List<string>?> DayGamePairsAsync(string name, DateOnly first, DateOnly last, string reason)
    {
        var pendingLoad = _chartLoadTask;
        if (pendingLoad != null && !pendingLoad.IsCompleted)
        {
            AppendLog(name + ": chart load is running, try again when it is done");
            return null;
        }
        var tab = _activeTab;
        var pairs = Chart.VisiblePairs();
        if (pairs.Count == 0)
        {
            AppendLog(name + " needs a visible pair on the chart");
            return null;
        }
        var loader = _loader;
        if (loader != null)
            await Task.WhenAll(pairs.Select(p => loader.EnsureYearsAsync(p, first.Year, last.Year, reason)));
        if (!ReferenceEquals(tab, _activeTab))
        {
            AppendLog(name + ": the tab changed while the history was loading, nothing started");
            return null;
        }
        return pairs;
    }

    private async Task StartNextDayAsync()
    {
        if (ActiveGame is not { } game || GameDayPicker.Parse(game.Day) is not { } played)
        {
            AppendLog("Play next day: no day game in this tab");
            return;
        }
        string mode = game.Mode;
        var today = GameToday();
        var last = GameDayPicker.LastDay(today);
        if (!GameDayPicker.HasNextDay(played, today))
        {
            AppendLog($"Play next day: the period ends {DayTitle(last)}, no day after {DayTitle(played)}");
            return;
        }
        var first = played.AddDays(1);
        var pairs = await DayGamePairsAsync("Play next day", first, last, "next day");
        if (pairs == null) return;
        var next = GameDayPicker.NextDay(played, last, pairs.Select(GameMinutes).ToList(), mode);
        if (next == null)
        {
            AppendLog($"Play next day: no day with candles from {GameDayPicker.Key(first)} " +
                $"to {GameDayPicker.Key(last)} for {string.Join(", ", pairs)}");
            return;
        }
        StartDayGame(next.Value, mode);
        AppendLog($"Play next day {DayTitle(next.Value)} after {DayTitle(played)}, " +
            $"{GameModes.Title(mode)} game: {string.Join(", ", pairs)}");
    }

    private async Task StartRandomDayAsync(string mode)
    {
        string name = RandomMenuName(mode);
        var first = GameDayPicker.FirstDay;
        var last = GameDayPicker.LastDay(GameToday());
        var pairs = await DayGamePairsAsync(name, first, last, "random day");
        if (pairs == null) return;
        var days = GameDayPicker.Candidates(first, last, pairs.Select(GameMinutes).ToList(), mode);
        var pick = GameDayPicker.Pick(days, GameLogStore.PlayCounts(GameLogOf(mode)),
            RandomNumberGenerator.GetInt32);
        if (pick == null)
        {
            AppendLog($"{name}: no day with candles from {GameDayPicker.Key(first)} " +
                $"to {GameDayPicker.Key(last)} for {string.Join(", ", pairs)}");
            return;
        }
        StartDayGame(pick.Day, mode);
        AppendLog($"{name} {DayTitle(pick.Day)}: {string.Join(", ", pairs)}; " +
            $"period {GameDayPicker.Key(first)} .. {GameDayPicker.Key(last)}, {days.Count} trading days " +
            $"({GameDayPicker.Key(days[0])} .. {GameDayPicker.Key(days[^1])}), " +
            $"drawn #{pick.Index} of 0..{pick.Pool - 1} among the days played {pick.Plays} time(s)");
        new GameDayPickWindow(name, first, last, days.Count, pick) { Owner = this }.ShowDialog();
    }

    private async Task StartReplayDayAsync()
    {
        if (ActiveGame is not { } game || GameDayPicker.Parse(game.Day) is not { } day)
        {
            AppendLog("Replay day: no day game in this tab");
            return;
        }
        var pairs = await DayGamePairsAsync("Replay day", day.AddDays(-1), day, "replay day");
        if (pairs == null) return;
        if (!ReferenceEquals(ActiveGame, game))
        {
            AppendLog("Replay day: the game changed while the history was loading, nothing started");
            return;
        }
        StartDayGame(day, game.Mode, true);
        AppendLog($"Replay {GameModes.Title(game.Mode)} {DayTitle(day)}: {string.Join(", ", pairs)}; " +
            "not written to the game log, the draft drawing is kept");
    }

    private void StartDayGame(DateOnly date, string mode, bool replay = false)
    {
        string day = GameDayPicker.Key(date);
        long start = GameDayPicker.StartUnix(date, mode) - ChartColumns.MinuteSeconds;
        var old = _activeTab.Game;
        _gameWindow?.Panel.FlushComment();
        _activeTab.Game = new GameState
        {
            Playing = true,
            Day = day,
            Mode = mode,
            Replay = replay,
            StartUnix = start,
            TimeUnix = start,
            EndUnix = GameDayPicker.EndUnix(date),
            StartedAt = GameClockNow(),
            StopPips = old?.StopPips ?? GameState.DefaultPips,
            TakePips = old?.TakePips ?? GameState.DefaultPips,
        };
        if (!replay)
        {
            DayStore(mode).ClearDraft(day);
            SaveGameDays(mode);
        }
        _gameDirty = true;
        RefreshGame(true);
    }

    private void FinishDayNow()
    {
        var game = ActiveGame;
        if (game == null) return;
        if (!game.IsDayGame())
        {
            StepPlay(1);
            return;
        }
        if (game.Finished)
        {
            if (!game.FutureOpen) ToggleFuture();
            return;
        }
        AppendLog($"Play: {GameModes.Title(game.Mode)} {DayTitle(game.Day)} played to the end from " +
            $"{GameTimeText(game.TimeUnix)}, the rest of the day is shown");
        game.FutureOpen = true;
        FinishDay(game, false);
    }

    private void FinishDay(GameState game, bool follow = true)
    {
        game.TimeUnix = game.EndUnix;
        foreach (var book in GameBooks(game))
            if (book.Result.Positions.Count > 0 || book.Result.Orders.Count > 0)
                game.Add(new GameAction { Kind = GameActions.DayEnd, Symbol = book.Symbol, AtUnix = game.EndUnix });
        game.Finished = true;
        game.EndedAt = GameClockNow();
        game.NotesOpen = true;
        var books = GameBooks(game);
        var entry = GameLogEntryOf(game, books);
        if (!game.Replay)
        {
            if (_timeSession != null) _timeSession.Games++;
            try
            {
                var log = GameLogOf(game.Mode);
                GameLogStore.Append(game.Mode, entry);
                log.Add(entry);
            }
            catch (Exception ex)
            {
                AppendLog("Game log write failed: " + ex.Message);
            }
        }
        _gameDirty = true;
        RefreshGame(follow);
        if (!game.Replay) RefreshGameStats(game.Mode);
        AppendLog($"{(game.Replay ? "Replay" : "Play")}: {GameModes.Title(game.Mode)} " +
            $"{DayTitle(game.Day)} finished at " +
            $"{GameTimeText(game.EndUnix)}, {PipsText(books.Sum(b => b.Result.ClosedPips))} pips " +
            $"in {entry.Trades.Count} trade(s), real time {ElapsedText(game)}" +
            (game.Replay ? ", not in the game log" : ""));
    }

    private void ToggleDayNotes()
    {
        var game = ActiveGame;
        if (game == null || !game.IsDayGame()) return;
        game.NotesOpen = !game.NotesOpen;
        _gameDirty = true;
        RefreshGame();
    }

    private void SaveDayComment(string day, string text)
    {
        string mode = ActiveGame?.Mode ?? GameModes.Day;
        var store = DayStore(mode);
        if (store.Comment(day) == text) return;
        store.SetComment(day, text);
        SaveGameDays(mode);
    }

    private void SaveGameDays(string mode)
    {
        try { DayStore(mode).Save(); }
        catch (Exception ex) { AppendLog("Game days save failed: " + ex.Message); }
    }

    private List<SymbolSeries> GameDrawingSeries(GameState game)
    {
        var list = new List<SymbolSeries>();
        if (!game.IsDayGame()) return list;
        foreach (var pair in Chart.VisiblePairs())
        {
            var source = Chart.GetSeries(pair);
            if (source?.Transform == null) continue;
            list.Add(GameDrawingOf(game, GameDrawingLayers.Draft, source, source.ColorArgb));
            if (game.NotesOpen)
                list.Add(GameDrawingOf(game, GameDrawingLayers.Day, source,
                    Shade(source.ColorArgb, DayDrawingShade)));
        }
        return list;
    }

    private SymbolSeries GameDrawingOf(GameState game, string layer, SymbolSeries source, int color) =>
        new(source.Symbol + GameDrawingSeparator + layer, CandleHistory.Build(Array.Empty<Candle>()), color,
            source.PipPoints, false, null, source.Transform, source.Symbol,
            DayStore(game.Mode).Lines(game.Day, layer, source.Symbol))
        {
            PriceMul = source.PriceMul,
            GameDrawing = layer,
        };

    private static int Shade(int argb, double factor)
    {
        int r = (int)(((argb >> 16) & 0xFF) * factor);
        int g = (int)(((argb >> 8) & 0xFF) * factor);
        int b = (int)((argb & 0xFF) * factor);
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    private bool TryCommitGameLine(string symbol, PivotPoint[] points)
    {
        var series = Chart.GetSeries(symbol);
        if (series?.GameDrawing == null) return false;
        var stored = series.DrawingLines ?? Array.Empty<PivotPoint[]>();
        var lines = new PivotPoint[stored.Length + 1][];
        Array.Copy(stored, lines, stored.Length);
        lines[^1] = points;
        SaveGameLines(series, lines);
        return true;
    }

    private bool TrySaveGameLines(string symbol, PivotPoint[][] lines)
    {
        var series = Chart.GetSeries(symbol);
        if (series?.GameDrawing == null) return false;
        SaveGameLines(series, lines);
        return true;
    }

    private void SaveGameLines(SymbolSeries series, PivotPoint[][] lines)
    {
        var game = ActiveGame;
        if (game == null || !game.IsDayGame() || series.SourceSymbol == null || series.GameDrawing == null)
        {
            AppendLog($"{series.Symbol}: no random day game in this tab, line discarded");
            return;
        }
        DayStore(game.Mode).SetLines(game.Day, series.GameDrawing, series.SourceSymbol, lines);
        SaveGameDays(game.Mode);
        Chart.ReplaceDrawing(series.Symbol, lines);
    }

    private void RefreshGameClock(GameState? game)
    {
        if (game is not { Playing: true })
        {
            _gameClock?.Stop();
            return;
        }
        if (_gameClock == null)
        {
            _gameClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _gameClock.Tick += (_, _) => GameClockTick();
        }
        _gameClock.Start();
    }

    private void GameClockTick()
    {
        var game = ActiveGame;
        if (game == null)
        {
            _gameClock?.Stop();
            return;
        }
        TickGameTime();
        var panel = _gameWindow?.Panel;
        if (panel == null) return;
        if (game is { Finished: false, StartedAt: not null }) panel.SetElapsed(ElapsedText(game));
        panel.SetOwnTime(OwnTimeText());
        panel.SetPlayed(PlayedText(game));
    }

    private static DateTimeOffset GameClockNow()
    {
        var now = DateTimeOffset.Now;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Offset);
    }

    private static string ElapsedText(GameState game) => game.StartedAt is { } started
        ? ClockText(((game.EndedAt ?? DateTimeOffset.Now) - started).TotalSeconds)
        : "";

    private static bool HasNextDay(GameState game) =>
        GameDayPicker.Parse(game.Day) is { } day && GameDayPicker.HasNextDay(day, GameToday());

    private static DateOnly GameToday() => DateOnly.FromDateTime(DateTime.Today);

    private static string GamePanelTitle(GameState game) =>
        (GameModes.IsAfternoon(game.Mode) ? GameModes.Title(game.Mode) : "Day") + " " + DayTitle(game.Day);

    private static string DayTitle(string day) =>
        GameDayPicker.Parse(day) is { } date ? DayTitle(date) : day;

    private static string DayTitle(DateOnly day) => day.ToString("dd-MMM-yy ddd", CultureInfo.InvariantCulture);

    private GameLogEntry GameLogEntryOf(GameState game, List<GameBook> books)
    {
        var started = game.StartedAt ?? GameClockNow();
        var ended = game.EndedAt ?? GameClockNow();
        var trades = books
            .SelectMany(b => b.Result.Trades.Select(t => (b.Symbol, Trade: t)))
            .OrderBy(x => x.Trade.OpenUnix)
            .ThenBy(x => x.Trade.Id)
            .Select(x => new GameLogTrade
            {
                Id = x.Trade.Id,
                Symbol = x.Symbol,
                Side = SideText(x.Trade.Buy),
                OpenUtc = GameLogTime(x.Trade.OpenUnix),
                OpenPrice = GamePriceValue(x.Symbol, x.Trade.OpenPrice),
                CloseUtc = GameLogTime(x.Trade.CloseUnix),
                ClosePrice = GamePriceValue(x.Symbol, x.Trade.ClosePrice),
                Reason = x.Trade.Reason,
                Pips = Math.Round(x.Trade.Pips, 1),
            })
            .ToList();
        int wins = trades.Count(t => t.Pips > 0);
        return new GameLogEntry
        {
            Day = game.Day,
            StartedAt = started,
            EndedAt = ended,
            Seconds = (long)Math.Max(0, (ended - started).TotalSeconds),
            ActiveSeconds = (long)Math.Round(game.ActiveSeconds),
            FromUtc = GameLogTime(game.StartUnix),
            ToUtc = GameLogTime(game.EndUnix),
            Pairs = books.Select(b => b.Symbol).ToList(),
            Pips = Math.Round(books.Sum(b => b.Result.ClosedPips), 1),
            Wins = wins,
            Losses = trades.Count - wins,
            Trades = trades,
            Actions = game.Actions.Where(a => a.Symbol.Length > 0).Select(LogActionOf).ToList(),
        };
    }

    private static GameLogAction LogActionOf(GameAction action)
    {
        bool opens = action.Kind is GameActions.Market or GameActions.Order;
        return new GameLogAction
        {
            TimeUtc = GameLogTime(action.AtUnix),
            Kind = action.Kind,
            Symbol = action.Symbol,
            Side = opens ? SideText(action.Buy) : "",
            Price = action.Kind == GameActions.Order ? GamePriceValue(action.Symbol, action.Price) : 0,
            StopPips = opens ? action.StopPips : 0,
            TakePips = opens ? action.TakePips : 0,
            Id = action.Id,
            Target = action.Target,
        };
    }

    private static string SideText(bool buy) => buy ? "buy" : "sell";

    private static string GameLogTime(long unixSeconds) => DateTimeOffset
        .FromUnixTimeSeconds(unixSeconds).UtcDateTime
        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static double GamePriceValue(string symbol, int rawPoints) =>
        Math.Round(rawPoints * (double)SymbolPriceDiv(symbol) / 100000.0, 5);
}
