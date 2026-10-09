namespace DyssCockpit.Presentation.Services;

/// <summary>
/// What is where under %LOCALAPPDATA%\DySS Cockpit, laid out like HusqA Cockpit's: the session and
/// the preferences at the top, the application's log in <c>Logs</c>, the record of the robot's
/// messages in <c>Messages</c>. Every name is English, whatever the language of the interface.
/// </summary>
public static class AppFolders
{
    public static string Logs(string root) => Path.Combine(root, "Logs");

    public static string Messages(string root) => Path.Combine(root, "Messages");

    /// <summary>How many log files are more than <paramref name="days"/> days old as of <paramref name="today"/>, which a retention of that many days would delete.</summary>
    internal static int LogsOlderThan(string root, int days, DateOnly today) =>
        DatedFiles.CountOlderThan(Logs(root), FileLoggerProvider.DefaultPrefix, FileLoggerProvider.Extension, today, days);

    /// <summary>The same for the records of the robot's messages.</summary>
    internal static int MessagesOlderThan(string root, int days, DateOnly today) =>
        DatedFiles.CountOlderThan(Messages(root), MessageLog.Prefix, MessageLog.Extension, today, days);

    /// <summary>
    /// Brings what an earlier version left at the top of the folder to its place: the log files
    /// (<c>journal-2026-10-05.log</c>, now <c>Logs\dyss-cockpit-2026-10-05.log</c>), the folder
    /// <c>messages</c> (now <c>Messages</c>), and <c>erreurs.log</c>, which nothing has written since
    /// the log went through <c>ILogger</c>: dropped when empty, kept as <c>Logs\errors-legacy.log</c> when
    /// it holds something. Done at each start, and harmless once there is nothing left to move; what
    /// cannot be moved now (a file open elsewhere) is left for the next start.
    /// </summary>
    public static void MigrateLegacyLayout(string root)
    {
        if (!Directory.Exists(root)) return;
        var logs = Logs(root);

        const string oldPrefix = "journal-";
        foreach (var file in Directory.EnumerateFiles(root, oldPrefix + "*.log").ToList())
        {
            var target = Path.Combine(logs, FileLoggerProvider.DefaultPrefix + Path.GetFileName(file)[oldPrefix.Length..]);
            Attempt(() =>
            {
                Directory.CreateDirectory(logs);
                // The new version may already have written today's: the old lines go on to it.
                if (File.Exists(target))
                {
                    File.AppendAllText(target, File.ReadAllText(file));
                    File.Delete(file);
                }
                else File.Move(file, target);
            });
        }

        var errors = Path.Combine(root, "erreurs.log");
        if (File.Exists(errors))
        {
            Attempt(() =>
            {
                if (string.IsNullOrWhiteSpace(File.ReadAllText(errors))) File.Delete(errors);
                else
                {
                    Directory.CreateDirectory(logs);
                    File.Move(errors, Path.Combine(logs, "errors-legacy.log"));
                }
            });
        }

        // Windows ignores case in a name, so the folder is found under either spelling: only the
        // spelling it was created with is wrong, and a rename that changes nothing else needs two steps.
        var legacy = Directory.EnumerateDirectories(root).FirstOrDefault(d => Path.GetFileName(d) == "messages");
        if (legacy is not null && !Directory.EnumerateDirectories(root).Any(d => Path.GetFileName(d) == "Messages"))
        {
            Attempt(() =>
            {
                var moving = Path.Combine(root, "messages.moving");
                Directory.Move(legacy, moving);
                Directory.Move(moving, Messages(root));
            });
        }
    }

    private static void Attempt(Action move)
    {
        try { move(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
