using Microsoft.Win32;

namespace DyssCockpit.App.Services;

/// <summary>
/// "Start with Windows", through the current user's Run key: no administrator rights needed. The
/// application then starts in the notification area (see <see cref="Program.MinimizedArgument"/>).
/// Taken from HusqA Cockpit.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DySS Cockpit";

    private static string Command => $"\"{Environment.ProcessPath}\" {Program.MinimizedArgument}";

    /// <summary>Only when the entry starts this very executable: a copy unzipped elsewhere is another one.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                && value.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
