using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dyss.App.Rendering;
using CorePoint = Dyss.Core.Point;

namespace Dyss.App.Controls;

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
/// A drag that starts on <see cref="EditableShape"/> moves or resizes that shape instead of the map.
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

    // ---- Moving and resizing a shape (a zone or a piece of furniture) ----------

    /// <summary>
    /// A shape the user may drag to move it, in world metres — the map manager's chosen zone or
    /// piece of furniture — or null. Pressing on it and dragging moves it instead of panning; with
    /// <see cref="EditableShapeResizable"/>, its four corners get handles that resize it.
    /// </summary>
    public IReadOnlyList<CorePoint>? EditableShape
    {
        get;
        set
        {
            field = value;
            _shapeGrip = null;
            _shapePreview = null;
            InvalidateVisual();
        }
    }

    /// <summary>Whether <see cref="EditableShape"/> shows corner handles: an upright rectangle resized from the opposite corner.</summary>
    public bool EditableShapeResizable
    {
        get;
        set { field = value; InvalidateVisual(); }
    }

    /// <summary>Raised once per drag of <see cref="EditableShape"/>, when it is dropped, with its new corners.</summary>
    public event Action<IReadOnlyList<CorePoint>>? ShapeEdited;

    /// <summary>
    /// The shortest side a rectangle may have, in metres, while it is drawn or resized: the corner
    /// being pulled stops that far from the opposite one instead of letting the shape shrink below
    /// what will be accepted. Zero lets it shrink freely.
    /// </summary>
    public double MinimumShapeSide { get; set; }

    private Point KeepMinimum(Point anchor, Point corner, Point? side = null)
    {
        var kept = Dyss.Core.MapShapes.KeepMinimum(new CorePoint(anchor.X, anchor.Y), new CorePoint(corner.X, corner.Y),
            MinimumShapeSide, side is { } s ? new CorePoint(s.X, s.Y) : null);
        return new Point(kept.X, kept.Y);
    }

    /// <summary>What the drag holds: -1 the shape itself, 0 to 3 one of its corners, null nothing.</summary>
    private int? _shapeGrip;
    private Point _shapeGripWorld;
    private Point _touchGripScreen;
    private IReadOnlyList<CorePoint>? _shapePreview;
    private const double HandleReach = 10;

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
        AddVisualChild(_markers);
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        Loaded += (_, _) => UpdateAnimation();
    }

    // ---- The robot and its dock, on a layer of their own --------------------------------
    // They are animated by what they are doing; redrawing only this layer each frame leaves the
    // map itself, much heavier to draw, untouched.

    private readonly DrawingVisual _markers = new();
    private readonly RobotGlide _glide = new();
    private bool _animating;
    private TimeSpan _lastFrame;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _markers : throw new ArgumentOutOfRangeException(nameof(index));

    private void DrawMarkers()
    {
        using var dc = _markers.RenderOpen();
        if (Scene is not { } scene) return;
        // Windows' "show animations" setting off: everything stands still.
        var animate = SystemParameters.ClientAreaAnimation;
        var seconds = animate ? Clock.Elapsed.TotalSeconds : 0;
        var robotAt = animate && scene.SmoothRobotMotion && !scene.RobotDocked && scene.Robot is { } robot
            ? _glide.At(robot, Clock.Elapsed.TotalSeconds)
            : null;
        RobotMarkers.Draw(dc, scene, WorldToScreen, seconds, robotAt);
        // A new position starts a glide, and one that has arrived stops the frames.
        if (robotAt is not null) Dispatcher.BeginInvoke(UpdateAnimation, DispatcherPriority.Render);
    }

    /// <summary>Keeps redrawing the markers while something moves and the map can be seen, and only then.</summary>
    private void UpdateAnimation()
    {
        var wanted = IsVisible && IsLoaded && Scene is { } scene && SystemParameters.ClientAreaAnimation
            && (RobotMarkers.IsAnimated(scene) || (scene.SmoothRobotMotion && _glide.IsGliding(Clock.Elapsed.TotalSeconds)));
        if (wanted == _animating) return;
        if (wanted) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
        _animating = wanted;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Rendering fires at the screen's rate; thirty frames a second is plenty for these.
        var now = e is RenderingEventArgs r ? r.RenderingTime : Clock.Elapsed;
        if (now - _lastFrame < FrameInterval) return;
        _lastFrame = now;
        DrawMarkers();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SceneProperty) UpdateAnimation();
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
        DrawMarkers();
        // The grid under what is being aimed or dragged, and where the pointer lands on it.
        if (SnapSpec is { } grid && (Picking != MapPick.None || EditableShape is not null))
        {
            MapRenderer.DrawGridLines(drawingContext, m, grid, size);
            if (_pickCursor is { } cursor) MapRenderer.DrawSnapMarker(drawingContext, m.Transform(cursor));
        }
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
            case MapPick.None when EditableShape is { Count: 4 } editable:
                var shown = _shapePreview ?? editable;
                if (_shapePreview is not null)
                    MapRenderer.DrawPendingShape(drawingContext, [.. shown.Select(p => m.Transform(new Point(p.X, p.Y)))]);
                if (EditableShapeResizable)
                    MapRenderer.DrawHandles(drawingContext, [.. shown.Select(p => m.Transform(new Point(p.X, p.Y)))]);
                break;
        }
    }

    /// <summary>What lies under a screen point of the editable shape: a corner handle (0 to 3), the shape itself (-1), or nothing.</summary>
    private int? ShapeGripAt(Point screen)
    {
        if (Picking != MapPick.None || EditableShape is not { Count: 4 } shape) return null;
        if (EditableShapeResizable)
            for (var i = 0; i < 4; i++)
                if ((WorldToScreen.Transform(new Point(shape[i].X, shape[i].Y)) - screen).Length <= HandleReach) return i;
        return ToWorld(screen) is { } w && Dyss.Core.MapShapes.Contains(shape, w.X, w.Y) ? -1 : null;
    }

    private void GripShape(int grip, Point screen)
    {
        if (ToWorld(screen) is not { } w) return;
        _shapeGrip = grip;
        _shapeGripWorld = w;
        _shapePreview = null;
    }

    /// <summary>Where the shape would land with the pointer at <paramref name="screen"/>: moved along, or stretched from the corner opposite the handle.</summary>
    private void DragShape(Point screen)
    {
        if (_shapeGrip is not { } grip || EditableShape is not { Count: 4 } shape || ToWorld(screen) is not { } w) return;
        if (grip < 0)
        {
            // Its first corner lands on the grid, so a shape that sat off it is brought back onto it.
            var moved = new Point(shape[0].X + w.X - _shapeGripWorld.X, shape[0].Y + w.Y - _shapeGripWorld.Y);
            var snapped = Snap(moved);
            _shapePreview = Dyss.Core.MapShapes.Translate(shape, snapped.X - shape[0].X, snapped.Y - shape[0].Y);
        }
        else
        {
            var opposite = shape[(grip + 2) % 4];
            var anchor = new Point(opposite.X, opposite.Y);
            var corner = KeepMinimum(anchor, Snap(w), new Point(shape[grip].X, shape[grip].Y));
            _shapePreview = Dyss.Core.MapShapes.Rectangle(opposite, new CorePoint(corner.X, corner.Y));
        }
        _pickCursor = null;
        InvalidateVisual();
    }

    // ---- The robot's grid -------------------------------------------------------

    /// <summary>
    /// Whether picked points and dragged shapes land on the map's grid — the robot's 5 cm cells —
    /// and the grid shows while aiming or while a shape is chosen, once zoomed in far enough to tell
    /// the cells apart. Off on the dashboard, where nothing is drawn.
    /// </summary>
    public bool SnapToGrid
    {
        get;
        set { field = value; InvalidateVisual(); }
    }

    /// <summary>Where the snapped pointer is while aiming, for its marker.</summary>
    private Point? _pickCursor;

    private (double X0, double Y0, double Step)? SnapSpec => SnapToGrid ? Scene?.GridSpec : null;

    private Point Snap(Point world) => SnapSpec is { } g
        ? new Point(g.X0 + Math.Round((world.X - g.X0) / g.Step) * g.Step, g.Y0 + Math.Round((world.Y - g.Y0) / g.Step) * g.Step)
        : world;

    private Point? SnappedWorld(Point screen) => ToWorld(screen) is { } w ? Snap(w) : null;

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_pickCursor is null) return;
        _pickCursor = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Escape: drops a shape being dragged back where it was. False when no shape was being dragged,
    /// so the key can mean something else (leaving a drawing, erasing a zone).
    /// </summary>
    public bool CancelGesture()
    {
        if (_shapeGrip is not null)
        {
            ReleaseShape(drop: false);
            _dragStart = null;
            ReleaseMouseCapture();
            return true;
        }
        return false;
    }

    private void ReleaseShape(bool drop)
    {
        var dropped = _shapePreview;
        _shapeGrip = null;
        _shapePreview = null;
        InvalidateVisual();
        if (drop && dropped is not null) ShapeEdited?.Invoke(dropped);
    }

    /// <summary>The cursor says what pressing would do: move the shape, pull a corner, or nothing special.</summary>
    private void UpdateHoverCursor(Point screen)
    {
        if (Picking != MapPick.None) return;
        Cursor = ShapeGripAt(screen) switch
        {
            -1 => Cursors.SizeAll,
            { } corner => CornerCursor(corner),
            _ => null,
        };
    }

    /// <summary>
    /// The diagonal arrow along the corner's bisector, as the corner sits on screen: the map is
    /// drawn with y up and possibly turned, so which corner is where cannot be told from its index.
    /// </summary>
    private Cursor CornerCursor(int corner)
    {
        if (EditableShape is not { Count: 4 } shape) return Cursors.SizeAll;
        var at = WorldToScreen.Transform(new Point(shape[corner].X, shape[corner].Y));
        var opposite = WorldToScreen.Transform(new Point(shape[(corner + 2) % 4].X, shape[(corner + 2) % 4].Y));
        return PullsAlongMainDiagonal(at, opposite) ? Cursors.SizeNWSE : Cursors.SizeNESW;
    }

    /// <summary>Whether a corner at <paramref name="at"/> on screen pulls along ↖↘ rather than ↗↙: screen y points down, so it does when it sits down-right or up-left of its opposite.</summary>
    public static bool PullsAlongMainDiagonal(Point at, Point opposite) => (at.X - opposite.X) * (at.Y - opposite.Y) > 0;

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
        var pos = e.GetPosition(this);
        _dragStart = pos;
        _panAtDragStart = _pan;
        _dragged = false;
        // Pressing on the editable shape holds it; anywhere else the drag pans the map. Whatever an
        // earlier gesture may have left held is let go first, never carried into this one.
        _shapeGrip = null;
        _shapePreview = null;
        if (ShapeGripAt(pos) is { } grip) GripShape(grip, pos);
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// The capture can go mid-gesture — Alt+Tab, a window taking the focus — and then no button-up
    /// ever comes. A shape being dragged is dropped where it was, unsent: otherwise the next drag,
    /// meant to pan the map, would carry it along and send it to the robot on release.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_shapeGrip is not null) ReleaseShape(drop: false);
        _dragStart = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_shapeGrip is not null && _dragStart is { } held && e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(this);
            // Same 4 px dead zone as panning, so a click on the shape stays a click.
            if (!_dragged && (pos - held).Length < 4) return;
            _dragged = true;
            DragShape(pos);
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) UpdateHoverCursor(e.GetPosition(this));
        if (Picking != MapPick.None)
        {
            var world = SnappedWorld(e.GetPosition(this));
            _pickCursor = world;
            if (_lineStart is null && Picking != MapPick.Point) InvalidateVisual();   // the snap marker follows
            LinePointMoved?.Invoke(world is { } w ? new CorePoint(w.X, w.Y) : null);
            // A placement follows the cursor from the start; a line or a rectangle once it has a first point.
            if (_lineStart is not null || Picking == MapPick.Point)
            {
                _linePreview = Picking == MapPick.Rectangle && _lineStart is { } first && world is { } other ? KeepMinimum(first, other) : world;
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
        var wasClick = _dragStart is not null && !_dragged;
        _dragStart = null;
        if (_shapeGrip is not null) ReleaseShape(drop: !wasClick);
        // Released only now: letting go of the capture raises LostMouseCapture at once, which would
        // otherwise drop the shape unsent before this handler had read the gesture.
        ReleaseMouseCapture();
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

    /// <summary>A touch that starts on the editable shape drags it rather than the map.</summary>
    protected override void OnManipulationStarted(ManipulationStartedEventArgs e)
    {
        base.OnManipulationStarted(e);
        if (ShapeGripAt(e.ManipulationOrigin) is { } grip)
        {
            GripShape(grip, e.ManipulationOrigin);
            _touchGripScreen = e.ManipulationOrigin;
        }
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        if (_shapeGrip is not null)
        {
            DragShape(_touchGripScreen + e.CumulativeManipulation.Translation);
            e.Handled = true;
            return;
        }
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
        var isTap = total.Translation.Length < 6 && Math.Abs(total.Scale.X - 1) < 0.03;
        if (_shapeGrip is not null) ReleaseShape(drop: !isTap);
        if (isTap)
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
            if (SnappedWorld(pos) is not { } picked) return;
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
            if (Picking == MapPick.Rectangle) picked = KeepMinimum(start, picked);
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
