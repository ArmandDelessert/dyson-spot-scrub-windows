using DyssCockpit.Presentation.Services;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The application's command line: an ordinary start, and the diagnostic one-shot runs the README documents.</summary>
public class LaunchOptionsTests
{
    [Fact]
    public void AnOrdinaryStartHasNothingToDo()
    {
        var o = LaunchOptions.Parse([]);

        Assert.False(o.IsOneShot);
        Assert.False(o.Minimized);
        Assert.Null(o.Theme);
        Assert.Equal(20, o.After);
        Assert.Empty(o.Zones);
    }

    [Fact]
    public void StartedWithWindowsItGoesToTheNotificationArea() =>
        Assert.True(LaunchOptions.Parse([LaunchOptions.MinimizedArgument]).Minimized);

    [Fact]
    public void AScreenshotReadsAllItsOptions()
    {
        var o = LaunchOptions.Parse(["--screenshot", "out.png", "--after", "8", "--zones", "11, 10", "--tab", "2",
            "--manage-maps", "--layer", "1", "--map-id", "1000000002", "--scroll", "--theme", "dark", "--lang", "en", "--login"]);

        Assert.True(o.IsOneShot);
        Assert.True(o.IsScreenshot);
        Assert.Equal(("out.png", 8, 2, 1, "1000000002"), (o.Screenshot, o.After, o.Tab, o.Layer, o.MapId));
        Assert.Equal(["11", "10"], o.Zones);
        Assert.True(o.ManageMaps && o.Scroll && o.Login);
        Assert.Equal(("dark", "en"), (o.Theme, o.Language));
    }

    [Fact]
    public void TheExportsAreOneShotRunsToo()
    {
        var map = LaunchOptions.Parse(["--export-map", "map.png", "--serial", "S"]);
        var icon = LaunchOptions.Parse(["--export-icon", "a.ico", "--alert"]);

        Assert.Equal(("map.png", "S"), (map.ExportMap, map.Serial));
        Assert.True(map.IsOneShot);
        Assert.Equal("a.ico", icon.ExportIcon);
        Assert.True(icon.Alert && icon.IsOneShot);
    }

    [Theory]
    [InlineData("--theme", "blue")]
    [InlineData("--lang", "xx")]
    [InlineData("--after", "soon")]
    public void AValueThatCannotBeReadLeavesTheDefault(string name, string value)
    {
        var o = LaunchOptions.Parse([name, value]);

        Assert.Null(o.Theme);
        Assert.Null(o.Language);
        Assert.Equal(20, o.After);
    }

    [Fact]
    public void AnOptionWithoutItsValueIsIgnored() =>
        Assert.Null(LaunchOptions.Parse(["--screenshot"]).Screenshot);
}
