using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DyssCockpit.Core;

/// <summary>
/// Persists the bearer token in %APPDATA%\DySS Cockpit\session.bin, encrypted with Windows DPAPI
/// (current user scope), so the one-time-code login does not have to be repeated at every start.
/// </summary>
public static class SessionStore
{
    // Part of the key DPAPI encrypts with: it is not a project name, and must stay as it is, or the
    // sessions already saved can no longer be read and everyone has to log in again.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dyss.SessionStore.v1");

    /// <summary>The folder under %APPDATA% that holds the session, the preferences and the logs.</summary>
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DySS Cockpit");

    public static string FilePath => Path.Combine(Directory, "session.bin");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(StoredSession session)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        var cipher = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
            : plain; // non-Windows: stored in clear (prototype only)
        // A torn write would lose the session and force a new one-time-code login.
        AtomicFile.WriteAllBytes(FilePath, cipher);
    }

    /// <summary>
    /// The stored session, or null when there is none — or when it cannot be read back: a
    /// truncated file, or one encrypted under another Windows account or machine, which DPAPI
    /// refuses. Either way the answer is the same, logging in again, not a start-up that fails.
    /// </summary>
    public static StoredSession? Load()
    {
        if (!Exists) return null;
        try
        {
            var cipher = File.ReadAllBytes(FilePath);
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser)
                : cipher;
            return JsonSerializer.Deserialize<StoredSession>(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public static void Delete()
    {
        if (Exists) File.Delete(FilePath);
    }
}
