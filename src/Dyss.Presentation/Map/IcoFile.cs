namespace Dyss.Presentation.Map;

/// <summary>The .ico container, which any UI can fill with its own renderings of the application's icon.</summary>
public static class IcoFile
{
    /// <summary>
    /// An .ico holding every size as a PNG, the format Windows has read since Vista. The header
    /// gives each image's size (0 meaning 256), its colour depth, and where its bytes start.
    /// </summary>
    public static void Write(Stream output, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // an icon, not a cursor
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, bytes) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);            // no palette
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // colour planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(bytes.Length);
            w.Write(offset);
            offset += bytes.Length;
        }
        foreach (var (_, bytes) in images) w.Write(bytes);
    }
}
