using System.IO.Compression;

namespace DyssCockpit.Core;

/// <summary>
/// What the cloud keeps of one clean behind the <c>downloadUrl</c> of its line in the history: a
/// zlib-compressed protobuf message (schema unknown, decoded by hand, see docs/protocole.md) that
/// holds, among other things, the occupancy grid as it was at the end of that clean. The robot
/// updates its map at every pass — a moved table, a door left open — so this is the map the clean
/// really ran on, where the current one may have changed since, or be gone. Only what is understood
/// is read: the times, the map's identity and frame, and the grid.
/// </summary>
/// <param name="MapId">The persistent map the clean ran on.</param>
public sealed record CleanSnapshot(long? MapId, MapGrid Grid, DateTimeOffset? Start, DateTimeOffset? End)
{
    /// <summary>The snapshot in a blob as downloaded; null when it is not one this decoder understands.</summary>
    public static CleanSnapshot? Parse(byte[] blob)
    {
        try
        {
            using var input = new ZLibStream(new MemoryStream(blob), CompressionMode.Decompress);
            using var message = new MemoryStream();
            input.CopyTo(message);
            return Decode(message.ToArray());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The fields of the decompressed message, numbered as observed: 2 the times, 3 the map, 4 the grid.</summary>
    internal static CleanSnapshot? Decode(byte[] message)
    {
        DateTimeOffset? start = null, end = null;
        long? mapId = null;
        int width = 0, height = 0;
        double offsetX = 0, offsetY = 0, resolution = 0;
        byte[]? cells = null;

        var reader = new ProtoReader(message);
        while (reader.Next(out var field, out var wire))
        {
            switch (field, wire)
            {
                case (2, ProtoReader.WireBytes):
                    var times = new ProtoReader(reader.Bytes());
                    while (times.Next(out var f, out var w))
                    {
                        if (f == 1 && w == ProtoReader.WireVarint) start = DateTimeOffset.FromUnixTimeSeconds((long)times.Varint());
                        else if (f == 2 && w == ProtoReader.WireVarint) end = DateTimeOffset.FromUnixTimeSeconds((long)times.Varint());
                        else times.Skip(w);
                    }
                    break;
                case (3, ProtoReader.WireBytes):
                    var map = new ProtoReader(reader.Bytes());
                    while (map.Next(out var f, out var w))
                    {
                        switch (f, w)
                        {
                            case (1, ProtoReader.WireVarint): mapId = (long)map.Varint(); break;
                            case (2, ProtoReader.WireVarint): width = (int)map.Varint(); break;
                            case (3, ProtoReader.WireVarint): height = (int)map.Varint(); break;
                            case (4, ProtoReader.WireFixed32): offsetX = map.Float(); break;
                            case (5, ProtoReader.WireFixed32): offsetY = map.Float(); break;
                            case (8, ProtoReader.WireFixed32): resolution = map.Float(); break;
                            default: map.Skip(w); break;
                        }
                    }
                    break;
                case (4, ProtoReader.WireBytes):
                    var grid = new ProtoReader(reader.Bytes());
                    while (grid.Next(out var f, out var w))
                    {
                        if (f == 1 && w == ProtoReader.WireBytes) cells = grid.Bytes();
                        else grid.Skip(w);
                    }
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        // A grid that does not fill its frame exactly is not one this decoder understands.
        if (cells is null || width <= 0 || height <= 0 || resolution <= 0 || cells.Length != width * height) return null;
        // Sent as 32-bit floats: 0.05 arrives as 0.0500000007, and the offsets the same way.
        var dimensions = new MapDimensions(width, height, Math.Round(resolution, 6), Math.Round(offsetX, 6), Math.Round(offsetY, 6));
        return new CleanSnapshot(mapId, new MapGrid(dimensions, [.. cells.Select(b => (int)b)]), start, end);
    }
}

/// <summary>Just enough of the protobuf wire format to walk a message whose schema is not known.</summary>
internal sealed class ProtoReader(byte[] data)
{
    public const int WireVarint = 0, WireFixed64 = 1, WireBytes = 2, WireFixed32 = 5;

    private int _at;

    /// <summary>Reads the next field's number and wire type; false at the end of the message.</summary>
    public bool Next(out int field, out int wire)
    {
        field = wire = 0;
        if (_at >= data.Length) return false;
        var key = Varint();
        field = (int)(key >> 3);
        wire = (int)(key & 7);
        return true;
    }

    public ulong Varint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_at >= data.Length) throw new FormatException("Truncated varint.");
            var b = data[_at++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new FormatException("Varint too long.");
    }

    public double Float()
    {
        if (_at + 4 > data.Length) throw new FormatException("Truncated float.");
        var value = BitConverter.ToSingle(data, _at);
        _at += 4;
        return value;
    }

    public byte[] Bytes()
    {
        var length = (int)Varint();
        if (length < 0 || _at + length > data.Length) throw new FormatException("Truncated field.");
        var value = data[_at..(_at + length)];
        _at += length;
        return value;
    }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case WireVarint: Varint(); break;
            case WireFixed64: _at += 8; break;
            case WireBytes: _ = Bytes(); break;
            case WireFixed32: _at += 4; break;
            default: throw new FormatException($"Unknown wire type {wire}.");
        }
        if (_at > data.Length) throw new FormatException("Truncated field.");
    }
}
