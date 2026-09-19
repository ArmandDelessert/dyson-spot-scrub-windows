using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyDyson.App.Rendering;

namespace MyDyson.App.Controls;

/// <summary>
/// Draws a <see cref="MapScene"/>, with mouse-wheel zoom about the cursor, drag to pan, and a click
/// that reports the zone under the cursor through <see cref="ZoneClicked"/>.
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

    /// <summary>Raised with the zone id when the user clicks a room without dragging.</summary>
    public event Action<string>? ZoneClicked;

    public Matrix WorldToScreen { get; private set; } = Matrix.Identity;
    public double Zoom { get; private set; } = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panAtDragStart;
    private bool _dragged;

    public MapView()
    {
        Focusable = true;
        ClipToBounds = true;
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
        var factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        var newZoom = Math.Clamp(Zoom * factor, 0.5, 12);
        factor = newZoom / Zoom;
        // Keep the world point under the cursor fixed: the zoom is about the viewport centre, so
        // move the pan by the cursor's offset from the centre, scaled.
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var cursor = e.GetPosition(this);
        var offset = cursor - centre;
        _pan = (_pan - offset) * factor + offset;
        Zoom = newZoom;
        InvalidateVisual();
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
        if (wasClick && ToWorld(e.GetPosition(this)) is { } w && Scene?.ZoneAt(w.X, w.Y) is { } zone)
            ZoneClicked?.Invoke(zone);
        e.Handled = true;
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
