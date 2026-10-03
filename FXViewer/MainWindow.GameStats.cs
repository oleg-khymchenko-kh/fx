using FXViewer.Game;

namespace FXViewer;

public partial class MainWindow
{
    private readonly Dictionary<string, GameStatsWindow> _gameStatsWindows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gameStatsBusy = new(StringComparer.Ordinal);

    private void RefreshGameStats(string mode)
    {
        if (_gameStatsWindows.ContainsKey(mode)) _ = ShowGameStatsAsync(mode, false);
    }

    private void RefreshGameStats()
    {
        foreach (var mode in _gameStatsWindows.Keys.ToList()) _ = ShowGameStatsAsync(mode, false);
    }

    private async Task ShowGameStatsAsync(string mode, bool activate)
    {
        if (!_gameStatsBusy.Add(mode)) return;
        try
        {
            string name = StatsWindowTitle(mode);
            var first = GameDayPicker.FirstDay;
            var last = GameDayPicker.LastDay(GameToday());
            var pairs = await DayGamePairsAsync(name, first, last, "game stats");
            if (pairs == null) return;
            var days = GameDayPicker.Candidates(first, last, pairs.Select(GameMinutes).ToList(), mode);
            var stats = GameStats.Build(GameLogOf(mode), GameTimes, first, last, days);
            if (_gameStatsWindows.TryGetValue(mode, out var open))
            {
                open.Update(stats);
                if (activate) open.Activate();
                return;
            }
            if (!activate) return;
            var window = new GameStatsWindow(name) { Owner = this };
            window.Update(stats);
            window.Closed += (_, _) => _gameStatsWindows.Remove(mode);
            _gameStatsWindows[mode] = window;
            window.Show();
        }
        catch (Exception ex)
        {
            AppendLog("Game stats failed: " + ex.Message);
        }
        finally
        {
            _gameStatsBusy.Remove(mode);
        }
    }

    private static string StatsWindowTitle(string mode) =>
        GameModes.IsAfternoon(mode) ? "Game stats " + GameModes.Title(mode) : "Game stats";
}
