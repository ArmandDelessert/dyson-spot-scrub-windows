using DyssCockpit.Presentation.Services;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The data folder's layout: what an earlier version wrote at the top of it goes where the new one looks.</summary>
public sealed class AppFoldersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dyss-folders-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string[] NamesIn(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Fact]
    public void TheLogsAndTheMessagesHaveEnglishFoldersOfTheirOwn()
    {
        Assert.Equal(Path.Combine(_root, "Logs"), AppFolders.Logs(_root));
        Assert.Equal(Path.Combine(_root, "Messages"), AppFolders.Messages(_root));
    }

    [Fact]
    public void TheOldLogsMoveToTheLogsFolderUnderTheApplicationsName()
    {
        Write("journal-2026-10-05.log", "one");
        Write("journal-2026-10-06.log", "two");
        Write("session.bin", "secret");
        Write("settings.json", "{}");

        AppFolders.MigrateLegacyLayout(_root);

        Assert.Equal(["dyss-cockpit-2026-10-05.log", "dyss-cockpit-2026-10-06.log"], NamesIn(AppFolders.Logs(_root)));
        Assert.Equal("one", File.ReadAllText(Path.Combine(AppFolders.Logs(_root), "dyss-cockpit-2026-10-05.log")));
        Assert.Equal(["Logs", "session.bin", "settings.json"], NamesIn(_root));
    }

    [Fact]
    public void AnOldLogOfADayTheNewVersionHasAlreadyWrittenJoinsIt()
    {
        Write("journal-2026-10-09.log", "old lines\n");
        Write(@"Logs\dyss-cockpit-2026-10-09.log", "new lines\n");

        AppFolders.MigrateLegacyLayout(_root);

        Assert.Equal("new lines\nold lines\n", File.ReadAllText(Path.Combine(AppFolders.Logs(_root), "dyss-cockpit-2026-10-09.log")));
        Assert.False(File.Exists(Path.Combine(_root, "journal-2026-10-09.log")));
    }

    [Fact]
    public void TheEmptyErrorFileOfTheFirstVersionsIsDroppedAndOneWithContentIsKept()
    {
        var errors = Write("erreurs.log", "\n");
        AppFolders.MigrateLegacyLayout(_root);
        Assert.False(File.Exists(errors));
        Assert.False(Directory.Exists(AppFolders.Logs(_root)));   // nothing to put there

        Write("erreurs.log", "boom\n");
        AppFolders.MigrateLegacyLayout(_root);
        Assert.False(File.Exists(errors));
        Assert.Equal("boom\n", File.ReadAllText(Path.Combine(AppFolders.Logs(_root), "errors-legacy.log")));
    }

    [Fact]
    public void TheMessagesFolderTakesItsCapitalAndKeepsItsFiles()
    {
        Write(@"messages\messages-2026-10-05.jsonl", "{}");

        AppFolders.MigrateLegacyLayout(_root);

        Assert.Equal(["Messages"], NamesIn(_root));
        Assert.Equal(["messages-2026-10-05.jsonl"], NamesIn(AppFolders.Messages(_root)));
    }

    [Fact]
    public void MigratingTwiceOrWithNothingToMoveChangesNothing()
    {
        AppFolders.MigrateLegacyLayout(Path.Combine(_root, "not-there"));   // a first start: no folder yet
        Write("journal-2026-10-05.log", "x");
        Write(@"messages\messages-2026-10-05.jsonl", "{}");
        AppFolders.MigrateLegacyLayout(_root);
        var before = NamesIn(_root);

        AppFolders.MigrateLegacyLayout(_root);

        Assert.Equal(before, NamesIn(_root));
        Assert.Equal(["dyss-cockpit-2026-10-05.log"], NamesIn(AppFolders.Logs(_root)));
    }
}
