namespace DyssCockpit.Presentation.Services;

/// <summary>
/// What is where under %LocalAppData%\DySS Cockpit, laid out like HusqA Cockpit's: the session and
/// the preferences at the top, the application's log in <c>Logs</c>, the record of the robot's
/// messages in <c>Messages</c>, the cleans kept from the history in <c>Cleans</c>. Every name is
/// English, whatever the language of the interface.
/// </summary>
public static class AppFolders
{
    public static string Logs(string root) => Path.Combine(root, "Logs");

    public static string Messages(string root) => Path.Combine(root, "Messages");

    public static string Cleans(string root) => Path.Combine(root, "Cleans");

    /// <summary>How many log files are more than <paramref name="days"/> days old as of <paramref name="today"/>, which a retention of that many days would delete.</summary>
    internal static int LogsOlderThan(string root, int days, DateOnly today) =>
        DatedFiles.CountOlderThan(Logs(root), FileLoggerProvider.DefaultPrefix, FileLoggerProvider.Extension, today, days);

    /// <summary>The same for the records of the robot's messages.</summary>
    internal static int MessagesOlderThan(string root, int days, DateOnly today) =>
        DatedFiles.CountOlderThan(Messages(root), MessageLog.Prefix, MessageLog.Extension, today, days);

    /// <summary>The same for the cleans kept from the history.</summary>
    internal static int CleansOlderThan(string root, int days, DateOnly today) =>
        DatedFiles.CountOlderThan(Cleans(root), CleanArchive.Prefix, CleanArchive.Extension, today, days);
}
