namespace FXViewer.Game;

public enum GameAwayReason
{
    NoInput,
    Background,
    NotRunning,
}

public sealed class GameTimeCounter
{
    public const double AwayLimit = 60;

    private double _mark;
    private double _front;
    private double _last;

    public double Active { get; private set; }
    public double Stalled { get; private set; }
    public bool Away { get; private set; }
    public bool Held { get; set; }
    public double AwayAt { get; private set; }
    public GameAwayReason Reason { get; private set; }

    public void Start(double now)
    {
        Active = 0;
        Stalled = 0;
        _last = now;
        Resume(now);
    }

    public double Step(double now, double idle, bool inFront)
    {
        bool stalled = Watch(now);
        if (inFront) _front = now;
        if (Held) return 0;
        double input = now - idle;
        double mark = Math.Max(_mark, Math.Min(input, _front));
        if (Away)
        {
            if (now - mark < AwayLimit) Resume(now);
            return 0;
        }
        if (stalled)
        {
            GoAway(_mark, GameAwayReason.NotRunning);
            return 0;
        }
        double delta = mark - _mark;
        if (delta > 0)
        {
            Active += delta;
            _mark = mark;
        }
        if (now - mark >= AwayLimit)
            GoAway(mark, _front <= input ? GameAwayReason.Background : GameAwayReason.NoInput);
        return delta;
    }

    public bool Watch(double now)
    {
        double gap = now - _last;
        _last = now;
        if (gap <= AwayLimit) return false;
        Stalled += gap;
        return true;
    }

    public void Resume(double now)
    {
        Watch(now);
        Away = false;
        Held = false;
        _mark = now;
        _front = now;
    }

    private void GoAway(double at, GameAwayReason reason)
    {
        Away = true;
        AwayAt = at;
        Reason = reason;
    }
}
