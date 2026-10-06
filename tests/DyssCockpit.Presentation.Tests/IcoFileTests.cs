using DyssCockpit.Presentation.Map;

namespace DyssCockpit.Presentation.Tests;

public class IcoFileTests
{
    /// <summary>A stand-in for a rendered size: the PNG signature, then some bytes.</summary>
    private static byte[] Png(int length) => [0x89, (byte)'P', (byte)'N', (byte)'G', .. new byte[length]];

    [Fact]
    public void TheIcoHoldsEverySizeAsAPng()
    {
        using var ico = new MemoryStream();
        IcoFile.Write(ico, [(16, Png(10)), (256, Png(30))]);
        var bytes = ico.ToArray();
        using var r = new BinaryReader(new MemoryStream(bytes));

        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal(1, r.ReadUInt16());     // an icon
        Assert.Equal(2, r.ReadUInt16());
        foreach (var (expected, size) in new[] { (16, 14), (0, 34) })   // 256 is written as 0
        {
            Assert.Equal(expected, r.ReadByte());
            Assert.Equal(expected, r.ReadByte());
            r.ReadBytes(2);
            Assert.Equal(1, r.ReadUInt16());
            Assert.Equal(32, r.ReadUInt16());
            var length = r.ReadInt32();
            var offset = r.ReadInt32();
            Assert.Equal(size, length);
            // Each image is a PNG, starting where the header says.
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[offset..(offset + 4)]);
            Assert.True(offset + length <= bytes.Length);
        }
    }
}
