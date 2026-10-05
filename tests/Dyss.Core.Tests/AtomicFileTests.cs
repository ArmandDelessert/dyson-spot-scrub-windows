using Dyss.Core;

namespace Dyss.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dyss-atomic-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void WritesANewFileAndLeavesNothingElseBehind()
    {
        var path = Path.Combine(_folder, "display.json");

        AtomicFile.WriteAllText(path, """{"ShowFurniture":false}""");

        Assert.Equal("""{"ShowFurniture":false}""", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public void ReplacesAnExistingFileWhole()
    {
        var path = Path.Combine(_folder, "session.bin");
        File.WriteAllBytes(path, new byte[1000]);

        AtomicFile.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(_folder));
    }

    [Fact]
    public void TextIsWrittenAsUtf8WithoutAByteOrderMark()
    {
        // What File.WriteAllText wrote before, and what the readers expect.
        var path = Path.Combine(_folder, "display.json");

        AtomicFile.WriteAllText(path, "Pièce");

        Assert.Equal("Pièce"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void AFailedReplacementLeavesTheTargetAsItWasAndNoTemporaryFile()
    {
        // A folder where the file should be: the final move cannot happen.
        var path = Path.Combine(_folder, "display.json");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inside.txt"), "untouched");

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "new"));

        Assert.Equal("untouched", File.ReadAllText(Path.Combine(path, "inside.txt")));
        Assert.Empty(Directory.GetFiles(_folder));
    }
}
