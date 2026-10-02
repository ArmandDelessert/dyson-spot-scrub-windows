using Dyss.Core;

namespace Dyss.Core.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), $"dyss-appdata-{Guid.NewGuid():N}");

    public SessionStoreTests() => Directory.CreateDirectory(_appData);

    public void Dispose() => Directory.Delete(_appData, recursive: true);

    [Fact]
    public void TheFolderOfTheFormerNameIsMovedOverWithWhatItHolds()
    {
        var former = Path.Combine(_appData, SessionStore.FormerFolderName);
        Directory.CreateDirectory(former);
        File.WriteAllText(Path.Combine(former, "display.json"), "{}");

        var folder = SessionStore.Resolve(_appData);

        Assert.Equal(Path.Combine(_appData, SessionStore.FolderName), folder);
        Assert.True(File.Exists(Path.Combine(folder, "display.json")));
        Assert.False(Directory.Exists(former));
    }

    [Fact]
    public void AnExistingFolderIsKeptAndTheFormerOneLeftAlone()
    {
        Directory.CreateDirectory(Path.Combine(_appData, SessionStore.FolderName));
        Directory.CreateDirectory(Path.Combine(_appData, SessionStore.FormerFolderName));

        Assert.Equal(Path.Combine(_appData, SessionStore.FolderName), SessionStore.Resolve(_appData));
        Assert.True(Directory.Exists(Path.Combine(_appData, SessionStore.FormerFolderName)));
    }

    [Fact]
    public void WithNeitherTheNewNameIsUsed() =>
        Assert.Equal(Path.Combine(_appData, SessionStore.FolderName), SessionStore.Resolve(_appData));

    [Fact]
    public void AFolderThatCannotBeMovedKeepsBeingUsed()
    {
        var former = Path.Combine(_appData, SessionStore.FormerFolderName);
        Directory.CreateDirectory(former);
        // A file held open, as the CLI's watch log might be: Windows refuses to move the folder.
        using var open = File.Open(Path.Combine(former, "messages.jsonl"), FileMode.Create, FileAccess.Write, FileShare.None);

        Assert.Equal(former, SessionStore.Resolve(_appData));
    }
}
