using System.Numerics;
using Dyss.Presentation.Map;
using Microsoft.Graphics.Canvas;

namespace Dyss.App.Rendering;

/// <summary>The map and the application's icon drawn off screen, to PNG files.</summary>
internal static class MapImage
{
    /// <summary>Renders the scene, robot and dock included, to a PNG file: for sharing, or for checking the renderer without a window.</summary>
    public static async Task ExportPngAsync(MapScene scene, int width, int height, string path, MapPalette palette)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            var size = new Size2(width, height);
            var m = MapGeometry.WorldToScreen(scene, size);
            MapRenderer.Render(ds, scene, size, m, palette);
            RobotMarkers.Draw(ds, scene, m ?? MapTransform.Identity, 0);
        }
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }
}

/// <summary>
/// The application's icon: the robot seen from above, heading down, its green light on the floor
/// ahead, drawn by the same code as on the map. Windows icons are bitmaps, one per size, so this
/// renders each size from the drawing and packs them into an .ico file (see <see cref="IcoFile"/>).
/// The file in the project is made with <c>DyssCockpit.exe --export-icon app.ico</c>; rerun it after
/// changing the drawing.
/// </summary>
internal static class AppIcon
{
    /// <summary>The sizes Windows picks from: title bars and lists at 100 to 200 % scaling, the taskbar, Explorer's large views.</summary>
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>The square the icon is framed in, in the robot's units: its body (radius 16) and the light below it.</summary>
    private const float Left = -25, Top = -17, Side = 50;

    public static async Task WriteIcoAsync(Stream output, IReadOnlyList<int> sizes)
    {
        var images = new List<(int, byte[])>();
        foreach (var size in sizes) images.Add((size, await RenderPngAsync(size)));
        IcoFile.Write(output, images);
    }

    private static async Task<byte[]> RenderPngAsync(int size)
    {
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            var k = size / Side;
            // Turned half round: the robot's front, at -y in its drawing, faces down.
            RobotMarkers.DrawIcon(ds, new Matrix3x2(-k, 0, 0, -k, -Left * k, -Top * k));
        }
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        using var copy = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(copy);
        return copy.ToArray();
    }
}
