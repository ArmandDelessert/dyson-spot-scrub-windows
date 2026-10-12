using System.IO.Compression;

namespace DyssCockpit.TestSupport;

/// <summary>
/// A tiny protobuf writer, enough to lay out a message like the cloud's snapshot of a clean, and the
/// zlib it comes packed in. Each field writer returns the list it wrote to, so that nested messages
/// read as one expression.
/// </summary>
internal static class ProtoWriter
{
    public const int WireVarint = 0, WireFixed64 = 1, WireBytes = 2, WireFixed32 = 5;

    public static void Varint(List<byte> to, ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            to.Add(value == 0 ? b : (byte)(b | 0x80));
        } while (value != 0);
    }

    public static void Key(List<byte> to, int field, int wire) => Varint(to, (ulong)((field << 3) | wire));

    public static List<byte> VarintField(List<byte> to, int field, ulong value)
    {
        Key(to, field, WireVarint);
        Varint(to, value);
        return to;
    }

    public static List<byte> FloatField(List<byte> to, int field, float value)
    {
        Key(to, field, WireFixed32);
        to.AddRange(BitConverter.GetBytes(value));
        return to;
    }

    public static List<byte> BytesField(List<byte> to, int field, IReadOnlyCollection<byte> value)
    {
        Key(to, field, WireBytes);
        Varint(to, (ulong)value.Count);
        to.AddRange(value);
        return to;
    }

    public static byte[] Zlib(List<byte> message)
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(message.ToArray());
        return packed.ToArray();
    }
}
