using System.Windows;
using FXViewer.Game;

namespace FXViewer;

public partial class MainWindow
{
    private readonly GameTimeCounter _timeCounter = new();
    private readonly HashSet<string> _timeDays = new(StringComparer.Ordinal);

    private List<GameTimeSession>? _gameTimes;
    private GameTimeSession? _timeSession;
    private bool _timeAwayFailed;

    private List<GameTimeSession> GameTimes => _gameTimes ??= GameTimeStore.Load();

    private void StartTimeSession()
    {
        if (_timeSession != null) return;
        _timeSession = new GameTimeSession { StartedAt = GameClockNow() };
        _timeDays.Clear();
        _timeCounter.Start(UserPresence.Seconds());
        _timeAwayFailed = false;
    }

    private void EndTimeSession()
    {
        var session = _timeSession;
        if (session == null) return;
        _timeSession = null;
        _timeCounter.Watch(UserPresence.Seconds());
        session.EndedAt = GameClockNow();
        double open = (session.EndedAt - session.StartedAt).TotalSeconds - _timeCounter.Stalled;
        session.Seconds = (long)Math.Max(0, Math.Round(open));
        session.ActiveSeconds = (long)Math.Round(_timeCounter.Active);
        session.Days = _timeDays.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (session.ActiveSeconds <= 0 && session.Games == 0) return;
        try
        {
            var sessions = GameTimes;
            GameTimeStore.Append(session);
            sessions.Add(session);
        }
        catch (Exception ex)
        {
            AppendLog("Game time write failed: " + ex.Message);
        }
        AppendLog($"Play: your own time {ClockText(session.ActiveSeconds)}, " +
            $"panel open {ClockText(session.Seconds)}" +
            (session.Pauses > 0 ? $", {session.Pauses} away pause(s)" : "") +
            (session.Days.Count > 0 ? ", days " + string.Join(", ", session.Days) : ""));
        RefreshGameStats();
    }

    private void TickGameTime()
    {
        if (_timeSession == null) return;
        double now = UserPresence.Seconds();
        var wallNow = DateTimeOffset.Now;
        bool wasAway = _timeCounter.Away;
        double delta = _timeCounter.Step(now, UserPresence.IdleSeconds(), UserPresence.AppInFront());
        if (_timeCounter.Held) return;
        if (delta > 0) AddTimeSpan(_timeSession, now, wallNow, delta);
        if (delta > 0 && _activeTab.Game is { Playing: true } game)
        {
            game.ActiveSeconds += delta;
            if (game.IsDayGame()) _timeDays.Add(game.Day);
            _gameDirty = true;
        }
        if (wasAway == _timeCounter.Away) return;
        if (wasAway)
        {
            LogBack(now);
            return;
        }
        _timeSession.Pauses++;
        var since = GameClockNow().AddSeconds(_timeCounter.AwayAt - now);
        AppendLog($"Play: your time stopped at {since:HH:mm:ss} because {AwayText(_timeCounter.Reason)}");
        AskStillHere(_timeCounter.Reason, since);
    }

    private void AddTimeSpan(GameTimeSession session, double now, DateTimeOffset wallNow, double delta)
    {
        var to = wallNow.AddSeconds(_timeCounter.Mark - now);
        string mode = _activeTab.Game is { Playing: true } game && game.IsDayGame() ? game.Mode : "";
        GameTimeSpans.Add(session.Spans, mode, to.AddSeconds(-delta), to);
    }

    private void AskStillHere(GameAwayReason reason, DateTimeOffset since)
    {
        _timeCounter.Held = true;
        bool shown = false;
        try
        {
            var dialog = new GameAwayWindow(AwayText(reason), since, ClockText(_timeCounter.Active))
            {
                Owner = AwayOwner(),
            };
            dialog.ShowDialog();
            shown = true;
        }
        catch (Exception ex)
        {
            if (!_timeAwayFailed) AppendLog("Play: the away popup could not open: " + ex.Message);
            _timeAwayFailed = true;
        }
        if (!shown)
        {
            _timeCounter.Held = false;
            return;
        }
        double now = UserPresence.Seconds();
        LogBack(now);
        _timeCounter.Resume(now);
    }

    private void LogBack(double now) =>
        AppendLog($"Play: your time goes on, away {ClockText(now - _timeCounter.AwayAt)}");

    private static string AwayText(GameAwayReason reason) => reason switch
    {
        GameAwayReason.Background => "FXViewer was in the background for a minute",
        GameAwayReason.NotRunning => "the computer was asleep, or FXViewer did not run, for more than a minute",
        _ => "there was no mouse move or key press for a minute",
    };

    private Window AwayOwner()
    {
        foreach (Window window in Application.Current.Windows)
            if (window.IsActive && window.IsVisible) return window;
        return this;
    }

    private string PlayedText(GameState game)
    {
        if (!game.IsDayGame()) return "";
        var logs = new Dictionary<string, List<GameLogEntry>>(StringComparer.Ordinal)
        {
            [GameModes.Day] = GameLogOf(GameModes.Day),
            [GameModes.Afternoon] = GameLogOf(GameModes.Afternoon),
        };
        var running = _timeSession?.Spans ?? new List<GameTimeSpan>();
        return GamePlayedCounter
            .Count(game.Mode, logs, GameTimes, running, DateTimeOffset.Now, new DateTimeOffset(DateTime.Today))
            .Text(game.Mode, ClockText);
    }

    private string OwnTimeText() => _timeSession == null
        ? ""
        : "own " + ClockText(_timeCounter.Active) + (_timeCounter.Away ? " paused" : "");

    private static string ClockText(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
