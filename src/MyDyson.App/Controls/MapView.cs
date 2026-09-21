using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyDyson.App.Rendering;

namespace MyDyson.App.Controls;

/// <summary>
/// Draws a <see cref="MapScene"/>. Mouse wheel or two-finger pinch zoom about the cursor/pinch
/// centre; drag or single-finger swipe pans. A click or tap toggles the room underneath through
/// <see cref="ZoneClicked"/>, or raises <see cref="EmptySpaceClicked"/> to clear the selection when
/// there is no room there. A double click or double tap zooms in on a room instead, or resets the
/// view when it lands on empty space — the same split, on purpose, so the gesture always means
/// "act on this room" vs. "act on the whole map". The single-tap action is deferred a short moment
/// so it can be cancelled if a second tap turns it into a double: a click/tap is otherwise
/// indistinguishable from the first half of a double click/tap, and firing its action right away
/// meant double-clicking a room briefly toggled it before zooming in, and double-clicking empty
/// space cleared the whole selection before resetting the zoom — both surprising in practice.
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

    /// <summary>Raised with the zone id when a single click/tap (confirmed not to be the first half of a double) lands on a room.</summary>
    public event Action<string>? ZoneClicked;
    /// <summary>Raised when a single click/tap (confirmed not to be the first half of a double) lands on empty map space, to clear the current room selection.</summary>
    public event Action? EmptySpaceClicked;

    public Matrix WorldToScreen { get; private set; } = Matrix.Identity;
    public double Zoom { get; private set; } = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panAtDragStart;
    private bool _dragged;

    private const double DoubleTapZoomFactor = 1.8;
    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(350);
    private const double DoubleTapMaxDistance = 24;
    private DateTime _lastTapTimeUtc;
    private Point _lastTapPosition;
    private DispatcherTimer? _pendingSingleTap;

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

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        MapRenderer.Render(dc, Scene ?? new MapScene(), size, out var m, Zoom, _pan);
        WorldToScreen = m;
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
            if (e.ClickCount >= 2) { _pendingSingleTap?.Stop(); _pendingSingleTap = null; HandleTap(pos, isDouble: true); }
            else DeferSingleTap(pos);
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
            var now = DateTime.UtcNow;
            var isDoubleTap = now - _lastTapTimeUtc < DoubleTapWindow && (pos - _lastTapPosition).Length < DoubleTapMaxDistance;
            if (isDoubleTap)
            {
                // A used double-tap can't itself chain into a triple-tap being read as another double.
                _lastTapTimeUtc = DateTime.MinValue;
                _pendingSingleTap?.Stop();
                _pendingSingleTap = null;
                HandleTap(pos, isDouble: true);
            }
            else
            {
                _lastTapTimeUtc = now;
                _lastTapPosition = pos;
                DeferSingleTap(pos);
            }
        }
        e.Handled = true;
    }

    /// <summary>
    /// Waits to see whether a second click/tap turns this into a double before acting, so a room
    /// isn't toggled nor the selection cleared just because a double happened to land there.
    /// </summary>
    private void DeferSingleTap(Point pos)
    {
        _pendingSingleTap?.Stop();
        var timer = new DispatcherTimer { Interval = DoubleTapWindow };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _pendingSingleTap = null;
            HandleTap(pos, isDouble: false);
        };
        _pendingSingleTap = timer;
        timer.Start();
    }

    /// <summary>Single click/tap toggles a room or clears the selection; the double variant zooms instead.</summary>
    private void HandleTap(Point pos, bool isDouble)
    {
        var zone = ToWorld(pos) is { } w ? Scene?.ZoneAt(w.X, w.Y) : null;
        if (isDouble)
        {
            if (zone is not null) ZoomAbout(pos, DoubleTapZoomFactor);
            else ResetView();
        }
        else
        {
            if (zone is not null) ZoneClicked?.Invoke(zone);
            else EmptySpaceClicked?.Invoke();
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
