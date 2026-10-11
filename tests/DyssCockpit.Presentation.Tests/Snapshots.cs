using System.IO.Compression;

namespace DyssCockpit.Presentation.Tests;

/// <summary>A clean's snapshot as the cloud serves it (zlib, protobuf): only the fields the decoder reads.</summary>
internal static class Snapshots
{
    private static void Varint(List<byte> to, ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            to.Add(value == 0 ? b : (byte)(b | 0x80));
        } while (value != 0);
    }

    private static void Bytes(List<byte> to, int field, List<byte> value)
    {
        Varint(to, (ulong)((field << 3) | 2));
        Varint(to, (ulong)value.Count);
        to.AddRange(value);
    }

    /// <summary>A map of <paramref name="width"/> × <paramref name="height"/> cells of 50 cm at the origin, every cell set to <paramref name="cell"/>.</summary>
    public static byte[] Of(long mapId, int width, int height, byte cell)
    {
        var map = new List<byte>();
        foreach (var (field, value) in new[] { (1, (ulong)mapId), (2, (ulong)width), (3, (ulong)height) })
        {
            Varint(map, (ulong)(field << 3));
            Varint(map, value);
        }
        foreach (var (field, value) in new[] { (4, 0f), (5, 0f), (8, 0.5f) })
        {
            Varint(map, (ulong)((field << 3) | 5));
            map.AddRange(BitConverter.GetBytes(value));
        }
        var grid = new List<byte>();
        Bytes(grid, 1, [.. Enumerable.Repeat(cell, width * height)]);
        var message = new List<byte>();
        Bytes(message, 3, map);
        Bytes(message, 4, grid);
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(message.ToArray());
        return packed.ToArray();
    }
}