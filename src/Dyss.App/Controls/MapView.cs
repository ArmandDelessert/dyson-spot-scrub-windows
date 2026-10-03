using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dyss.App.Rendering;
using Dyss.App.Services;
using Dyss.Presentation.Map;

namespace Dyss.App.Controls;

/// <summary>
/// Shows a map: draws the scene of its <see cref="Interaction"/> and hands it the mouse and touch
/// input, which it turns into zoom, pan, clicks and drags (see <see cref="MapInteraction"/>). What
/// is left here is WPF's part: drawing, the pointer capture, the cursor, and animating the robot.
/// </summary>
public sealed class MapView : FrameworkElement
{
    public static readonly DependencyProperty InteractionProperty = DependencyProperty.Register(
        nameof(Interaction), typeof(MapInteraction), typeof(MapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((MapView)d).Attach((MapInteraction?)e.OldValue, (MapInteraction?)e.NewValue)));

    /// <summary>The map's state and gestures, owned by a view model.</summary>
    public MapInteraction? Interaction
    {
        get => (MapInteraction?)GetValue(InteractionProperty);
        set => SetValue(InteractionProperty, value);
    }

    // The user's own double-click speed from Windows settings (500 ms by default). Anyone who needs
    // a slower double click will have set it there, so the deferred clear waits exactly that long.
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime);

    public MapView()
    {
        Focusable = true;
        ClipToBounds = true;
        IsManipulationEnabled = true;
        AddVisualChild(_markers);
        IsVisibleChanged += (_, _) => UpdateAnimation();
        // The map's colours follow the Windows theme; the scene does not change with it.
        Loaded += (_, _) =>
        {
            ThemeService.Changed -= OnThemeChanged;
            ThemeService.Changed += OnThemeChanged;
            UpdateAnimation();
        };
        Unloaded += (_, _) => { ThemeService.Changed -= OnThemeChanged; StopAnimation(); };
    }

    private void Attach(MapInteraction? old, MapInteraction? map)
    {
        if (old is not null)
        {
            old.Invalidated -= InvalidateVisual;
            old.SceneChanged -= UpdateAnimation;
            old.CursorChanged -= UpdateCursor;
        }
        if (map is not null)
        {
            map.DoubleClickTime = DoubleClickWindow;
            map.Size = new Size2(CurrentSize.Width, CurrentSize.Height);
            map.Invalidated += InvalidateVisual;
            map.SceneChanged += UpdateAnimation;
            map.CursorChanged += UpdateCursor;
        }
        UpdateCursor();
        UpdateAnimation();
    }

    private void OnThemeChanged() => InvalidateVisual();

    private Size CurrentSize => new(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));

    public void ResetView() => Interaction?.ResetView();

    /// <summary>
    /// Escape: drops a shape being dragged back where it was. False when no shape was being dragged,
    /// so the key can mean something else (leaving a drawing, erasing a zone).
    /// </summary>
    public bool CancelGesture()
    {
        if (Interaction?.CancelGesture() != true) return false;
        ReleaseMouseCapture();
        return true;
    }

    private void UpdateCursor() => Cursor = Interaction?.Cursor switch
    {
        MapCursor.Cross => Cursors.Cross,
        MapCursor.Move => Cursors.SizeAll,
        MapCursor.ResizeNorthwestSoutheast => Cursors.SizeNWSE,
        MapCursor.ResizeNortheastSouthwest => Cursors.SizeNESW,
        _ => null,
    };

    // ---- Drawing --------------------------------------------------------------------------

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (Interaction is { } map) map.Size = new Size2(CurrentSize.Width, CurrentSize.Height);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Interaction is not { } map) return;
        var size = CurrentSize;
        MapRenderer.Render(drawingContext, map.Scene, size, map.Transform);
        MapRenderer.DrawOverlay(drawingContext, map.Overlay, map.WorldToScreen, size);
        DrawMarkers();
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
        if (Interaction is not { } map) return;
        var scene = map.Scene;
        // Windows' "show animations" setting off: everything stands still.
        var animate = SystemParameters.ClientAreaAnimation;
        var seconds = animate ? Clock.Elapsed.TotalSeconds : 0;
        var robotAt = animate && scene.SmoothRobotMotion && !scene.RobotDocked && scene.Robot is { } robot
            ? _glide.At(robot, Clock.Elapsed.TotalSeconds)
            : null;
        RobotMarkers.Draw(dc, scene, map.WorldToScreen, seconds, robotAt);
        // A new position starts a glide, and one that has arrived stops the frames.
        if (robotAt is not null) Dispatcher.BeginInvoke(UpdateAnimation, DispatcherPriority.Render);
    }

    /// <summary>Keeps redrawing the markers while something moves and the map can be seen, and only then.</summary>
    private void UpdateAnimation()
    {
        var wanted = IsVisible && IsLoaded && Interaction?.Scene is { } scene && SystemParameters.ClientAreaAnimation
            && (RobotMarkerLayout.IsAnimated(scene) || (scene.SmoothRobotMotion && _glide.IsGliding(Clock.Elapsed.TotalSeconds)));
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

    // ---- Mouse ----------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Interaction?.PointerWheel(e.GetPosition(this).ToVec2(), e.Delta);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Interaction?.PointerPressed(e.GetPosition(this).ToVec2());
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Interaction?.PointerCaptureLost();
    }

    protected override void OnMouseMove(MouseEventArgs e) =>
        Interaction?.PointerMoved(e.GetPosition(this).ToVec2(), e.LeftButton == MouseButtonState.Pressed);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        Interaction?.PointerReleased(e.GetPosition(this).ToVec2(), e.ClickCount);
        // Released only now: letting go of the capture raises LostMouseCapture at once, which would
        // otherwise drop a dragged shape unsent before the gesture had been read.
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Interaction?.PointerExited();
    }

    // ---- Touch: WPF routes all touch through manipulation events rather than promoting it to
    // mouse events once a control opts in via IsManipulationEnabled. ----

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        e.ManipulationContainer = this;
    }

    protected override void OnManipulationStarted(ManipulationStartedEventArgs e)
    {
        base.OnManipulationStarted(e);
        Interaction?.ManipulationStarted(e.ManipulationOrigin.ToVec2());
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        Interaction?.ManipulationDelta(e.ManipulationOrigin.ToVec2(), e.DeltaManipulation.Translation.ToVec2(),
            e.DeltaManipulation.Scale.X, e.CumulativeManipulation.Translation.ToVec2());
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        Interaction?.ManipulationCompleted(e.ManipulationOrigin.ToVec2(), e.TotalManipulation.Translation.ToVec2(), e.TotalManipulation.Scale.X);
        e.Handled = true;
    }
}
