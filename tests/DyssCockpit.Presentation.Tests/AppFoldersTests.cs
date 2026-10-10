using DyssCockpit.Presentation.Services;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The data folder's layout: what is where under the application's folder.</summary>
public sealed class AppFoldersTests
{
    [Fact]
    public void TheLogsTheMessagesAndTheCleansHaveEnglishFoldersOfTheirOwn()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyss-root");

        Assert.Equal(Path.Combine(root, "Logs"), AppFolders.Logs(root));
        Assert.Equal(Path.Combine(root, "Messages"), AppFolders.Messages(root));
        Assert.Equal(Path.Combine(root, "Cleans"), AppFolders.Cleans(root));
    }
}
