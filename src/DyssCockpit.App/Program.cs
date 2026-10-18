using System.Globalization;
using DyssCockpit.Presentation.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DyssCockpit.App;

/// <summary>
/// The entry point, in place of the one XAML generates: it keeps the application to one instance
/// before any window exists. A second launch, from the Start menu or by clicking a notification,
/// hands its activation to the running instance, which comes to the front, and ends there — two
/// instances would hold two connections to the robot and write the same files.
/// </summary>
public static class Program
{
    private const string InstanceKey = "DyssCockpit.Main";

    /// <summary>Starting with Windows: straight to the notification area, the window left closed.</summary>
    public const string MinimizedArgument = LaunchOptions.MinimizedArgument;

    /// <summary>"--wait-for-exit=PID", from a restart: the old instance must be gone, with its key and its icon, before this one starts.</summary>
    public const string WaitForExitArgument = "--wait-for-exit";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WaitForPreviousInstance(args);
        var options = LaunchOptions.Parse(args);
        // Before any text is loaded: the resources are picked in the language chosen in the settings, or given by --lang.
        ApplyLanguage(options.Language is { } flag ? AppSettings.NormalizeLanguage(flag) : AppSettings.LoadReadOnly().Language);

        if (!options.IsOneShot)
        {
            var main = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (!main.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                // Off the STA thread: redirecting waits on the other instance, which must not block this one's COM.
                Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
                return 0;
            }
        }

        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>Makes Windows pick the resources of the language chosen in the settings (empty = follow Windows). Must run before any text is loaded.</summary>
    private static void ApplyLanguage(string language)
    {
        if (language.Length == 0) return;
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Not a language Windows knows: the one of Windows stays.
        }
    }

    private static void WaitForPreviousInstance(string[] args)
    {
        var prefix = WaitForExitArgument + "=";
        var argument = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        if (argument is null || !int.TryParse(argument[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return;
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }
}
