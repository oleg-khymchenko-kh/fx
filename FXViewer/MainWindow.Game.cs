using System.Globalization;
using System.Windows;
using FXViewer.Chart;
using FXViewer.Game;
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow
{
    private bool _gameDirty;

    private GamePanelWindow? _gameWindow;

    private sealed record GameBook(string Symbol, int ColorArgb, bool Mirror, bool Visible, int PipPoints,
        GameResult Result);

    private void InitGame()
    {
        Chart.PlayToggleRequested += TogglePlay;
        Chart.PlayRandomDayRequested += mode => _ = PlayRandomDayAsync(mode);
        Chart.PlayNextDayRequested += () => _ = PlayNextDayAsync();
        Chart.PlayReplayDayRequested += () => _ = ReplayDayAsync();
        Chart.PlayFinishDayRequested += FinishDayNow;
        Chart.GameStatsRequested += mode => _ = ShowGameStatsAsync(mode, true);
        Chart.PlayStepRequested += StepPlay;
        Chart.PlayOrderRequested += OpenPlayOrder;
        Chart.SeriesReplaced += symbol =>
        {
            if (ActiveGame != null && Chart.GetSeries(symbol)?.BasePair == true) RefreshGame();
        };
    }

    private GamePanelWindow GameWindow()
    {
        if (_gameWindow != null) return _gameWindow;
        var window = new GamePanelWindow(this);
        window.Panel.MarketRequested += PlayMarket;
        window.Panel.StepRequested += StepPlay;
        window.Panel.CloseRequested += ClosePlayPosition;
        window.Panel.CancelRequested += CancelPlayOrder;
        window.Panel.PipsChanged += SetPlayPips;
        window.Panel.StopPlayRequested += () => TogglePlay(0);
        window.Panel.NotesToggled += ToggleDayNotes;
        window.Panel.FutureToggled += ToggleFuture;
        window.Panel.ReplayRequested += () => _ = ReplayDayAsync();
        window.Panel.FinishDayRequested += FinishDayNow;
        window.Panel.CommentChanged += SaveDayComment;
        window.StepRequested += StepPlay;
        window.FinishDayRequested += FinishDayNow;
        window.Moved += (left, top) =>
        {
            _config.GamePanelLeft = left;
            _config.GamePanelTop = top;
            _config.Save();
        };
        _gameWindow = window;
        return window;
    }

    private GameState? ActiveGame => _activeTab.Game is { Playing: true } game ? game : null;

    private Candle[] GameMinutes(string symbol) =>
        Chart.GetSeries(symbol)?.History.Minutes ?? Array.Empty<Candle>();

    private static List<string> GameSymbols(GameState game, IEnumerable<string> visible)
    {
        var symbols = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in visible.Concat(game.Actions.Select(a => a.Symbol)))
            if (symbol.Length > 0 && keys.Add(IndicatorSymbol.NameKey(symbol))) symbols.Add(symbol);
        return symbols;
    }

    private int GamePairColor(string symbol)
    {
        var series = Chart.GetSeries(symbol);
        if (series != null) return series.ColorArgb;
        foreach (var c in SymbolConfigs)
            if (SymbolNameEquals(c.Symbol, symbol)) return PairColorOf(c.Symbol, c.ColorArgb);
        return unchecked((int)0xFF707070);
    }

    private static bool GamePairMirror(string symbol) => PairDisplay(symbol)?.Mirror ?? false;

    private void RefreshGameIfPlaying()
    {
        if (ActiveGame != null) RefreshGame();
    }

    private void TogglePlay(long timeUnix)
    {
        var old = _activeTab.Game;
        if (old is { Playing: true })
        {
            old.Playing = false;
            _gameDirty = true;
            RefreshGame();
            AppendLog($"Play off at {GameTimeText(old.TimeUnix)}");
            return;
        }
        var pairs = Chart.VisiblePairs();
        if (pairs.Count == 0)
        {
            AppendLog("Play needs a visible pair on the chart");
            return;
        }
        long? start = GameSim.SnapTime(pairs.Select(GameMinutes), timeUnix);
        if (start == null)
        {
            AppendLog("Play: the visible pairs have no candles loaded here");
            return;
        }
        _gameWindow?.Panel.FlushComment();
        _activeTab.Game = new GameState
        {
            Playing = true,
            StartUnix = start.Value,
            TimeUnix = start.Value,
            StartedAt = GameClockNow(),
            StopPips = old?.StopPips ?? GameState.DefaultPips,
            TakePips = old?.TakePips ?? GameState.DefaultPips,
        };
        _gameDirty = true;
        RefreshGame(true);
        AppendLog($"Play on from {GameTimeText(start.Value)}: {string.Join(", ", pairs)}");
    }

    private void StepPlay(int direction)
    {
        var game = ActiveGame;
        if (game == null || game.Finished) return;
        var symbols = GameSymbols(game, Chart.VisiblePairs());
        long next = GameSim.StepTime(symbols.Select(GameMinutes), game.TimeUnix, direction);
        if (next == game.TimeUnix) return;
        if (game.EndUnix > 0 && next >= game.EndUnix)
        {
            FinishDay(game);
            return;
        }
        game.TimeUnix = next;
        _gameDirty = true;
        RefreshGame(true);
    }

    private void PlayMarket(string symbol, bool buy)
    {
        var game = ActiveGame;
        if (game == null || game.Finished) return;
        game.Add(new GameAction
        {
            Kind = GameActions.Market,
            Symbol = symbol,
            Buy = buy,
            AtUnix = game.TimeUnix,
            StopPips = game.StopPips,
            TakePips = game.TakePips,
        });
        _gameDirty = true;
        RefreshGame();
    }

    private void OpenPlayOrder(IReadOnlyList<PlayLevel> levels, string? nearest)
    {
        var game = ActiveGame;
        if (game == null || game.Finished || levels.Count == 0) return;
        var pairs = levels
            .Select(l => new GameOrderPair(l.Symbol, GamePairColor(l.Symbol), GamePairMirror(l.Symbol),
                GamePriceText(l.Symbol, l.Price)))
            .ToList();
        var dialog = new GameOrderWindow(pairs, nearest, game.StopPips, game.TakePips) { Owner = this };
        if (dialog.ShowDialog() != true || !ReferenceEquals(ActiveGame, game)) return;
        string symbol = dialog.Symbol;
        double raw = dialog.Level * 100000.0 / SymbolPriceDiv(symbol);
        if (!double.IsFinite(raw) || raw < 1 || raw > int.MaxValue)
        {
            AppendLog($"Play: {symbol} level {dialog.Level.ToString(CultureInfo.InvariantCulture)} is out of range");
            return;
        }
        int price = (int)Math.Round(raw);
        game.Add(new GameAction
        {
            Kind = GameActions.Order,
            Symbol = symbol,
            Buy = dialog.Buy,
            AtUnix = game.TimeUnix,
            Price = price,
            StopPips = dialog.StopPips,
            TakePips = dialog.TakePips,
        });
        _gameDirty = true;
        RefreshGame();
        AppendLog($"Play: {symbol} {(dialog.Buy ? "buy" : "sell")} order at {GamePriceText(symbol, price)}");
    }

    private void ClosePlayPosition(string symbol, int id) => AddPlayAction(GameActions.Close, symbol, id);

    private void CancelPlayOrder(string symbol, int id) => AddPlayAction(GameActions.Cancel, symbol, id);

    private void AddPlayAction(string kind, string symbol, int target)
    {
        var game = ActiveGame;
        if (game == null || game.Finished) return;
        game.Add(new GameAction { Kind = kind, Symbol = symbol, AtUnix = game.TimeUnix, Target = target });
        _gameDirty = true;
        RefreshGame();
    }

    private void ToggleFuture()
    {
        var game = ActiveGame;
        if (game == null) return;
        game.FutureOpen = !game.FutureOpen;
        _gameDirty = true;
        RefreshGame();
        AppendLog($"Play: the history after {GameTimeText(game.TimeUnix)} is " +
            (game.FutureOpen ? "shown, the game keeps running" : "hidden again"));
    }

    private void SetPlayPips(double stop, double take)
    {
        var game = ActiveGame;
        if (game == null) return;
        game.StopPips = GameState.ClampPips(stop);
        game.TakePips = GameState.ClampPips(take);
        _gameDirty = true;
    }

    private void RefreshGame(bool follow = false)
    {
        var game = _activeTab.Game;
        if (game is not { Playing: true })
        {
            Chart.SetPlay(0, null);
            Chart.SetGameDrawings(Array.Empty<SymbolSeries>());
            _gameWindow?.Panel.FlushComment();
            _gameWindow?.Hide();
            RefreshGameClock(null);
            EndTimeSession();
            SaveGameSoon();
            return;
        }
        StartTimeSession();
        var books = GameBooks(game);
        Chart.SetPlay(game.TimeUnix, GameMarksOf(books, game.TimeUnix), follow, game.Finished,
            game.IsDayGame(), HasNextDay(game), game.FutureOpen);
        Chart.SetGameDrawings(GameDrawingSeries(game));
        var window = GameWindow();
        window.Panel.Update(PanelDataOf(game, books));
        if (!window.IsVisible)
        {
            window.Place(_config.GamePanelLeft, _config.GamePanelTop);
            window.Show();
        }
        RefreshGameClock(game);
        SaveGameSoon();
    }

    private List<GameBook> GameBooks(GameState game)
    {
        var visible = Chart.VisiblePairs();
        var visibleKeys = new HashSet<string>(visible.Select(IndicatorSymbol.NameKey), StringComparer.Ordinal);
        var keyed = game.Actions.Select(a => (Key: IndicatorSymbol.NameKey(a.Symbol), Action: a)).ToList();
        var books = new List<GameBook>();
        foreach (var symbol in GameSymbols(game, visible))
        {
            string key = IndicatorSymbol.NameKey(symbol);
            var actions = keyed.Where(x => x.Key == key).Select(x => x.Action).ToList();
            int pipPoints = SourcePipPoints(symbol);
            var result = GameSim.Run(actions, game.StartUnix, GameMinutes(symbol), pipPoints,
                AskViewRule.Applies(symbol), game.TimeUnix);
            books.Add(new GameBook(symbol, GamePairColor(symbol), GamePairMirror(symbol),
                visibleKeys.Contains(key), pipPoints, result));
        }
        return books;
    }

    private void SaveGameSoon()
    {
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    private static List<GameMark> GameMarksOf(List<GameBook> books, long timeUnix)
    {
        var marks = new List<GameMark>();
        foreach (var book in books)
        {
            string symbol = book.Symbol;
            var result = book.Result;
            foreach (var t in result.Trades)
                marks.Add(new GameMark(symbol, false, t.Buy, t.OpenUnix, t.CloseUnix, t.OpenPrice,
                    0, 0, false, false, true, t.ClosePrice, t.Pips > 0));
            foreach (var o in result.Orders)
                marks.Add(new GameMark(symbol, true, o.Buy, o.PlacedUnix, timeUnix, o.Price,
                    0, 0, false, false, false, 0, false));
            foreach (var p in result.Positions)
                marks.Add(new GameMark(symbol, false, p.Buy, p.OpenUnix, timeUnix, p.OpenPrice,
                    p.StopPrice, p.TakePrice, p.HasStop, p.HasTake, false, 0, false));
        }
        return marks;
    }

    private GamePanelData PanelDataOf(GameState game, List<GameBook> books)
    {
        var pairs = books.Where(b => b.Visible).Select(PanelPairOf).ToList();
        var positions = books
            .SelectMany(b => b.Result.Positions.Select(p => (Book: b, Position: p)))
            .OrderBy(x => x.Position.OpenUnix)
            .ThenBy(x => x.Position.Id)
            .Select(x => PositionRowOf(x.Book, x.Position))
            .ToList();
        var orders = books
            .SelectMany(b => b.Result.Orders.Select(o => (Book: b, Order: o)))
            .OrderBy(x => x.Order.Id)
            .Select(x => OrderRowOf(x.Book, x.Order))
            .ToList();
        double closed = books.Sum(b => b.Result.ClosedPips);
        double open = books.Sum(b => b.Result.OpenPips);
        int trades = books.Sum(b => b.Result.Trades.Count);
        int wins = books.Sum(b => b.Result.Wins);
        string stats = trades == 0
            ? $"started {GameTimeText(game.StartUnix)}"
            : $"{trades} closed, {wins} win, {trades - wins} loss";
        if (game.Finished)
            stats = $"day finished at {GameTimeText(game.EndUnix)}" + (trades == 0 ? ", no trades" : $": {stats}");
        bool day = game.IsDayGame();
        return new GamePanelData(GameTimeText(game.TimeUnix), pairs, positions, orders,
            PipsText(closed), PipsText(open), PipsText(closed + open), closed + open, stats,
            game.StopPips, game.TakePips)
        {
            Title = day ? GamePanelTitle(game) : "Play",
            Elapsed = ElapsedText(game),
            Day = game.Day,
            Comment = day ? DayStore(game.Mode).Comment(game.Day) : "",
            Comments = day ? DayComments(game.Day) : Array.Empty<GamePanelComment>(),
            NotesOpen = day && game.NotesOpen,
            FutureOpen = game.FutureOpen,
            Replay = day && game.Replay,
            OwnTime = OwnTimeText(),
            Played = PlayedText(game),
            Locked = game.Finished,
        };
    }

    private static GamePanelPair PanelPairOf(GameBook book)
    {
        if (book.Result.Quote is not { } q)
            return new GamePanelPair(book.Symbol, book.ColorArgb, book.Mirror, "", "", "");
        return new GamePanelPair(book.Symbol, book.ColorArgb, book.Mirror,
            GamePriceText(book.Symbol, q.AskHigh),
            GamePriceText(book.Symbol, q.BidLow),
            GameSim.PointsToPips(q.Spread, book.PipPoints).ToString("0.0", CultureInfo.InvariantCulture));
    }

    private static GamePanelRow PositionRowOf(GameBook book, GamePosition p)
    {
        string symbol = book.Symbol;
        int pipPoints = book.PipPoints;
        return new GamePanelRow(symbol, book.ColorArgb, p.Id, p.Buy, false,
            GamePriceText(symbol, p.OpenPrice),
            p.HasStop
                ? GamePanelView.FormatPips(GameSim.PointsToPips(Math.Abs(p.OpenPrice - p.StopPrice), pipPoints))
                : "-",
            p.HasTake
                ? GamePanelView.FormatPips(GameSim.PointsToPips(Math.Abs(p.TakePrice - p.OpenPrice), pipPoints))
                : "-",
            PipsText(p.Pips), p.Pips,
            $"{symbol} opened {GameTimeText(p.OpenUnix)}" +
            (p.HasStop ? $", stop {GamePriceText(symbol, p.StopPrice)}" : "") +
            (p.HasTake ? $", take {GamePriceText(symbol, p.TakePrice)}" : ""));
    }

    private static GamePanelRow OrderRowOf(GameBook book, GameOrder o) =>
        new(book.Symbol, book.ColorArgb, o.Id, o.Buy, true,
            GamePriceText(book.Symbol, o.Price),
            GamePanelView.FormatPips(o.StopPips), GamePanelView.FormatPips(o.TakePips), "", 0,
            $"{book.Symbol} placed {GameTimeText(o.PlacedUnix)}");

    private static string PipsText(double pips) =>
        (pips >= 0 ? "+" : "") + pips.ToString("0.0", CultureInfo.InvariantCulture);

    private static string GameTimeText(long unixSeconds) => DateTimeOffset
        .FromUnixTimeSeconds(unixSeconds).UtcDateTime
        .ToString("dd-MMM-yy HH:mm", CultureInfo.InvariantCulture);

    private static string GamePriceText(string symbol, int rawPoints)
    {
        int div = SymbolPriceDiv(symbol);
        int digits = PipDigits(SourcePipPoints(symbol) * div);
        return (rawPoints * (double)div / 100000.0)
            .ToString("0." + new string('0', digits), CultureInfo.InvariantCulture);
    }
}
