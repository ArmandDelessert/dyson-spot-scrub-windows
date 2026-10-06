using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;

namespace DyssCockpit.Presentation.Tests;

/// <summary>What clicks, drags and the wheel do on a map, fed in screen pixels as a view would.</summary>
public class MapInteractionTests
{
    /// <summary>A 2 m square on a 50 cm grid: room 10 on the left half, room 11 on the right.</summary>
    private static MapScene Scene() => new()
    {
        Grid = new MapGrid(new MapDimensions(4, 4, 0.5, 0, 0), [10, 10, 11, 11, 10, 10, 11, 11, 10, 10, 11, 11, 10, 10, 11, 11]),
    };

    private static MapInteraction Map(ManualTime? time = null) => new(time) { Scene = Scene(), Size = new Size2(400, 400) };

    /// <summary>Where a world point is on screen.</summary>
    private static Vec2 At(MapInteraction map, double x, double y) => map.WorldToScreen.Transform(x, y);

    /// <summary>A screen point in the margin, off the grid: empty map space.</summary>
    private static readonly Vec2 Empty = new(3, 3);

    private static void Click(MapInteraction map, Vec2 at)
    {
        map.PointerPressed(at);
        map.PointerReleased(at, clickCount: 1);
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    // ---- Clicks -------------------------------------------------------------------------

    [Fact]
    public void AClickOnARoomNamesItAndTheWorldPoint()
    {
        var map = Map();
        var zones = new List<string>();
        var points = new List<Point>();
        map.ZoneClicked += zones.Add;
        map.WorldClicked += points.Add;

        Click(map, At(map, 0.5, 1));
        Click(map, At(map, 1.5, 1));

        Assert.Equal(["10", "11"], zones);
        Assert.Equal(0.5, points[0].X, 6);
        Assert.Equal(1, points[0].Y, 6);
    }

    [Fact]
    public void ADragPansTheMapInsteadOfClicking()
    {
        var map = Map();
        var clicks = 0;
        map.WorldClicked += _ => clicks++;
        var start = At(map, 0.5, 1);
        var before = map.WorldToScreen;

        map.PointerPressed(start);
        map.PointerMoved(start + new Vec2(50, 20), pressed: true);
        map.PointerReleased(start + new Vec2(50, 20), clickCount: 1);

        Assert.Equal(0, clicks);
        Assert.Equal(before.OffsetX + 50, map.WorldToScreen.OffsetX, 6);
        Assert.Equal(before.OffsetY + 20, map.WorldToScreen.OffsetY, 6);
    }

    [Fact]
    public void AWobbleUnderFourPixelsIsStillAClick()
    {
        var map = Map();
        var zones = new List<string>();
        map.ZoneClicked += zones.Add;
        var start = At(map, 0.5, 1);

        map.PointerPressed(start);
        map.PointerMoved(start + new Vec2(2, 1), pressed: true);
        map.PointerReleased(start + new Vec2(2, 1), clickCount: 1);

        Assert.Equal(["10"], zones);
    }

    [Fact]
    public async Task AClickOnEmptySpaceClearsOnlyOnceNoSecondClickHasCome()
    {
        // No UI thread here: the deferred clear runs wherever the timer fires.
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new ManualTime();
        var map = Map(time);
        var cleared = 0;
        map.EmptySpaceClicked += () => cleared++;

        Click(map, Empty);
        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(0, cleared);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForAsync(() => cleared == 1);
    }

    [Fact]
    public async Task ADoubleClickOnEmptySpaceResetsTheZoomWithoutClearing()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new ManualTime();
        var map = Map(time);
        var cleared = 0;
        map.EmptySpaceClicked += () => cleared++;
        map.PointerWheel(new Vec2(200, 200), 120);
        Assert.Equal(1.2, map.Zoom, 6);

        Click(map, Empty);
        time.Advance(TimeSpan.FromMilliseconds(200));
        Click(map, Empty + new Vec2(3, 2));
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, map.Zoom);
        Assert.Equal(0, cleared);
    }

    [Fact]
    public async Task ASingleClickIsConfirmedWhereverItLandedButNotTheHalvesOfADouble()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new ManualTime();
        var map = Map(time);
        var confirmed = new List<Point>();
        map.ClickConfirmed += confirmed.Add;

        Click(map, At(map, 0.5, 1));                 // on a room
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => confirmed.Count == 1);
        Assert.Equal(0.5, confirmed[0].X, 6);

        Click(map, Empty);                           // a double click on empty space
        time.Advance(TimeSpan.FromMilliseconds(200));
        Click(map, Empty + new Vec2(2, 2));
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(confirmed);
    }

    [Fact]
    public async Task ADoubleTapAsWinUIReportsItResetsTheZoomWithoutClearing()
    {
        // A tap, then the second one reported only as a double tap: never two taps.
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new ManualTime();
        var map = Map(time);
        map.ManipulationTaps = false;
        var cleared = 0;
        map.EmptySpaceClicked += () => cleared++;
        map.PointerWheel(new Vec2(200, 200), 120);

        map.Tap(Empty);
        time.Advance(TimeSpan.FromMilliseconds(200));
        map.DoubleTap(Empty + new Vec2(3, 2));
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, map.Zoom);
        Assert.Equal(0, cleared);
    }

    [Fact]
    public void TapsReportedApartAreNotAlsoTakenFromTheManipulation()
    {
        var map = Map();
        map.ManipulationTaps = false;
        var zones = new List<string>();
        map.ZoneClicked += zones.Add;
        var at = At(map, 0.5, 1);

        map.ManipulationCompleted(at, new Vec2(1, 1), 1);
        Assert.Empty(zones);
        map.Tap(at);
        Assert.Equal(["10"], zones);
    }

    [Fact]
    public void TheWheelZoomsAboutThePointer()
    {
        var map = Map();
        var pointer = At(map, 0.5, 1);

        map.PointerWheel(pointer, 120);

        // The world point under the pointer stays under it.
        var after = map.ToWorld(pointer)!.Value;
        Assert.Equal(0.5, after.X, 6);
        Assert.Equal(1, after.Y, 6);
    }

    // ---- Picking -------------------------------------------------------------------------

    [Fact]
    public void TwoClicksPickARectangleOnTheGrid()
    {
        var map = Map();
        map.SnapToGrid = true;
        map.MinimumShapeSide = 0.3;
        map.Picking = MapPick.Rectangle;
        (Point A, Point B)? picked = null;
        map.RectanglePicked += (a, b) => picked = (a, b);
        Assert.Equal(MapCursor.Cross, map.Cursor);

        Click(map, At(map, 0.1, 0.2));
        Assert.NotNull(map.Overlay.RectangleFrom);
        Click(map, At(map, 1.4, 0.9));

        Assert.NotNull(picked);
        Assert.Equal((0, 0), (picked.Value.A.X, picked.Value.A.Y));
        Assert.Equal((1.5, 1), (picked.Value.B.X, picked.Value.B.Y));
        Assert.Null(map.Overlay.RectangleFrom);
    }

    [Fact]
    public void ASecondClickOnTheFirstStartsTheRectangleAgain()
    {
        var map = Map();
        map.Picking = MapPick.Rectangle;
        var picked = 0;
        map.RectanglePicked += (_, _) => picked++;
        var first = At(map, 0.5, 0.5);

        Click(map, first);
        Click(map, first + new Vec2(3, 3));

        Assert.Equal(0, picked);
        Assert.NotNull(map.Overlay.RectangleFrom);
    }

    [Fact]
    public void ThePickedPointFollowsThePointerAndLeavingHidesTheMarker()
    {
        var map = Map();
        map.SnapToGrid = true;
        map.Picking = MapPick.Line;
        Point? under = null;
        map.LinePointMoved += p => under = p;

        map.PointerMoved(At(map, 0.6, 1.1), pressed: false);

        Assert.Equal((0.5, 1.0), (under!.X, under.Y));
        Assert.NotNull(map.Overlay.SnapMarker);
        map.PointerExited();
        Assert.Null(map.Overlay.SnapMarker);
    }

    // ---- Dragging a shape ----------------------------------------------------------------

    [Fact]
    public void DraggingTheShapeMovesItOnTheGrid()
    {
        var map = Map();
        map.SnapToGrid = true;
        map.EditableShape = MapShapes.Rectangle(new Point(0.5, 0.5), new Point(1, 1));
        IReadOnlyList<Point>? dropped = null;
        map.ShapeEdited += corners => dropped = corners;
        var from = At(map, 0.75, 0.75);

        map.PointerMoved(from, pressed: false);
        Assert.Equal(MapCursor.Move, map.Cursor);
        map.PointerPressed(from);
        map.PointerMoved(At(map, 1.3, 0.75), pressed: true);
        Assert.NotNull(map.Overlay.PendingShape);
        map.PointerReleased(At(map, 1.3, 0.75), clickCount: 1);

        var centre = MapShapes.Centre(dropped!);
        Assert.Equal(1.25, centre.X, 6);
        Assert.Equal(0.75, centre.Y, 6);
    }

    [Fact]
    public void EscapePutsTheShapeBackUnsent()
    {
        var map = Map();
        map.EditableShape = MapShapes.Rectangle(new Point(0.5, 0.5), new Point(1, 1));
        var dropped = 0;
        map.ShapeEdited += _ => dropped++;
        var from = At(map, 0.75, 0.75);

        map.PointerPressed(from);
        map.PointerMoved(At(map, 1.5, 0.75), pressed: true);

        Assert.True(map.CancelGesture());
        Assert.False(map.CancelGesture());
        map.PointerReleased(At(map, 1.5, 0.75), clickCount: 1);
        Assert.Equal(0, dropped);
        Assert.Null(map.Overlay.PendingShape);
    }

    [Fact]
    public void ACornerOfAResizableShapeStretchesItButNotBelowTheMinimum()
    {
        var map = Map();
        map.EditableShape = MapShapes.Rectangle(new Point(0.5, 0.5), new Point(1.5, 1.5));
        map.EditableShapeResizable = true;
        map.MinimumShapeSide = 0.3;
        IReadOnlyList<Point>? dropped = null;
        map.ShapeEdited += corners => dropped = corners;
        Assert.Equal(4, map.Overlay.Handles!.Count);

        // The top-right corner, pulled past the bottom-left one: it stops 30 cm short.
        map.PointerPressed(At(map, 1.5, 1.5));
        map.PointerMoved(At(map, 0.2, 0.2), pressed: true);
        map.PointerReleased(At(map, 0.2, 0.2), clickCount: 1);

        var (width, height) = MapShapes.Sides(dropped!);
        Assert.Equal(0.3, width, 6);
        Assert.Equal(0.3, height, 6);
        Assert.Equal(0.5, dropped!.Min(p => p.X), 6);
        Assert.Equal(0.5, dropped!.Min(p => p.Y), 6);
    }
}

/// <summary>A clock that only moves when told to, firing the timers that fall due.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        List<ManualTimer> due;
        lock (_timers) due = [.. _timers.Where(t => t.DueAt <= _now)];
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; } = DateTimeOffset.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = DateTimeOffset.MaxValue;   // one-shot, which is all a delay needs
            callback(state);
        }

        public void Dispose()
        {
            lock (clock._timers) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
