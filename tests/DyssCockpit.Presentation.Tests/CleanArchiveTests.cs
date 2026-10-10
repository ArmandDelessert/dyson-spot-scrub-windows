using System.IO.Compression;
using System.Text.Json;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using Microsoft.Extensions.Time.Testing;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The cleans kept on disk once the cloud has forgotten them.</summary>
public sealed class CleanArchiveTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dyss-cleans-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A clock at noon on 10 October 2026, UTC.</summary>
    private static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    private static long Unix(int year, int month, int day) => new DateTimeOffset(year, month, day, 9, 37, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static CleanSummary Summary(string id, long start) => JsonSerializer.Deserialize<CleanSummary>(
        $$$"""{"cleanId":"{{{id}}}","persistentMapId":"1782494719","startTime":{{{start}}},"endTime":{{{start + 600}}},"cleanDuration":10,"areaCleaned":6.4,"startBattery":100.0,"endBattery":97.0,"faults":[]}""",
        Web)!;

    // What the cloud returned for a clean with a stain of a type no earlier note had, and a field this version does not model.
    private static CleanDetail Detail(string id) => JsonSerializer.Deserialize<CleanDetail>(
        $$$"""
        {"cleanId":"{{{id}}}","persistentMapId":"1782494719","zones":[{"id":"10","name":"Salle de bain","type":"bathroom","cleanStatus":"CLEAN_COMPLETE"}],
         "cleanPath":[{"x":0,"y":0,"update":1},{"x":1.5,"y":-2.25,"update":0}],"obstacles":[{"x":-0.11,"y":1.23}],
         "dirt":[{"x":3.736144,"y":3.9629486,"type":"solid","isUvScanOn":false}],"hazardZones":[],"somethingNew":{"a":1}}
        """, Web)!;

    [Fact]
    public void ACleanIsKeptWithItsListLineAndItsDetailAndReadBackAsItWas()
    {
        var archive = new CleanArchive(_folder, Clock());

        Assert.False(archive.Has("abc"));
        Assert.True(archive.Save(Summary("abc", Unix(2026, 10, 10)), Detail("abc")));

        Assert.True(archive.Has("abc"));
        var file = Assert.Single(Directory.GetFiles(_folder));
        Assert.Equal("clean-2026-10-10-abc.json.gz", Path.GetFileName(file));
        var detail = archive.LoadDetail("abc")!;
        Assert.Equal("solid", Assert.Single(detail.Dirt!).Type);   // the type the notes did not have yet
        Assert.Equal(2, detail.CleanPath!.Count);
        Assert.Equal(-2.25, detail.CleanPath[1].Y);
        Assert.Equal("Salle de bain", Assert.Single(detail.Zones!).Name);
        var summary = Assert.Single(archive.Summaries());
        Assert.Equal("abc", summary.CleanId);
        Assert.Equal(10, summary.CleanDurationMinutes);
    }

    [Fact]
    public void WhatTheCloudSentThatThisVersionDoesNotKnowIsKept()
    {
        var archive = new CleanArchive(_folder, Clock());
        archive.Save(Summary("abc", Unix(2026, 10, 10)), Detail("abc"));

        var detail = archive.LoadDetail("abc")!;

        Assert.True(detail.Extra!.ContainsKey("somethingNew"));
        Assert.Equal(1, detail.Extra["somethingNew"].GetProperty("a").GetInt32());
    }

    [Fact]
    public void TheFileIsCompressedJsonOfTwoLinesTheListLineThenTheDetail()
    {
        var archive = new CleanArchive(_folder, Clock());
        archive.Save(Summary("abc", Unix(2026, 10, 10)), Detail("abc"));

        using var reader = new StreamReader(new GZipStream(File.OpenRead(Directory.GetFiles(_folder)[0]), CompressionMode.Decompress));
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("abc", JsonDocument.Parse(lines[0]).RootElement.GetProperty("cleanId").GetString());
        Assert.True(JsonDocument.Parse(lines[1]).RootElement.TryGetProperty("cleanPath", out _));
    }

    [Fact]
    public void ACleanAlreadyKeptIsNotWrittenAgain()
    {
        var archive = new CleanArchive(_folder, Clock());
        archive.Save(Summary("abc", Unix(2026, 10, 10)), Detail("abc"));
        var written = File.GetLastWriteTimeUtc(Directory.GetFiles(_folder)[0]);
        File.SetLastWriteTimeUtc(Directory.GetFiles(_folder)[0], written.AddHours(-1));

        Assert.True(archive.Save(Summary("abc", Unix(2026, 10, 10)), Detail("abc")));

        Assert.Single(Directory.GetFiles(_folder));
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(Directory.GetFiles(_folder)[0]));
    }

    [Fact]
    public void TheListIsNewestFirstWhateverTheOrderOfTheFiles()
    {
        var archive = new CleanArchive(_folder, Clock());
        archive.Save(Summary("old", Unix(2026, 9, 20)), Detail("old"));
        archive.Save(Summary("new", Unix(2026, 10, 10)), Detail("new"));
        archive.Save(Summary("mid", Unix(2026, 10, 1)), Detail("mid"));

        Assert.Equal(["new", "mid", "old"], archive.Summaries().Select(s => s.CleanId));
    }

    [Fact]
    public void CleansOlderThanTheRetentionGoByTheDayTheyBeganAndNothingElseIsTouched()
    {
        var archive = new CleanArchive(_folder, Clock());
        archive.Save(Summary("old", Unix(2026, 8, 1)), Detail("old"));         // 70 days
        archive.Save(Summary("edge", Unix(2026, 9, 10)), Detail("edge"));      // exactly 30 days: kept
        archive.Save(Summary("new", Unix(2026, 10, 10)), Detail("new"));
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_folder, "clean-notaday-abc.json.gz"), "x");

        Assert.Equal(1, archive.CountOlderThan(30));
        archive.Purge(30);

        Assert.False(archive.Has("old"));
        Assert.True(archive.Has("edge"));
        Assert.True(archive.Has("new"));
        Assert.True(File.Exists(Path.Combine(_folder, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_folder, "clean-notaday-abc.json.gz")));
    }

    [Fact]
    public void ZeroKeepsEveryCleanForEver()
    {
        var archive = new CleanArchive(_folder, Clock());
        archive.Save(Summary("ancient", Unix(2020, 1, 1)), Detail("ancient"));

        archive.Purge(0);

        Assert.Equal(0, archive.CountOlderThan(0));
        Assert.True(archive.Has("ancient"));
    }

    [Fact]
    public void AFileThatCannotBeReadIsSkippedNotAnError()
    {
        var archive = new CleanArchive(_folder, Clock());
        archive.Save(Summary("good", Unix(2026, 10, 10)), Detail("good"));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "clean-2026-10-09-bad.json.gz"), "this is not gzip");

        Assert.Equal(["good"], archive.Summaries().Select(s => s.CleanId));
        Assert.Null(archive.LoadDetail("bad"));
        Assert.Null(archive.LoadDetail("missing"));
    }

    [Fact]
    public void ANonExistentFolderHoldsNothingAndIsCreatedByTheFirstSave()
    {
        var archive = new CleanArchive(Path.Combine(_folder, "deeper"), Clock());

        Assert.Empty(archive.Summaries());
        Assert.Equal(0, archive.CountOlderThan(30));
        archive.Purge(30);   // nothing to do, no error
        Assert.True(archive.Save(Summary("abc", Unix(2026, 10, 10)), Detail("abc")));
    }
}
