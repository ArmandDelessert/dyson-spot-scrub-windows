using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DyssCockpit.App.Services;

/// <summary>
/// "Start with Windows", with no administrator rights: through the current user's Run key for the unzipped application,
/// and through the startup task of the manifest for the MSIX package, which a Run key cannot do (the registry of a package
/// is its own). The user can also turn the task off in Settings › Apps › Startup and in the Task Manager, and then only
/// they can turn it back on: <see cref="IsEnabled"/> tells.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DySS Cockpit";

    /// <summary>The TaskId of the startup task in Package.appxmanifest.</summary>
    private const string TaskId = "DyssCockpitStartup";

    private static string Command => $"\"{Environment.ProcessPath}\" {Program.MinimizedArgument}";

    public static bool IsEnabled => PackageIdentity.IsPackaged ? TaskIsEnabled() : RunKeyIsEnabled();

    public static void SetEnabled(bool enabled)
    {
        if (PackageIdentity.IsPackaged)
        {
            SetTask(enabled);
        }
        else
        {
            SetRunKey(enabled);
        }
    }

    private static bool RunKeyIsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value
            && value.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    // The task's calls are asynchronous; the settings page asks synchronously, from the UI thread: they go through the thread pool.
    private static bool TaskIsEnabled() =>
        Task.Run(async () => (await StartupTask.GetAsync(TaskId).AsTask()).State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            .GetAwaiter().GetResult();

    private static void SetTask(bool enabled) =>
        Task.Run(async () =>
        {
            var task = await StartupTask.GetAsync(TaskId).AsTask();
            if (enabled)
            {
                // Stays disabled when the user turned it off in Windows' settings: only they can turn it back on.
                await task.RequestEnableAsync().AsTask();
            }
            else
            {
                task.Disable();
            }
        }).GetAwaiter().GetResult();
}
