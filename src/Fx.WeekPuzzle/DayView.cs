using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fx.WeekPuzzle;

internal sealed class DayView : FrameworkElement
{
    private const double MinZoom = 0.005;
    private const double MaxZoom = 8;
    private const double WheelStep = 1.2;
    private const double DragThresholdPx = 4;
    private const double GripPx = 7;
    private const int BlinkPhases = 6;
    private const double TitleSize = 26 * DayCards.Scale;
    private const double ImageGap = 10 * DayCards.Scale;
    private const double MaxImagePixels = 40_000_000;
    private static readonly Brush Table = Hex("#3C4043");
    private static readonly Brush BlockFill = Hex("#45494D");
    private static readonly Brush TargetFill = Hex("#55595E");
    private static readonly Typeface TitleFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private sealed class CardVisual
    {
        public required DayCard Card { get; init; }
        public required DrawingVisual Visual { get; init; }
        public required TranslateTransform Position { get; init; }
        public required StreamGeometry Bars { get; init; }
        public required StreamGeometry Line { get; init; }
        public required FormattedText Label { get; init; }
        public required FormattedText MirrorLabel { get; init; }
        public bool ShownMirrored { get; set; }
    }

    private sealed record FrameResize(DayGroup Group, FrameEdges Edges, WorldRect Start);

    private readonly VisualCollection _children;
    private readonly DrawingVisual _blocks = new();
    private readonly ContainerVisual _world = new();
    private readonly MatrixTransform _worldTransform = new();
    private readonly List<CardVisual> _cards = new();
    private readonly Dictionary<DependencyObject, CardVisual> _byVisual = new();
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly HashSet<CardVisual> _highlight = new();
    private readonly ContextMenu _menu = new();
    private DayBoard _board = new(Array.Empty<DayShape>());
    private double _zoom = 1;
    private Point _last;
    private Point _down;
    private CardVisual? _drag;
    private Point _dragFrom;
    private DayGroup? _dragGroup;
    private FrameResize? _resize;
    private DayGroup? _target;
    private bool _dragMoved;
    private bool _pan;
    private bool _fitPending;
    private WorldRect? _fitTarget;
    private int _blinkPhase;

    public DayView()
    {
        _blocks.Transform = _worldTransform;
        _world.Transform = _worldTransform;
        _children = new VisualCollection(this) { _blocks, _world };
        ClipToBounds = true;
        Focusable = true;
        ContextMenu = _menu;
        _blink.Tick += (_, _) => BlinkTick();
    }

    public event Action? Changed;

    public DayBoard Board => _board;

    public double Zoom => _zoom;

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    public void SetBoard(DayBoard board)
    {
        StopBlink();
        _world.Children.Clear();
        _cards.Clear();
        _byVisual.Clear();
        _drag = null;
        _board = board;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var card in board.Cards)
        {
            var position = new TranslateTransform();
            var visual = new DrawingVisual { Transform = position };
            var cv = new CardVisual
            {
                Card = card,
                Visual = visual,
                Position = position,
                Bars = DayArt.BarGeometry(card.Shape),
                Line = DayArt.LineGeometry(card.Shape),
                Label = DayArt.Label(card.Shape, false, dip),
                MirrorLabel = DayArt.Label(card.Shape, true, dip),
            };
            _cards.Add(cv);
            _byVisual[visual] = cv;
            _world.Children.Add(visual);
        }
        SyncPositions();
        RenderAll();
        Fit();
        Changed?.Invoke();
    }

    public void Refresh()
    {
        SyncPositions();
        foreach (var cv in _cards)
            if (cv.ShownMirrored != cv.Card.Mirrored) RenderCard(cv);
        DrawBlocks();
        Changed?.Invoke();
    }

    public void Fit() => FitTo(_board.Bounds);

    public void FitTo(WorldRect b)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            _fitPending = true;
            _fitTarget = b;
            return;
        }
        _fitPending = false;
        _fitTarget = null;
        const double pad = 40;
        double zoom = Math.Clamp(Math.Min((ActualWidth - 2 * pad) / b.Width, (ActualHeight - 2 * pad) / b.Height), MinZoom, MaxZoom);
        SetMatrix(new Matrix(zoom, 0, 0, zoom,
            ActualWidth / 2 - (b.X + b.Width / 2) * zoom,
            ActualHeight / 2 - (b.Y + b.Height / 2) * zoom));
    }

    public static WorldRect RectOf(IEnumerable<DayCard> cards) =>
        DayBoard.BoundsOf(cards.Select(c => new WorldRect(c.X, c.Y, c.Shape.Width, c.Shape.Height)).ToList());

    public void Highlight(IEnumerable<DayCard> cards)
    {
        StopBlink();
        foreach (var card in cards)
        {
            var cv = _cards[card.Index];
            _highlight.Add(cv);
            Raise(cv);
        }
        if (_highlight.Count == 0) return;
        _blinkPhase = 0;
        foreach (var cv in _highlight) RenderCard(cv);
        _blink.Start();
    }

    public byte[] RenderCardsPng(IReadOnlyList<DayCard> cards, int columns, double scale)
    {
        if (cards.Count == 0) throw new ArgumentException("no cards to draw");
        columns = Math.Clamp(columns, 1, cards.Count);
        double cardWidth = cards.Max(c => c.Shape.Width);
        var rows = cards.Chunk(columns).ToList();
        double width = columns * cardWidth + (columns + 1) * ImageGap;
        double height = rows.Sum(r => r.Max(c => c.Shape.Height)) + (rows.Count + 1) * ImageGap;
        scale = Math.Min(scale, Math.Sqrt(MaxImagePixels / (width * height)));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Table, null, new Rect(0, 0, width, height));
            double y = ImageGap;
            foreach (var row in rows)
            {
                double x = ImageGap;
                foreach (var card in row)
                {
                    var cv = _cards[card.Index];
                    dc.PushTransform(new TranslateTransform(x, y));
                    DayArt.Draw(dc, card.Shape, cv.Bars, cv.Line, card.Mirrored ? cv.MirrorLabel : cv.Label, scale,
                        CardState.Normal, card.Mirrored);
                    dc.Pop();
                    x += cardWidth + ImageGap;
                }
                y += row.Max(c => c.Shape.Height) + ImageGap;
            }
        }
        return Png(visual, width * scale, height * scale, scale);
    }

    public byte[] RenderViewPng()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) throw new InvalidOperationException("the Days tab is not on screen");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(this), null, new Rect(0, 0, ActualWidth, ActualHeight));
        return Png(visual, ActualWidth, ActualHeight, 1);
    }

    internal static byte[] Png(Visual visual, double width, double height, double scale)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_fitPending) FitTo(_fitTarget ?? _board.Bounds);
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
        _down = _last;
        _dragMoved = false;
        if (e.ChangedButton == MouseButton.Left && HitCard(_last, null) is { } cv)
        {
            _drag = cv;
            _dragFrom = new Point(cv.Card.X, cv.Card.Y);
            Raise(cv);
            RenderCard(cv);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && EdgeAt(_last) is { } edge)
        {
            _resize = edge;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && FrameAt(_last, null) is { } frame)
        {
            _dragGroup = frame.Group;
            foreach (var card in frame.Group.Cards) Raise(_cards[card.Index]);
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
        if (_drag is not null || _dragGroup is not null || _resize is not null)
        {
            if (!_dragMoved && (p - _down).Length < DragThresholdPx) return;
            _dragMoved = true;
            if (_resize is not null)
            {
                var total = (p - _down) / _zoom;
                _board.SetFrame(_resize.Group, DayBoard.Resize(_resize.Start, _resize.Edges, total.X, total.Y));
            }
            else if (_drag is not null)
            {
                _drag.Card.X += d.X / _zoom;
                _drag.Card.Y += d.Y / _zoom;
                _drag.Position.X = _drag.Card.X;
                _drag.Position.Y = _drag.Card.Y;
                _target = FrameAt(p, _drag.Card)?.Group;
            }
            else
            {
                _board.MoveGroup(_dragGroup!, d.X / _zoom, d.Y / _zoom);
                SyncPositions();
            }
            DrawBlocks();
        }
        else if (_pan)
        {
            var m = _worldTransform.Matrix;
            m.Translate(d.X, d.Y);
            _worldTransform.Matrix = m;
        }
        else
        {
            UpdateCursor(p);
        }
        _last = p;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured) Cursor = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.M || Keyboard.Modifiers != ModifierKeys.None) return;
        if (HitCard(Mouse.GetPosition(this), null) is not { } cv) return;
        cv.Card.Mirrored = !cv.Card.Mirrored;
        Refresh();
        e.Handled = true;
    }

    private void UpdateCursor(Point p)
    {
        if (HitCard(p, null) is not null)
        {
            Cursor = null;
            return;
        }
        if (EdgeAt(p) is { } edge)
        {
            Cursor = edge.Edges switch
            {
                FrameEdges.Left | FrameEdges.Top or FrameEdges.Right | FrameEdges.Bottom => Cursors.SizeNWSE,
                FrameEdges.Right | FrameEdges.Top or FrameEdges.Left | FrameEdges.Bottom => Cursors.SizeNESW,
                FrameEdges.Left or FrameEdges.Right => Cursors.SizeWE,
                _ => Cursors.SizeNS,
            };
            return;
        }
        Cursor = FrameAt(p, null) is not null ? Cursors.SizeAll : null;
    }

    private FrameResize? EdgeAt(Point p)
    {
        var w = ToWorld(p);
        double tol = GripPx / _zoom;
        var frames = _board.Frames();
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            var r = frames[i].Rect;
            if (w.X < r.X - tol || w.X > r.X + r.Width + tol || w.Y < r.Y - tol || w.Y > r.Y + r.Height + tol) continue;
            var edges = FrameEdges.None;
            if (Math.Abs(w.X - r.X) <= tol) edges |= FrameEdges.Left;
            else if (Math.Abs(w.X - (r.X + r.Width)) <= tol) edges |= FrameEdges.Right;
            if (Math.Abs(w.Y - r.Y) <= tol) edges |= FrameEdges.Top;
            else if (Math.Abs(w.Y - (r.Y + r.Height)) <= tol) edges |= FrameEdges.Bottom;
            return edges == FrameEdges.None ? null : new FrameResize(frames[i].Group, edges, r);
        }
        return null;
    }

    private Point ToWorld(Point p) => _worldTransform.Inverse?.Transform(p) ?? p;

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag is not null && e.ChangedButton == MouseButton.Left)
        {
            var dragged = _drag;
            _drag = null;
            _target = null;
            ReleaseMouseCapture();
            RenderCard(dragged);
            if (_dragMoved)
            {
                DropCard(dragged.Card, e.GetPosition(this), new Point(dragged.Card.X, dragged.Card.Y) - _dragFrom);
                Refresh();
            }
            e.Handled = true;
            return;
        }
        if (_resize is not null && e.ChangedButton == MouseButton.Left)
        {
            var group = _resize.Group;
            _resize = null;
            ReleaseMouseCapture();
            if (_dragMoved && _board.FrameOf(group) is { } shown)
            {
                _board.SetFrame(group, shown);
                Refresh();
            }
            e.Handled = true;
            return;
        }
        if (_dragGroup is not null && e.ChangedButton == MouseButton.Left)
        {
            _dragGroup = null;
            ReleaseMouseCapture();
            if (_dragMoved) Refresh();
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

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        var p = Mouse.GetPosition(this);
        _menu.Items.Clear();
        if (HitCard(p, null) is { } cv)
        {
            var card = cv.Card;
            var mirror = Item("Mirror (M)", () => card.Mirrored = !card.Mirrored);
            mirror.IsCheckable = true;
            mirror.IsChecked = card.Mirrored;
            _menu.Items.Add(mirror);
            _menu.Items.Add(new Separator());
            _menu.Items.Add(Item("New group", () => _board.NewGroup(new[] { card })));
            if (card.Group is not null) _menu.Items.Add(Item("Remove from group", () => TakeOut(card)));
        }
        else if (FrameAt(p, null) is { } frame)
        {
            var group = frame.Group;
            _menu.Items.Add(Item("Rename group...", () => Rename(group)));
            _menu.Items.Add(Item("Arrange group", () => _board.ArrangeGroup(group, frame.Rect.X, frame.Rect.Y)));
            if (group.Frame is not null) _menu.Items.Add(Item("Fit frame to cards", () => _board.FitFrame(group)));
            if (group.Cards.Any(c => !c.Mirrored)) _menu.Items.Add(Item("Mirror all cards", () => _board.Mirror(group.Cards, true)));
            if (group.Cards.Any(c => c.Mirrored)) _menu.Items.Add(Item("Unmirror all cards", () => _board.Mirror(group.Cards, false)));
            _menu.Items.Add(new Separator());
            _menu.Items.Add(Item("Delete group", () => _board.Delete(group)));
        }
        if (_menu.Items.Count == 0) e.Handled = true;
        base.OnContextMenuOpening(e);
    }

    private MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            action();
            Refresh();
        };
        return item;
    }

    private void TakeOut(DayCard card)
    {
        if (card.Group is not { } group) return;
        var frame = _board.FrameOf(group) ?? DayBoard.FrameOf(group.Cards);
        _board.Ungroup(new[] { card });
        if (group.Cards.Count == 0) return;
        card.X = frame.X + frame.Width + DayBoard.CardGap;
        Raise(_cards[card.Index]);
    }

    private void Rename(DayGroup group)
    {
        var owner = Window.GetWindow(this);
        string? name = NameDialog.Ask(owner, "Rename group", group.Name);
        if (name is null) return;
        try
        {
            _board.Rename(group, name);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(owner, ex.Message, "Rename group");
        }
    }

    private void DropCard(DayCard card, Point p, Vector moved)
    {
        if (FrameAt(p, card) is { } frame)
        {
            if (card.Group != frame.Group) _board.Group(frame.Group.Name, new[] { card }, false);
        }
        else if (card.Group is { } group)
        {
            if (group.Cards.Count > 1) _board.Ungroup(new[] { card });
            else _board.ShiftFrame(group, moved.X, moved.Y);
        }
    }

    private DayFrame? FrameAt(Point p, DayCard? except)
    {
        var w = ToWorld(p);
        return _board.FrameAt(w.X, w.Y, except);
    }

    private CardVisual? HitCard(Point p, CardVisual? except)
    {
        CardVisual? found = null;
        VisualTreeHelper.HitTest(this,
            v => v == _blocks ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : except is not null && v == except.Visual ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            r => _byVisual.TryGetValue(r.VisualHit, out found) ? HitTestResultBehavior.Stop : HitTestResultBehavior.Continue,
            new PointHitTestParameters(p));
        return found;
    }

    private void BlinkTick()
    {
        if (++_blinkPhase >= BlinkPhases)
        {
            StopBlink();
            return;
        }
        foreach (var cv in _highlight) RenderCard(cv);
    }

    private void StopBlink()
    {
        _blink.Stop();
        var shown = _highlight.ToList();
        _highlight.Clear();
        foreach (var cv in shown)
            if (_cards.Contains(cv)) RenderCard(cv);
    }

    private void Raise(CardVisual cv)
    {
        _world.Children.Remove(cv.Visual);
        _world.Children.Add(cv.Visual);
    }

    private void SetMatrix(Matrix m)
    {
        _worldTransform.Matrix = m;
        if (Math.Abs(m.M11 - _zoom) < 1e-12) return;
        _zoom = m.M11;
        RenderAll();
    }

    private void SyncPositions()
    {
        foreach (var cv in _cards)
        {
            cv.Position.X = cv.Card.X;
            cv.Position.Y = cv.Card.Y;
        }
    }

    private void RenderAll()
    {
        foreach (var cv in _cards) RenderCard(cv);
        DrawBlocks();
    }

    private void RenderCard(CardVisual cv)
    {
        var state = cv == _drag ? CardState.Dragging
            : _highlight.Contains(cv) && _blinkPhase % 2 == 0 ? CardState.Hint
            : CardState.Normal;
        bool mirrored = cv.Card.Mirrored;
        cv.ShownMirrored = mirrored;
        using var dc = cv.Visual.RenderOpen();
        DayArt.Draw(dc, cv.Card.Shape, cv.Bars, cv.Line, mirrored ? cv.MirrorLabel : cv.Label, _zoom, state, mirrored);
    }

    private void DrawBlocks()
    {
        using var dc = _blocks.RenderOpen();
        double px = 1 / _zoom;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var block in _board.Frames(_drag?.Card))
        {
            var brush = Hex(block.Group.Color);
            var r = new Rect(block.Rect.X, block.Rect.Y, block.Rect.Width, block.Rect.Height);
            double width = Math.Max(2 * DayCards.Scale, 2 * px) * (block.Group == _target ? 3 : 1);
            var pen = new Pen(brush, width);
            pen.Freeze();
            double radius = 8 * DayCards.Scale;
            dc.DrawRoundedRectangle(block.Group == _target ? TargetFill : BlockFill, pen, r, radius, radius);
            DrawGrip(dc, brush, r, px);
            var title = new FormattedText(block.Title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TitleFont,
                TitleSize, brush, dip)
            {
                MaxTextWidth = Math.Max(1, r.Width - 2 * DayBoard.BlockPad),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(title, new Point(r.X + DayBoard.BlockPad, r.Y + (DayBoard.BlockHeader - title.Height) / 2));
        }
    }

    private static void DrawGrip(DrawingContext dc, Brush brush, Rect r, double px)
    {
        double size = Math.Max(DayBoard.BlockPad * 0.7, 12 * px);
        if (size > Math.Min(r.Width, r.Height) / 4) return;
        var pen = new Pen(brush, Math.Max(DayCards.Scale, 1.5 * px));
        pen.Freeze();
        double inset = Math.Max(DayBoard.BlockPad * 0.2, 3 * px);
        double x = r.Right - inset, y = r.Bottom - inset;
        for (int i = 1; i <= 3; i++)
        {
            double k = size * i / 3;
            dc.DrawLine(pen, new Point(x - k, y), new Point(x, y - k));
        }
    }

    private static Brush Hex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
