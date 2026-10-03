using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dyss.Presentation.Map;

namespace Dyss.App.Rendering;

/// <summary>
/// The application's icon: the robot seen from above, heading down, its green light on the floor
/// ahead, drawn by the same code as on the map. Windows icons are bitmaps, one per size, so this
/// renders each size from the drawing and packs them into an .ico file. The file in the project is
/// made with <c>DyssCockpit.exe --export-icon app.ico</c>; rerun it after changing the drawing.
/// </summary>
public static class AppIcon
{
    /// <summary>The sizes Windows picks from: title bars and lists at 100 to 200 % scaling, the taskbar, Explorer's large views.</summary>
    public static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>The square the icon is framed in, in the robot's units: its body (radius 16) and the light below it.</summary>
    private const double Left = -25, Top = -17, Side = 50;

    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var k = size / Side;
            // Turned half round: the robot's front, at -y in its drawing, faces down.
            var frame = new Matrix(-k, 0, 0, -k, -Left * k, -Top * k);
            RobotMarkers.DrawIcon(dc, frame);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Every size rendered as a PNG and packed into an .ico (see <see cref="IcoFile"/>).</summary>
    public static void WriteIco(Stream output, IReadOnlyList<int> sizes)
    {
        var images = sizes.Select(s =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(s)));
            using var png = new MemoryStream();
            encoder.Save(png);
            return (s, png.ToArray());
        }).ToList();
        IcoFile.Write(output, images);
    }
}
