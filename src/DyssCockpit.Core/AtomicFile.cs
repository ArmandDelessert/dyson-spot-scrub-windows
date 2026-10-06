using System.Text;

namespace DyssCockpit.Core;

/// <summary>
/// Writes a file so that it is either the previous one or the new one, never part of each: the
/// bytes go to a temporary file beside it, which then takes its place in a single move. A crash,
/// a power cut or a full disk while writing leaves the previous file whole.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var full = Path.GetFullPath(path);
        // Beside the target, so the move stays on one volume and is a rename.
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                // On disk before the move: otherwise the rename can outlive a power cut that the bytes did not.
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left behind, harmless: the next write uses another name.
            }
        }
    }
}
