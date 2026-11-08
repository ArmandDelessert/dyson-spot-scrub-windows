using System.Runtime.InteropServices;

namespace DyssCockpit.App.Services;

/// <summary>
/// Whether the application runs from its MSIX package (it then has a package identity) or from an unzipped folder.
/// A few things work differently in a package: "start with Windows" is a startup task of the manifest, not a Run key,
/// and the notifications take their name and icon from the manifest.
/// </summary>
internal static class PackageIdentity
{
    /// <summary>What GetCurrentPackageFullName answers when the process has no package.</summary>
    private const int NoPackage = 15700;

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackage;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);
}
