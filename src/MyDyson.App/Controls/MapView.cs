using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyDyson.App.Rendering;
using CorePoint = MyDyson.Core.Point;

namespace MyDyson.App.Controls;

/// <summary>What clicks on the map pick while an edit is being aimed; see <see cref="MapView.Picking"/>.</summary>
public enum MapPick
{
    /// <summary>Clicks choose rooms as usual.</summary>
    None,
    /// <summary>Two clicks, the ends of a cut.</summary>
    Line,
    /// <summary>Two clicks, opposite corners of a zone.</summary>
    Rectangle,
    /// <summary>One click, where a piece of furniture goes.</summary>
    Point,
}

/// <summary>
/// Draws a <see cref="MapScene"/>. Mouse wheel or two-finger pinch zoom about the cursor/pinch
/// centre; drag or single-finger swipe pans. A click or tap toggles the room underneath through
/// <see cref="ZoneClicked"/> — always immediately, there being nothing else a click on a room could
/// mean. Empty map space is different: a click/tap there clears the room selection, but a double
/// click/tap resets the zoom instead, so a single click/tap is deferred a short moment to see
/// whether a second one turns it into a double before clearing the selection — otherwise every
/// double click/tap meant to reset the zoom would clear the selection as a surprising side effect.
/// </summary>
public sealed class MapView : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(MapScene), typeof(MapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public MapScene? Scene
    {
        get => (MapScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>Raised with the zone id on every click/tap that lands on a room.</summary>
    public event Action<string>? ZoneClicked;
    /// <summary>Raised when a single click/tap (confirmed not to be the first half of a double) lands on empty map space, to clear the current room selection.</summary>
    public event Action? EmptySpaceClicked;

    // ---- Picking points (cutting a room, drawing a zone, placing furniture) -----

    /// <summary>
    /// While not <see cref="MapPick.None"/>, clicks pick points instead of selecting rooms, and
    /// what is being drawn shows over the map: the cut line, the zone's rectangle, or the outline
    /// of <see cref="PlacementShape"/> under the cursor. Zoom and pan keep working, so it can be
    /// aimed closely.
    /// </summary>
    public MapPick Picking
    {
        get;
        set
        {
            field = value;
            _lineStart = null;
            _linePreview = null;
            Cursor = value == MapPick.None ? null : Cursors.Cross;
            InvalidateVisual();
        }
    }

    /// <summary>For <see cref="MapPick.Point"/>: the outline to show under the cursor, as corners relative to its centre, in metres.</summary>
    public IReadOnlyList<CorePoint>? PlacementShape
    {
        get;
        set { field = value; InvalidateVisual(); }
    }

    /// <summary>Both ends of the line the user drew, in world metres.</summary>
    public event Action<CorePoint, CorePoint>? LinePicked;
    /// <summary>Two opposite corners of the rectangle the user drew, in world metres.</summary>
    public event Action<CorePoint, CorePoint>? RectanglePicked;
    /// <summary>The point the user clicked, in world metres.</summary>
    public event Action<CorePoint>? PointPicked;
    /// <summary>
    /// Every single click or tap outside picking, in world metres, raised before <see cref="ZoneClicked"/>
    /// or <see cref="EmptySpaceClicked"/>: what lies there besides rooms (zones, furniture) is for the
    /// listener to find.
    /// </summary>
    public event Action<CorePoint>? WorldClicked;
    /// <summary>The point under the cursor while picking, or null once it leaves; for a coordinate readout.</summary>
    public event Action<CorePoint?>? LinePointMoved;

    private Point? _lineStart;
    private Point? _linePreview;

    public Matrix WorldToScreen { get; private set; } = Matrix.Identity;
    public double Zoom { get; private set; } = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panAtDragStart;
    private bool _dragged;

    // The user's own double-click speed from Windows settings (500 ms by default). Anyone who needs
    // a slower double click will have set it there, so the deferred clear waits exactly that long.
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime);

    /// <summary>
    /// Whether a click on empty space waits to see if a double click follows before raising
    /// <see cref="EmptySpaceClicked"/>. The dashboard needs it: there, clearing the selection on
    /// the first half of a double click would untick the rooms queued for a clean. Where clearing
    /// costs nothing (the map manager), turning it off makes the click react at once; a double
    /// click still resets the zoom, the first click having simply cleared along the way.
    /// </summary>
    public bool DeferEmptySpaceClick { get; set; } = true;
    private const double DoubleClickMaxDistance = 24;
    private DateTime _lastClickTimeUtc;
    private Point _lastClickPosition;
    private DispatcherTimer? _pendingEmptySpaceClear;

    public MapView()
    {
        Focusable = true;
        ClipToBounds = true;
        IsManipulationEnabled = true;
    }

    public void ResetView()
    {
        Zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        MapRenderer.Render(drawingContext, Scene ?? new MapScene(), size, out var m, Zoom, _pan);
        WorldToScreen = m;
        switch (Picking)
        {
            case MapPick.Line when _lineStart is { } start:
                MapRenderer.DrawPendingCut(drawingContext, m.Transform(start), _linePreview is { } end ? m.Transform(end) : null);
                break;
            case MapPick.Rectangle when _lineStart is { } corner:
                MapRenderer.DrawPendingRectangle(drawingContext, m.Transform(corner), _linePreview is { } other ? m.Transform(other) : null);
                break;
            case MapPick.Point when _linePreview is { } at && PlacementShape is { Count: > 2 } shape:
                MapRenderer.DrawPendingShape(drawingContext, [.. shape.Select(p => m.Transform(new Point(at.X + p.X, at.Y + p.Y)))]);
                break;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAbout(e.GetPosition(this), e.Delta > 0 ? 1.2 : 1 / 1.2);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragStart = e.GetPosition(this);
        _panAtDragStart = _pan;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Picking != MapPick.None)
        {
            var world = ToWorld(e.GetPosition(this));
            LinePointMoved?.Invoke(world is { } w ? new CorePoint(w.X, w.Y) : null);
            // A placement follows the cursor from the start; a line or a rectangle once it has a first point.
            if (_lineStart is not null || Picking == MapPick.Point)
            {
                _linePreview = world;
                InvalidateVisual();
            }
        }
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        if (!_dragged && delta.Length < 4) return;
        _dragged = true;
        _pan = _panAtDragStart + delta;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        var wasClick = _dragStart is not null && !_dragged;
        _dragStart = null;
        if (wasClick)
        {
            var pos = e.GetPosition(this);
            // ClickCount alone isn't trusted here: a Surface trackpad's double-click/double-tap-to-
            // click was observed to never report ClickCount 2 (each click reads as a fresh single),
            // so a click-count-only check would silently never recognise a double on that hardware —
            // hence also checking the same manual timing/distance test used for touch.
            HandleClick(pos, e.ClickCount >= 2 || IsDoubleClick(pos));
        }
        e.Handled = true;
    }

    // ---- Touch: single finger pans, two fingers pinch-zoom, a still touch is a tap. WPF routes all
    // touch through manipulation events rather than promoting it to mouse events once a control opts
    // in via IsManipulationEnabled, so tap/double-tap have no separate routed event to lean on and
    // are detected here from a manipulation whose net movement and scale stayed negligible. ----

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        e.ManipulationContainer = this;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        var scale = e.DeltaManipulation.Scale.X;
        if (Math.Abs(scale - 1) > 0.0005) ZoomAbout(e.ManipulationOrigin, scale);
        if (e.DeltaManipulation.Translation.Length > 0) _pan += e.DeltaManipulation.Translation;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        var total = e.TotalManipulation;
        if (total.Translation.Length < 6 && Math.Abs(total.Scale.X - 1) < 0.03)
        {
            var pos = e.ManipulationOrigin;
            HandleClick(pos, IsDoubleClick(pos));
        }
        e.Handled = true;
    }

    /// <summary>True when this click/tap lands within the double-click time and distance of the previous one. Always records this one as "the last click" regardless, so a used double can't chain into a triple being read as another double.</summary>
    private bool IsDoubleClick(Point pos)
    {
        var now = DateTime.UtcNow;
        var isDouble = now - _lastClickTimeUtc < DoubleClickWindow && (pos - _lastClickPosition).Length < DoubleClickMaxDistance;
        _lastClickTimeUtc = isDouble ? DateTime.MinValue : now;
        _lastClickPosition = pos;
        return isDouble;
    }

    /// <summary>A room always reacts right away; only empty space needs to wait and see whether a second click/tap turns this into a double, since that's the only place the two mean different things.</summary>
    private void HandleClick(Point pos, bool isDouble)
    {
        if (Picking != MapPick.None)
        {
            if (ToWorld(pos) is not { } picked) return;
            if (Picking == MapPick.Point)
            {
                PointPicked?.Invoke(new CorePoint(picked.X, picked.Y));
                return;
            }
            if (_lineStart is not { } start)
            {
                _lineStart = picked;
                InvalidateVisual();
                return;
            }
            // Two clicks in the same spot would be a zero-length cut or an empty zone, which the
            // robot refuses; treat it as the user changing their mind about where to start.
            if ((WorldToScreen.Transform(picked) - WorldToScreen.Transform(start)).Length < 8)
            {
                _lineStart = picked;
                InvalidateVisual();
                return;
            }
            _lineStart = null;
            _linePreview = null;
            InvalidateVisual();
            var (a, b) = (new CorePoint(start.X, start.Y), new CorePoint(picked.X, picked.Y));
            if (Picking == MapPick.Rectangle) RectanglePicked?.Invoke(a, b);
            else LinePicked?.Invoke(a, b);
            return;
        }

        var world = ToWorld(pos);
        if (world is { } clicked && !isDouble) WorldClicked?.Invoke(new CorePoint(clicked.X, clicked.Y));
        var zone = world is { } w ? Scene?.ZoneAt(w.X, w.Y) : null;
        if (zone is not null)
        {
            _pendingEmptySpaceClear?.Stop();
            _pendingEmptySpaceClear = null;
            ZoneClicked?.Invoke(zone);
        }
        else if (isDouble)
        {
            _pendingEmptySpaceClear?.Stop();
            _pendingEmptySpaceClear = null;
            ResetView();
        }
        else if (!DeferEmptySpaceClick)
        {
            EmptySpaceClicked?.Invoke();
        }
        else
        {
            _pendingEmptySpaceClear?.Stop();
            var timer = new DispatcherTimer { Interval = DoubleClickWindow };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _pendingEmptySpaceClear = null;
                EmptySpaceClicked?.Invoke();
            };
            _pendingEmptySpaceClear = timer;
            timer.Start();
        }
    }

    /// <summary>Scales by <paramref name="factor"/> while keeping the world point under <paramref name="cursor"/> fixed.</summary>
    private void ZoomAbout(Point cursor, double factor)
    {
        var newZoom = Math.Clamp(Zoom * factor, 0.5, 12);
        factor = newZoom / Zoom;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var offset = cursor - centre;
        _pan = (_pan - offset) * factor + offset;
        Zoom = newZoom;
        InvalidateVisual();
    }

    /// <summary>Screen point to world metres, using the last render's transform.</summary>
    public Point? ToWorld(Point screen)
    {
        var m = WorldToScreen;
        if (!m.HasInverse) return null;
        m.Invert();
        return m.Transform(screen);
    }
}
