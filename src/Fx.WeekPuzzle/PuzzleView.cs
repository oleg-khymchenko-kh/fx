using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fx.WeekPuzzle;

internal sealed class PuzzleView : FrameworkElement
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 4;
    private const double WheelStep = 1.2;
    private const double SnapTolerancePx = 30;
    private const double ScatterGap = 300;
    private const double SortGap = 150;
    private const int SortRowCards = 8;
    private const double BigWeekPips = 60;
    private const int BlinkPhases = 6;
    private static readonly Brush Table = Frozen("#3C4043");

    private sealed class CardVisual
    {
        public required PuzzleCard Card { get; init; }
        public required CardShape Shape { get; init; }
        public required DrawingVisual Visual { get; init; }
        public required TranslateTransform Position { get; init; }
        public required StreamGeometry Bars { get; init; }
        public required StreamGeometry Line { get; init; }
    }

    private readonly VisualCollection _children;
    private readonly ContainerVisual _world = new();
    private readonly DrawingVisual _marks = new();
    private readonly MatrixTransform _worldTransform = new();
    private readonly List<CardVisual> _cards = new();
    private readonly Dictionary<DependencyObject, CardVisual> _byVisual = new();
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private PuzzleBoard _board = new(Array.Empty<PuzzleCard>());
    private double _zoom = 1;
    private Point _last;
    private CardVisual? _drag;
    private CardVisual? _menuCard;
    private CardVisual? _hint;
    private int _blinkPhase;
    private bool _pan;
    private bool _fitPending;

    public PuzzleView()
    {
        _world.Transform = _worldTransform;
        _marks.Transform = _worldTransform;
        _children = new VisualCollection(this) { _world, _marks };
        ClipToBounds = true;
        Focusable = true;
        _blink.Tick += (_, _) => BlinkTick();
        var hint = new MenuItem { Header = "Hint" };
        hint.Click += (_, _) => ShowHint();
        ContextMenu = new ContextMenu { Items = { hint } };
    }

    public event Action? Changed;

    public PuzzleBoard Board => _board;

    public double Zoom => _zoom;

    public bool MarksShown { get; private set; }

    public int Hints { get; private set; }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    public void SetCards(IReadOnlyList<CardShape> shapes, Random random)
    {
        StopBlink();
        Hints = 0;
        _world.Children.Clear();
        _cards.Clear();
        _byVisual.Clear();
        _drag = null;
        _menuCard = null;
        _board = new PuzzleBoard(shapes.Select((s, i) => new PuzzleCard(i, s.Width, s.Height, s.LeftTick, s.RightTick, s.Candle)));
        _board.Scatter(random, ScatterGap);
        for (int i = 0; i < shapes.Count; i++)
        {
            var position = new TranslateTransform();
            var visual = new DrawingVisual { Transform = position };
            var cv = new CardVisual
            {
                Card = _board.Cards[i],
                Shape = shapes[i],
                Visual = visual,
                Position = position,
                Bars = CardArt.BarGeometry(shapes[i]),
                Line = CardArt.LineGeometry(shapes[i]),
            };
            _cards.Add(cv);
            _byVisual[visual] = cv;
            _world.Children.Add(visual);
        }
        HideMarks();
        SyncPositions(_board.Cards);
        RenderCards();
        Fit();
        Changed?.Invoke();
    }

    public void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            _fitPending = true;
            return;
        }
        _fitPending = false;
        var b = _board.Bounds;
        const double pad = 40;
        double zoom = Math.Clamp(Math.Min((ActualWidth - 2 * pad) / b.Width, (ActualHeight - 2 * pad) / b.Height), MinZoom, MaxZoom);
        SetMatrix(new Matrix(zoom, 0, 0, zoom,
            ActualWidth / 2 - (b.X + b.Width / 2) * zoom,
            ActualHeight / 2 - (b.Y + b.Height / 2) * zoom));
    }

    public void ShowMarks()
    {
        MarksShown = true;
        DrawMarks();
    }

    public void HideMarks()
    {
        MarksShown = false;
        using var dc = _marks.RenderOpen();
    }

    public void Sort()
    {
        _board.Sort(BigWeekPips * WeekCards.PipPoints, SortGap, SortRowCards);
        SyncPositions(_board.Cards);
        Fit();
        Changed?.Invoke();
    }

    public void HintFor(PuzzleCard card)
    {
        var target = _board.HintFor(card);
        if (target is null) return;
        Hints++;
        StopBlink();
        _hint = _cards[target.Index];
        _blinkPhase = 0;
        Raise(target);
        RenderCard(_hint);
        _blink.Start();
        Changed?.Invoke();
    }

    private void ShowHint()
    {
        if (_menuCard is not null) HintFor(_menuCard.Card);
    }

    private void BlinkTick()
    {
        if (_hint is null || ++_blinkPhase >= BlinkPhases)
        {
            StopBlink();
            return;
        }
        RenderCard(_hint);
    }

    private void StopBlink()
    {
        _blink.Stop();
        var shown = _hint;
        _hint = null;
        if (shown is not null && _cards.Contains(shown)) RenderCard(shown);
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        _menuCard = HitCard(Mouse.GetPosition(this));
        if (_menuCard is null) e.Handled = true;
        base.OnContextMenuOpening(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fitPending) Fit();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Table, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        double target = Math.Clamp(_zoom * (e.Delta > 0 ? WheelStep : 1 / WheelStep), MinZoom, MaxZoom);
        double factor = target / _zoom;
        var m = _worldTransform.Matrix;
        m.ScaleAt(factor, factor, p.X, p.Y);
        SetMatrix(m);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        _last = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Left && HitCard(_last) is { } cv)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _board.Detach(cv.Card);
            HideMarks();
            _drag = cv;
            foreach (var c in cv.Card.Chain()) Raise(c);
            RenderCard(cv);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton is MouseButton.Left or MouseButton.Middle)
        {
            _pan = true;
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var d = p - _last;
        _last = p;
        if (_drag is not null)
        {
            _board.MoveChain(_drag.Card, d.X / _zoom, d.Y / _zoom);
            SyncPositions(_drag.Card.Chain());
        }
        else if (_pan)
        {
            var m = _worldTransform.Matrix;
            m.Translate(d.X, d.Y);
            _worldTransform.Matrix = m;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag is not null && e.ChangedButton == MouseButton.Left)
        {
            var dragged = _drag;
            _drag = null;
            _board.TrySnap(dragged.Card, SnapTolerancePx / _zoom);
            SyncPositions(dragged.Card.Chain());
            RenderCard(dragged);
            ReleaseMouseCapture();
            Changed?.Invoke();
            e.Handled = true;
            return;
        }
        if (_pan)
        {
            _pan = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private CardVisual? HitCard(Point p)
    {
        CardVisual? found = null;
        VisualTreeHelper.HitTest(this,
            v => v == _marks ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            r => _byVisual.TryGetValue(r.VisualHit, out found) ? HitTestResultBehavior.Stop : HitTestResultBehavior.Continue,
            new PointHitTestParameters(p));
        return found;
    }

    private void Raise(PuzzleCard card)
    {
        var visual = _cards[card.Index].Visual;
        _world.Children.Remove(visual);
        _world.Children.Add(visual);
    }

    private void SetMatrix(Matrix m)
    {
        _worldTransform.Matrix = m;
        if (Math.Abs(m.M11 - _zoom) < 1e-12) return;
        _zoom = m.M11;
        RenderCards();
        if (MarksShown) DrawMarks();
    }

    private void SyncPositions(IEnumerable<PuzzleCard> cards)
    {
        foreach (var card in cards)
        {
            var cv = _cards[card.Index];
            cv.Position.X = card.X;
            cv.Position.Y = card.Y;
        }
    }

    private void RenderCards()
    {
        foreach (var cv in _cards) RenderCard(cv);
    }

    private void RenderCard(CardVisual cv)
    {
        var state = cv == _drag ? CardState.Dragging
            : cv == _hint && _blinkPhase % 2 == 0 ? CardState.Hint
            : CardState.Normal;
        using var dc = cv.Visual.RenderOpen();
        CardArt.Draw(dc, cv.Shape, cv.Bars, cv.Line, _zoom, state);
    }

    private void DrawMarks()
    {
        using var dc = _marks.RenderOpen();
        double width = 8 / _zoom;
        foreach (var link in _board.Links)
        {
            double x = link.Left.X + link.Left.Width;
            double top = Math.Max(link.Left.Y, link.Right.Y);
            double bottom = Math.Min(link.Left.Y + link.Left.Height, link.Right.Y + link.Right.Height);
            dc.DrawRectangle(link.Correct ? CardArt.Good : CardArt.Bad, null, new Rect(x - width / 2, top, width, bottom - top));
        }
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
