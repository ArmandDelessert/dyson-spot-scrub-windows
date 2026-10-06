using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace DyssCockpit.App.Services;

/// <summary>Renders part of the interface to a PNG, for documentation and for checking a layout without anyone at the screen.</summary>
internal static class Screenshot
{
    /// <summary>
    /// Draws <paramref name="element"/> as it is on screen, over the theme's plain background: the
    /// window's Mica backdrop is the system's doing, out of reach of the rendering.
    /// </summary>
    public static async Task SaveAsync(UIElement element, string path, ElementTheme theme)
    {
        var rendered = new RenderTargetBitmap();
        await rendered.RenderAsync(element);
        var pixels = (await rendered.GetPixelsAsync()).ToArray();
        var device = CanvasDevice.GetSharedDevice();
        using var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, rendered.PixelWidth, rendered.PixelHeight, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, rendered.PixelWidth, rendered.PixelHeight, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(theme == ElementTheme.Light ? Color.FromArgb(255, 0xf3, 0xf3, 0xf3) : Color.FromArgb(255, 0x20, 0x20, 0x20));
            ds.DrawImage(bitmap);
        }
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }
}
