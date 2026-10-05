using Dyss.App.Rendering;
using Dyss.Presentation.Map;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Dyss.App.Controls;

/// <summary>
/// Shows a map: draws the scene of its <see cref="Interaction"/> and hands it the pointer input,
/// which it turns into zoom, pan, clicks and drags (see <see cref="MapInteraction"/>). What is left
/// here is WinUI's part: drawing with Win2D, the pointer capture, the cursor, the theme's colours,
/// and animating the robot. Mouse and pen come through the pointer events; touch through the
/// manipulation events, which report a pan and a pinch as one gesture.
/// </summary>
public sealed partial class MapView : UserControl
{
    public static readonly DependencyProperty InteractionProperty = DependencyProperty.Register(
        nameof(Interaction), typeof(MapInteraction), typeof(MapView),
        new PropertyMetadata(null, (d, e) => ((MapView)d).Attach((MapInteraction?)e.OldValue, (MapInteraction?)e.NewValue)));

    /// <summary>The map's state and gestures, owned by a view model.</summary>
    public MapInteraction? Interaction
    {
        get => (MapInteraction?)GetValue(InteractionProperty);
        set => SetValue(InteractionProperty, value);
    }

    private static readonly UISettings Settings = new();

    /// <summary>The map itself, redrawn when something on it changes.</summary>
    private readonly CanvasControl _map = new();
    /// <summary>The robot and its dock, on a layer of their own: redrawing only this one each frame of their animation leaves the map, much heavier to draw, untouched.</summary>
    private readonly CanvasControl _markers = new() { IsHitTestVisible = false, ClearColor = Microsoft.UI.Colors.Transparent };

    public MapView()
    {
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(_map);
        root.Children.Add(_markers);
        Content = root;
        IsTabStop = false;
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;

        _map.Draw += (_, e) => DrawMap(e.DrawingSession);
        _markers.Draw += (_, e) => DrawMarkers(e.DrawingSession);
        SizeChanged += (_, _) => UpdateSize();
        ActualThemeChanged += (_, _) => Redraw();
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    /// <summary>The map's colours, which follow the theme of the window.</summary>
    private MapPalette Palette => ActualTheme == ElementTheme.Light ? MapPalette.Light : MapPalette.Dark;

    private void Attach(MapInteraction? old, MapInteraction? map)
    {
        if (old is not null)
        {
            old.Invalidated -= Redraw;
            old.SceneChanged -= UpdateAnimation;
            old.CursorChanged -= UpdateCursor;
        }
        if (map is not null)
        {
            // The user's own double-click speed, from the Windows settings.
            map.DoubleClickTime = TimeSpan.FromMilliseconds(Settings.DoubleClickTime);
            // A tap comes as a Tapped event, never as a manipulation: see OnTapped.
            map.ManipulationTaps = false;
            map.Invalidated += Redraw;
            map.SceneChanged += UpdateAnimation;
            map.CursorChanged += UpdateCursor;
        }
        UpdateSize();
        UpdateCursor();
        UpdateAnimation();
        Redraw();
    }

    private void UpdateSize()
    {
        if (Interaction is { } map) map.Size = new Size2(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
    }

    private void Redraw()
    {
        _map.Invalidate();
        _markers.Invalidate();
    }

    public void ResetView() => Interaction?.ResetView();

    /// <summary>
    /// Escape: drops a shape being dragged back where it was. False when no shape was being dragged,
    /// so the key can mean something else (leaving a drawing, erasing a zone).
    /// </summary>
    public bool CancelGesture()
    {
        if (Interaction?.CancelGesture() != true) return false;
        ReleasePointerCaptures();
        return true;
    }

    /// <summary>
    /// Lets go of Win2D's graphics resources for good. For a view that will not be shown again —
    /// the map manager once closed — since a control merely taken off screen may well come back.
    /// </summary>
    public void Release()
    {
        Interaction = null;
        StopAnimation();
        _map.RemoveFromVisualTree();
        _markers.RemoveFromVisualTree();
    }

    private void UpdateCursor() => ProtectedCursor = Interaction?.Cursor switch
    {
        MapCursor.Cross => InputSystemCursor.Create(InputSystemCursorShape.Cross),
        MapCursor.Move => InputSystemCursor.Create(InputSystemCursorShape.SizeAll),
        MapCursor.ResizeNorthwestSoutheast => InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast),
        MapCursor.ResizeNortheastSouthwest => InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest),
        _ => null,
    };

    // ---- Drawing --------------------------------------------------------------------------

    private Size2 CanvasSize => new(Math.Max(1, _map.ActualWidth), Math.Max(1, _map.ActualHeight));

    private void DrawMap(Microsoft.Graphics.Canvas.CanvasDrawingSession ds)
    {
        if (Interaction is not { } map) return;
        var palette = Palette;
        MapRenderer.Render(ds, map.Scene, CanvasSize, map.Transform, palette);
        MapRenderer.DrawOverlay(ds, map.Overlay, map.WorldToScreen, CanvasSize, palette);
    }

    private readonly RobotGlide _glide = new();
    private bool _animating;
    private TimeSpan _lastFrame;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private void DrawMarkers(Microsoft.Graphics.Canvas.CanvasDrawingSession ds)
    {
        if (Interaction is not { } map) return;
        var scene = map.Scene;
        // Windows' "animation effects" setting off: everything stands still.
        var animate = Settings.AnimationsEnabled;
        var seconds = animate ? Clock.Elapsed.TotalSeconds : 0;
        var robotAt = animate && scene.SmoothRobotMotion && !scene.RobotDocked && scene.Robot is { } robot
            ? _glide.At(robot, Clock.Elapsed.TotalSeconds)
            : null;
        RobotMarkers.Draw(ds, scene, map.WorldToScreen, seconds, robotAt);
        // A new position starts a glide, and one that has arrived stops the frames.
        if (robotAt is not null) DispatcherQueue.TryEnqueue(UpdateAnimation);
    }

    /// <summary>Keeps redrawing the markers while something moves and the map can be seen, and only then.</summary>
    private void UpdateAnimation()
    {
        var wanted = IsLoaded && Interaction?.Scene is { } scene && Settings.AnimationsEnabled
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

    private void OnFrame(object? sender, object e)
    {
        // Rendering fires at the screen's rate; thirty frames a second is plenty for these.
        var now = e is RenderingEventArgs r ? r.RenderingTime : Clock.Elapsed;
        if (now - _lastFrame < FrameInterval) return;
        _lastFrame = now;
        _markers.Invalidate();
    }

    // ---- Mouse and pen ----------------------------------------------------------------------

    private static bool IsTouch(PointerRoutedEventArgs e) => e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

    private Vec2 Position(PointerRoutedEventArgs e) => e.GetCurrentPoint(this).Position.ToVec2();

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Interaction?.PointerWheel(Position(e), e.GetCurrentPoint(this).Properties.MouseWheelDelta);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsTouch(e) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Interaction?.PointerPressed(Position(e));
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsTouch(e)) return;
        Interaction?.PointerMoved(Position(e), e.GetCurrentPoint(this).Properties.IsLeftButtonPressed);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (IsTouch(e)) return;
        // There is no click count to read here: the interaction tells a double click by its timing.
        Interaction?.PointerReleased(Position(e), clickCount: 1);
        // Released only now: letting go of the capture reports it lost, which would otherwise drop
        // a dragged shape unsent before the gesture had been read.
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!IsTouch(e)) Interaction?.PointerCaptureLost();
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        if (!IsTouch(e)) Interaction?.PointerExited();
    }

    // ---- Touch: a finger that moves pans or pinches, through the manipulation events, which only
    // begin once it has moved; one that does not is a tap, reported on its own. ----

    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);
        if (e.PointerDeviceType != PointerDeviceType.Touch) return;
        Interaction?.Tap(e.GetPosition(this).ToVec2());
        e.Handled = true;
    }

    /// <summary>The second tap of a double tap, which WinUI reports only here, not as a Tapped of its own.</summary>
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (e.PointerDeviceType != PointerDeviceType.Touch) return;
        Interaction?.DoubleTap(e.GetPosition(this).ToVec2());
        e.Handled = true;
    }

    protected override void OnManipulationStarted(ManipulationStartedRoutedEventArgs e)
    {
        base.OnManipulationStarted(e);
        if (e.PointerDeviceType != PointerDeviceType.Touch) return;
        Interaction?.ManipulationStarted(e.Position.ToVec2());
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        base.OnManipulationDelta(e);
        if (e.PointerDeviceType != PointerDeviceType.Touch) return;
        Interaction?.ManipulationDelta(e.Position.ToVec2(), e.Delta.Translation.ToVec2(), e.Delta.Scale, e.Cumulative.Translation.ToVec2());
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedRoutedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        if (e.PointerDeviceType != PointerDeviceType.Touch) return;
        Interaction?.ManipulationCompleted(e.Position.ToVec2(), e.Cumulative.Translation.ToVec2(), e.Cumulative.Scale);
        e.Handled = true;
    }
}
