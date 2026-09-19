using System.Windows;
using System.Windows.Media;
using MyDyson.App.Rendering;

namespace MyDyson.App.Controls;

/// <summary>Draws a <see cref="MapScene"/>, refitting whenever the scene or the size changes.</summary>
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

    /// <summary>Last world-to-screen transform, so a click can be mapped back to metres.</summary>
    public Matrix WorldToScreen { get; private set; } = Matrix.Identity;

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        MapRenderer.Render(dc, Scene ?? new MapScene(), size, out var m);
        WorldToScreen = m;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
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
