using Dyss.Core;
using CorePoint = Dyss.Core.Point;

namespace Dyss.Presentation.Map;

/// <summary>What clicks on the map pick while an edit is being aimed; see <see cref="MapInteraction.Picking"/>.</summary>
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

/// <summary>The pointer the map asks for: what pressing would do.</summary>
public enum MapCursor
{
    Default,
    /// <summary>Clicks pick points.</summary>
    Cross,
    /// <summary>Over a shape that can be dragged.</summary>
    Move,
    /// <summary>Over a corner that pulls along ↖↘.</summary>
    ResizeNorthwestSoutheast,
    /// <summary>Over a corner that pulls along ↗↙.</summary>
    ResizeNortheastSouthwest,
}

/// <summary>What is drawn over the map while something is aimed or dragged, in screen pixels; null or empty for what is not.</summary>
public sealed record MapOverlay
{
    /// <summary>The robot's cell grid, origin and step in metres: drawn as <see cref="MapGeometry.GridLines"/>.</summary>
    public (double X0, double Y0, double Step)? Grid { get; init; }
    /// <summary>A small cross where the pointer lands on the grid.</summary>
    public Vec2? SnapMarker { get; init; }
    /// <summary>The cut being aimed: its first end as a ring, and the line to the cursor once there is one.</summary>
    public Vec2? CutFrom { get; init; }
    public Vec2? CutTo { get; init; }
    /// <summary>The zone being drawn: its first corner, then the upright rectangle to the cursor.</summary>
    public Vec2? RectangleFrom { get; init; }
    public Vec2? RectangleTo { get; init; }
    /// <summary>An outline: where a piece of furniture would land, or a shape being dragged.</summary>
    public IReadOnlyList<Vec2>? PendingShape { get; init; }
    /// <summary>Square grips on the corners of a shape that can be resized.</summary>
    public IReadOnlyList<Vec2>? Handles { get; init; }

    public static readonly MapOverlay Empty = new();
}

/// <summary>
/// Everything a map view does short of drawing: zoom and pan, and what clicks, drags and touches
/// mean. The view feeds it its size and its input in screen pixels, draws <see cref="Scene"/> with
/// <see cref="Transform"/> and <see cref="Overlay"/> on top, shows <see cref="Cursor"/>, and redraws
/// on <see cref="Invalidated"/>; whoever owns it — a view model — listens to the events. Mouse
/// wheel or two-finger pinch zoom about the cursor/pinch centre; drag or single-finger swipe pans.
/// A click or tap toggles the room underneath through <see cref="ZoneClicked"/> — always
/// immediately, there being nothing else a click on a room could mean. Empty map space is
/// different: a click/tap there clears the room selection, but a double click/tap resets the zoom
/// instead, so a single click/tap is deferred a short moment to see whether a second one turns it
/// into a double before clearing the selection — otherwise every double click/tap meant to reset
/// the zoom would clear the selection as a surprising side effect. A drag that starts on
/// <see cref="EditableShape"/> moves or resizes that shape instead of the map.
/// </summary>
public sealed class MapInteraction(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Something on screen changed: the view redraws.</summary>
    public event Action? Invalidated;
    /// <summary>A new scene: besides redrawing, the view checks whether it has anything to animate.</summary>
    public event Action? SceneChanged;
    public event Action? CursorChanged;

    /// <summary>Raised with the zone id on every click/tap that lands on a room.</summary>
    public event Action<string>? ZoneClicked;
    /// <summary>Raised when a single click/tap (confirmed not to be the first half of a double) lands on empty map space, to clear the current room selection.</summary>
    public event Action? EmptySpaceClicked;
    /// <summary>
    /// Every single click or tap outside picking, in world metres, raised before <see cref="ZoneClicked"/>
    /// or <see cref="EmptySpaceClicked"/>: what lies there besides rooms (zones, furniture) is for the
    /// listener to find.
    /// </summary>
    public event Action<CorePoint>? WorldClicked;

    public MapScene Scene
    {
        get;
        set
        {
            field = value;
            _transformStale = true;
            SceneChanged?.Invoke();
            Invalidate();
        }
    } = new();

    /// <summary>The view's size in pixels; set by the view whenever it changes.</summary>
    public Size2 Size
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            _transformStale = true;
            Invalidate();
        }
    } = new(1, 1);

    public MapCursor Cursor
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            CursorChanged?.Invoke();
        }
    }

    // ---- Zoom and pan -----------------------------------------------------------------

    public double Zoom { get; private set; } = 1;
    private Vec2 _pan;
    private MapTransform? _transform;
    private bool _transformStale = true;

    /// <summary>World metres to screen pixels, as the scene is fitted, zoomed and panned now; null when the scene has nothing to show.</summary>
    public MapTransform? Transform
    {
        get
        {
            if (_transformStale)
            {
                _transform = MapGeometry.WorldToScreen(Scene, Size, Zoom, _pan);
                _transformStale = false;
            }
            return _transform;
        }
    }

    /// <summary>The same, or the identity when there is nothing to show.</summary>
    public MapTransform WorldToScreen => Transform ?? MapTransform.Identity;

    private void Invalidate() => Invalidated?.Invoke();

    /// <summary>The zoom or the pan changed: the transform with them.</summary>
    private void Moved()
    {
        _transformStale = true;
        Invalidate();
    }

    public void ResetView()
    {
        Zoom = 1;
        _pan = default;
        Moved();
    }

    /// <summary>Screen point to world metres, using the current transform.</summary>
    public Vec2? ToWorld(Vec2 screen) => WorldToScreen.Inverse()?.Transform(screen);

    /// <summary>Scales by <paramref name="factor"/> while keeping the world point under <paramref name="cursor"/> fixed.</summary>
    private void ZoomAbout(Vec2 cursor, double factor)
    {
        var newZoom = Math.Clamp(Zoom * factor, 0.5, 12);
        factor = newZoom / Zoom;
        var centre = new Vec2(Size.Width / 2, Size.Height / 2);
        var offset = cursor - centre;
        _pan = (_pan - offset) * factor + offset;
        Zoom = newZoom;
        Moved();
    }

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
            Cursor = value == MapPick.None ? MapCursor.Default : MapCursor.Cross;
            Invalidate();
        }
    }

    /// <summary>For <see cref="MapPick.Point"/>: the outline to show under the cursor, as corners relative to its centre, in metres.</summary>
    public IReadOnlyList<CorePoint>? PlacementShape
    {
        get;
        set { field = value; Invalidate(); }
    }

    /// <summary>Both ends of the line the user drew, in world metres.</summary>
    public event Action<CorePoint, CorePoint>? LinePicked;
    /// <summary>Two opposite corners of the rectangle the user drew, in world metres.</summary>
    public event Action<CorePoint, CorePoint>? RectanglePicked;
    /// <summary>The point the user clicked, in world metres.</summary>
    public event Action<CorePoint>? PointPicked;
    /// <summary>The point under the cursor while picking, or null once it leaves; for a coordinate readout.</summary>
    public event Action<CorePoint?>? LinePointMoved;

    private Vec2? _lineStart;
    private Vec2? _linePreview;

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
            Invalidate();
        }
    }

    /// <summary>Whether <see cref="EditableShape"/> shows corner handles: an upright rectangle resized from the opposite corner.</summary>
    public bool EditableShapeResizable
    {
        get;
        set { field = value; Invalidate(); }
    }

    /// <summary>Raised once per drag of <see cref="EditableShape"/>, when it is dropped, with its new corners.</summary>
    public event Action<IReadOnlyList<CorePoint>>? ShapeEdited;

    /// <summary>
    /// The shortest side a rectangle may have, in metres, while it is drawn or resized: the corner
    /// being pulled stops that far from the opposite one instead of letting the shape shrink below
    /// what will be accepted. Zero lets it shrink freely.
    /// </summary>
    public double MinimumShapeSide { get; set; }

    private Vec2 KeepMinimum(Vec2 anchor, Vec2 corner, Vec2? side = null) =>
        Vec2.Of(MapShapes.KeepMinimum(anchor.ToPoint(), corner.ToPoint(), MinimumShapeSide, side?.ToPoint()));

    /// <summary>What the drag holds: -1 the shape itself, 0 to 3 one of its corners, null nothing.</summary>
    private int? _shapeGrip;
    private Vec2 _shapeGripWorld;
    private Vec2 _touchGripScreen;
    private IReadOnlyList<CorePoint>? _shapePreview;
    private const double HandleReach = 10;

    /// <summary>What lies under a screen point of the editable shape: a corner handle (0 to 3), the shape itself (-1), or nothing.</summary>
    private int? ShapeGripAt(Vec2 screen)
    {
        if (Picking != MapPick.None || EditableShape is not { Count: 4 } shape) return null;
        if (EditableShapeResizable)
            for (var i = 0; i < 4; i++)
                if ((WorldToScreen.Transform(shape[i]) - screen).Length <= HandleReach) return i;
        return ToWorld(screen) is { } w && MapShapes.Contains(shape, w.X, w.Y) ? -1 : null;
    }

    private void GripShape(int grip, Vec2 screen)
    {
        if (ToWorld(screen) is not { } w) return;
        _shapeGrip = grip;
        _shapeGripWorld = w;
        _shapePreview = null;
    }

    /// <summary>Where the shape would land with the pointer at <paramref name="screen"/>: moved along, or stretched from the corner opposite the handle.</summary>
    private void DragShape(Vec2 screen)
    {
        if (_shapeGrip is not { } grip || EditableShape is not { Count: 4 } shape || ToWorld(screen) is not { } w) return;
        if (grip < 0)
        {
            // Its first corner lands on the grid, so a shape that sat off it is brought back onto it.
            var moved = new Vec2(shape[0].X + w.X - _shapeGripWorld.X, shape[0].Y + w.Y - _shapeGripWorld.Y);
            var snapped = Snap(moved);
            _shapePreview = MapShapes.Translate(shape, snapped.X - shape[0].X, snapped.Y - shape[0].Y);
        }
        else
        {
            var opposite = shape[(grip + 2) % 4];
            var corner = KeepMinimum(Vec2.Of(opposite), Snap(w), Vec2.Of(shape[grip]));
            _shapePreview = MapShapes.Rectangle(opposite, corner.ToPoint());
        }
        _pickCursor = null;
        Invalidate();
    }

    private void ReleaseShape(bool drop)
    {
        var dropped = _shapePreview;
        _shapeGrip = null;
        _shapePreview = null;
        Invalidate();
        if (drop && dropped is not null) ShapeEdited?.Invoke(dropped);
    }

    /// <summary>
    /// Escape: drops a shape being dragged back where it was. False when no shape was being dragged,
    /// so the key can mean something else (leaving a drawing, erasing a zone). The view lets go of
    /// the pointer when this returns true.
    /// </summary>
    public bool CancelGesture()
    {
        if (_shapeGrip is null) return false;
        ReleaseShape(drop: false);
        _dragStart = null;
        return true;
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
        set { field = value; Invalidate(); }
    }

    /// <summary>Where the snapped pointer is while aiming, for its marker.</summary>
    private Vec2? _pickCursor;

    private (double X0, double Y0, double Step)? SnapSpec => SnapToGrid ? Scene.GridSpec : null;

    private Vec2 Snap(Vec2 world) => SnapSpec is { } g
        ? new Vec2(g.X0 + Math.Round((world.X - g.X0) / g.Step) * g.Step, g.Y0 + Math.Round((world.Y - g.Y0) / g.Step) * g.Step)
        : world;

    private Vec2? SnappedWorld(Vec2 screen) => ToWorld(screen) is { } w ? Snap(w) : null;

    // ---- What to draw over the map ---------------------------------------------------

    /// <summary>The grid under what is being aimed or dragged, where the pointer lands on it, and what is being drawn.</summary>
    public MapOverlay Overlay
    {
        get
        {
            var m = WorldToScreen;
            var overlay = MapOverlay.Empty;
            if (SnapSpec is { } grid && (Picking != MapPick.None || EditableShape is not null))
                overlay = overlay with { Grid = grid, SnapMarker = _pickCursor is { } cursor ? m.Transform(cursor) : null };
            switch (Picking)
            {
                case MapPick.Line when _lineStart is { } start:
                    return overlay with { CutFrom = m.Transform(start), CutTo = _linePreview is { } end ? m.Transform(end) : null };
                case MapPick.Rectangle when _lineStart is { } corner:
                    return overlay with { RectangleFrom = m.Transform(corner), RectangleTo = _linePreview is { } other ? m.Transform(other) : null };
                case MapPick.Point when _linePreview is { } at && PlacementShape is { Count: > 2 } shape:
                    return overlay with { PendingShape = [.. shape.Select(p => m.Transform(at.X + p.X, at.Y + p.Y))] };
                case MapPick.None when EditableShape is { Count: 4 } editable:
                    var shown = (_shapePreview ?? editable).Select(m.Transform).ToList();
                    return overlay with
                    {
                        PendingShape = _shapePreview is not null ? shown : null,
                        Handles = EditableShapeResizable ? shown : null,
                    };
                default:
                    return overlay;
            }
        }
    }

    // ---- Mouse --------------------------------------------------------------------------

    private Vec2? _dragStart;
    private Vec2 _panAtDragStart;
    private bool _dragged;

    /// <summary>The user's double-click speed (500 ms by default in Windows); set by the view from the system's setting.</summary>
    public TimeSpan DoubleClickTime { get; set; } = TimeSpan.FromMilliseconds(500);

    private const double DoubleClickMaxDistance = 24;
    private DateTimeOffset _lastClickTime;
    private Vec2 _lastClickPosition;
    /// <summary>The single click on empty space waiting to see whether a second one follows; dropped by anything that overrides it.</summary>
    private object? _pendingEmptySpaceClear;
    /// <summary>The latest single click waiting for <see cref="ClickConfirmed"/>; dropped by a double click.</summary>
    private object? _pendingConfirmation;

    public void PointerWheel(Vec2 position, double delta) => ZoomAbout(position, delta > 0 ? 1.2 : 1 / 1.2);

    /// <summary>The main button went down; the view captures the pointer until it comes up.</summary>
    public void PointerPressed(Vec2 position)
    {
        _dragStart = position;
        _panAtDragStart = _pan;
        _dragged = false;
        // Pressing on the editable shape holds it; anywhere else the drag pans the map. Whatever an
        // earlier gesture may have left held is let go first, never carried into this one.
        _shapeGrip = null;
        _shapePreview = null;
        if (ShapeGripAt(position) is { } grip) GripShape(grip, position);
    }

    public void PointerMoved(Vec2 position, bool pressed)
    {
        if (_shapeGrip is not null && _dragStart is { } held && pressed)
        {
            // Same 4 px dead zone as panning, so a click on the shape stays a click.
            if (!_dragged && (position - held).Length < 4) return;
            _dragged = true;
            DragShape(position);
            return;
        }
        if (!pressed) UpdateHoverCursor(position);
        if (Picking != MapPick.None)
        {
            var world = SnappedWorld(position);
            _pickCursor = world;
            if (_lineStart is null && Picking != MapPick.Point) Invalidate();   // the snap marker follows
            LinePointMoved?.Invoke(world?.ToPoint());
            // A placement follows the cursor from the start; a line or a rectangle once it has a first point.
            if (_lineStart is not null || Picking == MapPick.Point)
            {
                _linePreview = Picking == MapPick.Rectangle && _lineStart is { } first && world is { } other ? KeepMinimum(first, other) : world;
                Invalidate();
            }
        }
        if (_dragStart is not { } start || !pressed) return;
        var delta = position - start;
        if (!_dragged && delta.Length < 4) return;
        _dragged = true;
        _pan = _panAtDragStart + delta;
        Moved();
    }

    /// <summary>
    /// The main button came up. <paramref name="clickCount"/> is what the system counted (2 for a
    /// double click), but it is not trusted alone: a Surface trackpad's double-click/double-tap-to-
    /// click was observed to never report 2 (each click reads as a fresh single), so the same
    /// timing/distance test as for touch is applied too. The view lets go of the pointer afterwards.
    /// </summary>
    public void PointerReleased(Vec2 position, int clickCount)
    {
        var wasClick = _dragStart is not null && !_dragged;
        _dragStart = null;
        if (_shapeGrip is not null) ReleaseShape(drop: !wasClick);
        if (wasClick) HandleClick(position, clickCount >= 2 || IsDoubleClick(position));
    }

    /// <summary>
    /// The view lost the pointer mid-gesture — Alt+Tab, a window taking the focus — and then no
    /// button-up ever comes. A shape being dragged is dropped where it was, unsent: otherwise the
    /// next drag, meant to pan the map, would carry it along and send it to the robot on release.
    /// </summary>
    public void PointerCaptureLost()
    {
        if (_shapeGrip is not null) ReleaseShape(drop: false);
        _dragStart = null;
    }

    public void PointerExited()
    {
        if (_pickCursor is null) return;
        _pickCursor = null;
        Invalidate();
    }

    /// <summary>The cursor says what pressing would do: move the shape, pull a corner, or nothing special.</summary>
    private void UpdateHoverCursor(Vec2 screen)
    {
        if (Picking != MapPick.None) return;
        Cursor = ShapeGripAt(screen) switch
        {
            -1 => MapCursor.Move,
            { } corner => CornerCursor(corner),
            _ => MapCursor.Default,
        };
    }

    /// <summary>
    /// The diagonal arrow along the corner's bisector, as the corner sits on screen: the map is
    /// drawn with y up and possibly turned, so which corner is where cannot be told from its index.
    /// </summary>
    private MapCursor CornerCursor(int corner)
    {
        if (EditableShape is not { Count: 4 } shape) return MapCursor.Move;
        var at = WorldToScreen.Transform(shape[corner]);
        var opposite = WorldToScreen.Transform(shape[(corner + 2) % 4]);
        return PullsAlongMainDiagonal(at, opposite) ? MapCursor.ResizeNorthwestSoutheast : MapCursor.ResizeNortheastSouthwest;
    }

    /// <summary>Whether a corner at <paramref name="at"/> on screen pulls along ↖↘ rather than ↗↙: screen y points down, so it does when it sits down-right or up-left of its opposite.</summary>
    public static bool PullsAlongMainDiagonal(Vec2 at, Vec2 opposite) => (at.X - opposite.X) * (at.Y - opposite.Y) > 0;

    // ---- Touch: single finger pans, two fingers pinch-zoom, a still touch is a tap. Fed from the
    // view's manipulation events, which report a whole gesture rather than single contacts, so a
    // tap or a double tap is a manipulation whose net movement and scale stayed negligible. ----

    /// <summary>A touch that starts on the editable shape drags it rather than the map.</summary>
    public void ManipulationStarted(Vec2 origin)
    {
        if (ShapeGripAt(origin) is not { } grip) return;
        GripShape(grip, origin);
        _touchGripScreen = origin;
    }

    /// <param name="origin">Where the gesture is, for zooming about it.</param>
    /// <param name="translation">The pan since the last delta.</param>
    /// <param name="scale">The pinch since the last delta, 1 for none.</param>
    /// <param name="cumulativeTranslation">The pan since the gesture started.</param>
    public void ManipulationDelta(Vec2 origin, Vec2 translation, double scale, Vec2 cumulativeTranslation)
    {
        if (_shapeGrip is not null)
        {
            DragShape(_touchGripScreen + cumulativeTranslation);
            return;
        }
        if (Math.Abs(scale - 1) > 0.0005) ZoomAbout(origin, scale);
        if (translation.Length > 0) _pan += translation;
        Moved();
    }

    public void ManipulationCompleted(Vec2 origin, Vec2 totalTranslation, double totalScale)
    {
        var isTap = totalTranslation.Length < 6 && Math.Abs(totalScale - 1) < 0.03;
        if (_shapeGrip is not null) ReleaseShape(drop: !isTap);
        if (isTap && ManipulationTaps) HandleClick(origin, IsDoubleClick(origin));
    }

    /// <summary>
    /// Whether a manipulation that hardly moved counts as a tap, for a UI that only reports taps
    /// that way (WPF). One that reports them apart, through <see cref="Tap"/>, turns this off so a
    /// tap is never counted twice — which would read as a double tap.
    /// </summary>
    public bool ManipulationTaps { get; set; } = true;

    /// <summary>A tap, from a UI that reports taps apart from manipulations (WinUI).</summary>
    public void Tap(Vec2 position) => HandleClick(position, IsDoubleClick(position));

    // ---- Clicks and taps --------------------------------------------------------------

    /// <summary>True when this click/tap lands within the double-click time and distance of the previous one. Always records this one as "the last click" regardless, so a used double can't chain into a triple being read as another double.</summary>
    private bool IsDoubleClick(Vec2 position)
    {
        var now = _time.GetUtcNow();
        var isDouble = now - _lastClickTime < DoubleClickTime && (position - _lastClickPosition).Length < DoubleClickMaxDistance;
        _lastClickTime = isDouble ? DateTimeOffset.MinValue : now;
        _lastClickPosition = position;
        return isDouble;
    }

    /// <summary>
    /// A room always reacts right away; only empty space needs to wait and see whether a second
    /// click/tap turns this into a double, since that's the only place the two mean different
    /// things. The same wait applies on every map, so a double click to reset the zoom never
    /// lets go of what was chosen.
    /// </summary>
    private void HandleClick(Vec2 position, bool isDouble)
    {
        if (Picking != MapPick.None)
        {
            PickAt(position);
            return;
        }

        var world = ToWorld(position);
        if (world is { } clicked && !isDouble)
        {
            WorldClicked?.Invoke(clicked.ToPoint());
            if (ClickConfirmed is not null)
            {
                var confirmation = _pendingConfirmation = new object();
                _ = ConfirmAfterDoubleClickTimeAsync(confirmation, clicked.ToPoint());
            }
        }
        if (isDouble) _pendingConfirmation = null;
        var zone = world is { } w ? Scene.ZoneAt(w.X, w.Y) : null;
        if (zone is not null)
        {
            CancelPendingClear();
            ZoneClicked?.Invoke(zone);
        }
        else if (isDouble)
        {
            CancelPendingClear();
            ResetView();
        }
        else
        {
            var pending = _pendingEmptySpaceClear = new object();
            _ = ClearAfterDoubleClickTimeAsync(pending);
        }
    }

    /// <summary>
    /// A single click or tap, wherever it landed, once the double-click time has passed with no
    /// second one: for what the first half of a double click must not do, such as letting go of a
    /// chosen shape. Only the latest click is confirmed. Raised after <see cref="WorldClicked"/>.
    /// </summary>
    public event Action<CorePoint>? ClickConfirmed;

    private async Task ConfirmAfterDoubleClickTimeAsync(object pending, CorePoint at)
    {
        await Task.Delay(DoubleClickTime, _time);
        if (!ReferenceEquals(_pendingConfirmation, pending)) return;
        _pendingConfirmation = null;
        ClickConfirmed?.Invoke(at);
    }

    private void PickAt(Vec2 position)
    {
        if (SnappedWorld(position) is not { } picked) return;
        if (Picking == MapPick.Point)
        {
            PointPicked?.Invoke(picked.ToPoint());
            return;
        }
        if (_lineStart is not { } start)
        {
            _lineStart = picked;
            Invalidate();
            return;
        }
        // Two clicks in the same spot would be a zero-length cut or an empty zone, which the
        // robot refuses; treat it as the user changing their mind about where to start.
        if ((WorldToScreen.Transform(picked) - WorldToScreen.Transform(start)).Length < 8)
        {
            _lineStart = picked;
            Invalidate();
            return;
        }
        _lineStart = null;
        _linePreview = null;
        Invalidate();
        if (Picking == MapPick.Rectangle) picked = KeepMinimum(start, picked);
        if (Picking == MapPick.Rectangle) RectanglePicked?.Invoke(start.ToPoint(), picked.ToPoint());
        else LinePicked?.Invoke(start.ToPoint(), picked.ToPoint());
    }

    /// <summary>
    /// Clears the selection once the double-click time has passed, unless something overrode this
    /// click meanwhile. Resumes on the caller's thread, the UI's.
    /// </summary>
    private async Task ClearAfterDoubleClickTimeAsync(object pending)
    {
        await Task.Delay(DoubleClickTime, _time);
        if (!ReferenceEquals(_pendingEmptySpaceClear, pending)) return;
        _pendingEmptySpaceClear = null;
        EmptySpaceClicked?.Invoke();
    }

    private void CancelPendingClear() => _pendingEmptySpaceClear = null;
}
