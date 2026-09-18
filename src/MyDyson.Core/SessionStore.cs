using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyDyson.Core;

/// <summary>
/// Persists the bearer token in %APPDATA%\MyDyson\session.bin, encrypted with Windows DPAPI
/// (current user scope), so the one-time-code login does not have to be repeated at every start.
/// </summary>
public static class SessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyDyson.SessionStore.v1");

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyDyson");

    public static string FilePath => Path.Combine(Directory, "session.bin");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(StoredSession session)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        var cipher = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
            : plain; // non-Windows: stored in clear (prototype only)
        File.WriteAllBytes(FilePath, cipher);
    }

    public static StoredSession? Load()
    {
        if (!Exists) return null;
        var cipher = File.ReadAllBytes(FilePath);
        var plain = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser)
            : cipher;
        return JsonSerializer.Deserialize<StoredSession>(plain);
    }

    public static void Delete()
    {
        if (Exists) File.Delete(FilePath);
    }
}
