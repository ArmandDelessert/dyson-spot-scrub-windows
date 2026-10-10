using System.Globalization;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// Files named after the day they belong to, <c>prefix + 2026-10-05 + extension</c> (or, for the cleans
/// kept from the history, with the clean's id after the day): the logs, the message records, the cleans. Their age is read from their name, not from the date the file system gives,
/// which a copy or a restore changes.
/// </summary>
internal static class DatedFiles
{
    /// <summary>
    /// Deletes the files of that name more than <paramref name="days"/> days older than
    /// <paramref name="today"/> (so with 7, a week ago is kept and eight days ago is not). A number
    /// of days of zero or less keeps everything. Files of any other name are left alone.
    /// </summary>
    public static void DeleteOlderThan(string folder, string prefix, string extension, DateOnly today, int days)
    {
        try
        {
            foreach (var file in OlderThan(folder, prefix, extension, today, days))
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // open elsewhere: next time
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start, or the next day.
        }
    }

    /// <summary>How many files <see cref="DeleteOlderThan"/> would delete: what the user is told before it is done.</summary>
    public static int CountOlderThan(string folder, string prefix, string extension, DateOnly today, int days)
    {
        try { return OlderThan(folder, prefix, extension, today, days).Count; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static List<string> OlderThan(string folder, string prefix, string extension, DateOnly today, int days)
    {
        if (days <= 0 || !Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, $"{prefix}*{extension}").Where(file =>
        {
            // The day, then possibly more after a dash (the clean's id): logs and messages have nothing after it.
            var rest = Path.GetFileName(file)[prefix.Length..^extension.Length];
            return rest.Length >= 10 && (rest.Length == 10 || rest[10] == '-')
                && DateOnly.TryParseExact(rest[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < today.AddDays(-days);
        }).ToList();
    }
}
