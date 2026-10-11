using System.IO.Compression;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The cloud's snapshot of a clean, decoded from a message built here field by field as the real ones
/// are laid out (docs/protocole.md): what is understood is read, the rest is stepped over.
/// </summary>
public class CleanSnapshotTests
{
    // ---- A tiny protobuf writer, enough to lay out a message like the cloud's -------------------

    private static void Varint(List<byte> to, ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            to.Add(value == 0 ? b : (byte)(b | 0x80));
        } while (value != 0);
    }

    private static void Key(List<byte> to, int field, int wire) => Varint(to, (ulong)((field << 3) | wire));

    private static List<byte> VarintField(List<byte> to, int field, ulong value)
    {
        Key(to, field, 0);
        Varint(to, value);
        return to;
    }

    private static List<byte> FloatField(List<byte> to, int field, float value)
    {
        Key(to, field, 5);
        to.AddRange(BitConverter.GetBytes(value));
        return to;
    }

    private static List<byte> BytesField(List<byte> to, int field, IReadOnlyCollection<byte> value)
    {
        Key(to, field, 2);
        Varint(to, (ulong)value.Count);
        to.AddRange(value);
        return to;
    }

    private static byte[] Zlib(List<byte> message)
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(message.ToArray());
        return packed.ToArray();
    }

    /// <summary>A snapshot of a 4 × 3 map at 5 cm, offset (−5, −9), with fields this decoder does not know around the ones it does.</summary>
    private static List<byte> Message(byte[] cells, int width = 4, int height = 3)
    {
        var message = new List<byte>();
        VarintField(message, 1, 21);
        var times = VarintField(VarintField([], 1, 1791710567), 2, 1791712013);
        BytesField(times, 7, BytesField([], 1, "418a9ecac1adc867de1e38300365f268"u8.ToArray()));
        BytesField(message, 2, times);
        var map = VarintField([], 1, 1782494719);
        VarintField(map, 2, (ulong)width);
        VarintField(map, 3, (ulong)height);
        FloatField(map, 4, -5f);
        FloatField(map, 5, -9f);
        FloatField(map, 6, 11f);
        FloatField(map, 7, 13f);
        FloatField(map, 8, 0.05f);
        BytesField(message, 3, map);
        BytesField(message, 4, BytesField([], 1, cells));
        BytesField(message, 5, BytesField(VarintField([], 1, 1782494719), 2, "Appartement"u8.ToArray()));
        Key(message, 9, 1);
        message.AddRange(BitConverter.GetBytes(1.5));   // a fixed64, stepped over
        return message;
    }

    private static readonly byte[] Cells = [0, 0, 255, 0, 10, 10, 255, 11, 10, 10, 11, 11];

    [Fact]
    public void TheGridTheMapAndTheTimesAreReadFromTheBlob()
    {
        var snapshot = CleanSnapshot.Parse(Zlib(Message(Cells)))!;

        Assert.Equal(1782494719, snapshot.MapId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791710567), snapshot.Start);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791712013), snapshot.End);
        Assert.Equal(4, snapshot.Grid.Width);
        Assert.Equal(3, snapshot.Grid.Height);
        Assert.Equal(0.05, snapshot.Grid.Resolution);   // a 32-bit float on the wire, 0.0500000007
        Assert.Equal(-5, snapshot.Grid.OffsetX);
        Assert.Equal(-9, snapshot.Grid.OffsetY);
        Assert.Equal(Cells.Select(c => (int)c), Enumerable.Range(0, 12).Select(i => snapshot.Grid[i % 4, i / 4]));   // row by row, as mapData
    }

    [Fact]
    public void AGridThatDoesNotFillItsFrameIsNotTakenForOne()
    {
        Assert.Null(CleanSnapshot.Parse(Zlib(Message(Cells, width: 5))));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]   // not zlib
    public void WhatIsNotASnapshotIsNull(byte[] blob) => Assert.Null(CleanSnapshot.Parse(blob));

    [Fact]
    public void ATruncatedMessageIsNullNotAnException()
    {
        var message = Message(Cells);
        message.RemoveRange(message.Count - 20, 20);

        Assert.Null(CleanSnapshot.Parse(Zlib(message)));
    }

    [Fact]
    public void AMessageWithoutAGridIsNull()
    {
        var message = VarintField([], 1, 21);

        Assert.Null(CleanSnapshot.Parse(Zlib(message)));
    }
}
