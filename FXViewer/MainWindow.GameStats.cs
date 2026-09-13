using FXViewer.Game;

namespace FXViewer;

public partial class MainWindow
{
    private GameStatsWindow? _gameStatsWindow;
    private bool _gameStatsBusy;

    private void RefreshGameStats()
    {
        if (_gameStatsWindow != null) _ = ShowGameStatsAsync(false);
    }

    private async Task ShowGameStatsAsync(bool activate)
    {
        if (_gameStatsBusy) return;
        _gameStatsBusy = true;
        try
        {
            var first = GameDayPicker.FirstDay;
            var last = GameDayPicker.LastDay(GameToday());
            var pairs = await DayGamePairsAsync("Game stats", first, last, "game stats");
            if (pairs == null) return;
            var days = GameDayPicker.Candidates(first, last, pairs.Select(GameMinutes).ToList());
            var stats = GameStats.Build(GameLog, GameTimes, first, last, days);
            if (_gameStatsWindow != null)
            {
                _gameStatsWindow.Update(stats);
                if (activate) _gameStatsWindow.Activate();
                return;
            }
            if (!activate) return;
            var window = new GameStatsWindow { Owner = this };
            window.Update(stats);
            window.Closed += (_, _) => _gameStatsWindow = null;
            _gameStatsWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            AppendLog("Game stats failed: " + ex.Message);
        }
        finally
        {
            _gameStatsBusy = false;
        }
    }
}
