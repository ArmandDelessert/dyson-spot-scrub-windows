using System.IO;
using MyDyson.Core;

namespace MyDyson.App.Services;

/// <summary>
/// Unexpected errors, appended to %APPDATA%\MyDyson\erreurs.log with their stack trace, so a
/// problem seen once — a message box, or a crash with no window left to show it — can still be
/// looked into afterwards. Kept to about a megabyte: past that the file starts over.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SessionStore.Directory, "erreurs.log");

    /// <summary>Never throws: the log is where failures go, it cannot become one.</summary>
    public static void Write(string context, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SessionStore.Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{context}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report it.
        }
    }
}
