using System.Globalization;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// Files written one a day and named after it, <c>prefix + 2026-10-05 + extension</c>: the logs and
/// the message records. Their age is read from their name, not from the date the file system gives,
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
        if (days <= 0 || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, $"{prefix}*{extension}"))
            {
                var name = Path.GetFileName(file);
                var stamp = name[prefix.Length..^extension.Length];
                if (!DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    || day >= today.AddDays(-days))
                    continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // open elsewhere: next time
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start, or the next day.
        }
    }
}
