using System.IO;
using MyDyson.App.Rendering;

namespace MyDyson.App.Tests;

public class AppIconTests
{
    [Fact]
    public void TheIcoHoldsEverySizeAsAPng()
    {
        using var ico = new MemoryStream();
        AppIcon.WriteIco(ico, [16, 256]);
        var bytes = ico.ToArray();
        using var r = new BinaryReader(new MemoryStream(bytes));

        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal(1, r.ReadUInt16());     // an icon
        Assert.Equal(2, r.ReadUInt16());
        foreach (var expected in new[] { 16, 0 })   // 256 is written as 0
        {
            Assert.Equal(expected, r.ReadByte());
            Assert.Equal(expected, r.ReadByte());
            r.ReadBytes(2);
            Assert.Equal(1, r.ReadUInt16());
            Assert.Equal(32, r.ReadUInt16());
            var length = r.ReadInt32();
            var offset = r.ReadInt32();
            // Each image is a PNG, starting where the header says.
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[offset..(offset + 4)]);
            Assert.True(offset + length <= bytes.Length);
        }
    }

    [Fact]
    public void TheIconIsDrawnAtTheSizeAsked()
    {
        var image = AppIcon.Render(48);
        Assert.Equal(48, image.PixelWidth);
        Assert.Equal(48, image.PixelHeight);
    }
}
