using static DyssCockpit.TestSupport.ProtoWriter;

namespace DyssCockpit.Presentation.Tests;

/// <summary>A clean's snapshot as the cloud serves it (zlib, protobuf): only the fields the decoder reads.</summary>
internal static class Snapshots
{
    /// <summary>A map of <paramref name="width"/> × <paramref name="height"/> cells of 50 cm at the origin, every cell set to <paramref name="cell"/>.</summary>
    public static byte[] Of(long mapId, int width, int height, byte cell)
    {
        var map = VarintField(VarintField(VarintField([], 1, (ulong)mapId), 2, (ulong)width), 3, (ulong)height);
        FloatField(FloatField(FloatField(map, 4, 0f), 5, 0f), 8, 0.5f);
        var message = BytesField([], 3, map);
        BytesField(message, 4, BytesField([], 1, [.. Enumerable.Repeat(cell, width * height)]));
        return Zlib(message);
    }
}
