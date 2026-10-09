using System.Text.Json;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;
using DyssCockpit.Core;
using Microsoft.Extensions.Time.Testing;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The record of every robot message: one file per day, old days cleared, and the tick in the Journal tab.</summary>
public sealed class MessageLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dyss-messages-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static RobotMessage Message(DateTimeOffset at, string payload = """{"msg":"CURRENT-STATE"}""") =>
        new(at, "RB05/SERIAL/status", payload);

    private static DateTimeOffset Noon(int year, int month, int day) => new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero).ToLocalTime();

    [Fact]
    public void EachMessageIsALineOfTheFileOfItsDay()
    {
        using (var log = new MessageLog(_dir))
        {
            log.Write(Message(Noon(2026, 9, 26)));
            log.Write(Message(Noon(2026, 9, 26), "not json"));
            log.Write(Message(Noon(2026, 9, 27)));
        }

        var first = File.ReadAllLines(Path.Combine(_dir, "messages-2026-09-26.jsonl"));
        Assert.Equal(2, first.Length);
        using var doc = JsonDocument.Parse(first[0]);
        Assert.Equal("RB05/SERIAL/status", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("CURRENT-STATE", doc.RootElement.GetProperty("payload").GetProperty("msg").GetString());
        // A payload that is not JSON is kept as text.
        Assert.Contains("\"payload\":\"not json\"", first[1], StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "messages-2026-09-27.jsonl")));
    }

    [Fact]
    public void ARestartTheSameDayAppendsToItsFile()
    {
        using (var log = new MessageLog(_dir)) log.Write(Message(Noon(2026, 9, 26)));
        using (var log = new MessageLog(_dir)) log.Write(Message(Noon(2026, 9, 26)));

        Assert.Equal(2, File.ReadAllLines(Path.Combine(_dir, "messages-2026-09-26.jsonl")).Length);
    }

    [Fact]
    public void FilesOlderThanThirtyDaysAreDeletedWhenADayStarts()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "messages-2026-08-01.jsonl"), "{}");
        File.WriteAllText(Path.Combine(_dir, "messages-2026-08-27.jsonl"), "{}");
        File.WriteAllText(Path.Combine(_dir, "autre-fichier.txt"), "gardé");

        using (var log = new MessageLog(_dir)) log.Write(Message(Noon(2026, 9, 26)));

        Assert.False(File.Exists(Path.Combine(_dir, "messages-2026-08-01.jsonl")));
        Assert.True(File.Exists(Path.Combine(_dir, "messages-2026-08-27.jsonl")));   // exactly 30 days: kept
        Assert.True(File.Exists(Path.Combine(_dir, "autre-fichier.txt")));
    }

    private string Day(string stamp)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"messages-{stamp}.jsonl");
        File.WriteAllText(path, "{}");
        return path;
    }

    /// <summary>A clock at noon on 9 October 2026, UTC.</summary>
    private static FakeTimeProvider Clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    [Fact]
    public void TheNumberOfDaysKeptIsTheOneAskedFor()
    {
        var old = Day("2026-09-20");
        var recent = Day("2026-09-24");

        using (var log = new MessageLog(_dir, keepDays: 3)) log.Write(Message(Noon(2026, 9, 26)));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void ZeroDaysKeepsEverythingForEver()
    {
        var ancient = Day("2020-01-01");

        using (var log = new MessageLog(_dir, keepDays: 0, time: Clock()))
        {
            log.Write(Message(Noon(2026, 9, 26)));
            log.Purge();
        }

        Assert.True(File.Exists(ancient));
    }

    [Fact]
    public void ClearingOldRecordsDoesNotNeedAnythingToBeRecorded()
    {
        var old = Day("2026-09-01");
        var recent = Day("2026-10-01");
        using var log = new MessageLog(_dir, keepDays: 30, time: Clock());

        log.Purge();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void TheJournalAppliesANewNumberOfDaysAsSoonAsItIsChosen()
    {
        var threeWeeks = Day("2026-09-18");
        var settings = AppSettings.Load(Path.Combine(_dir, "settings.json"));
        using var journal = new JournalViewModel(TestHub.Create(), settings, new MessageLog(_dir, time: Clock()));
        Assert.True(File.Exists(threeWeeks));   // 30 days is the default, nothing is cleared by opening the tab

        settings.MessageRetentionDays = 14;

        Assert.False(File.Exists(threeWeeks));
    }

    [Fact]
    public void TheJournalOnlyRecordsWhileTheBoxIsTickedAndRemembersIt()
    {
        var settingsFile = Path.Combine(_dir, "settings.json");
        var hub = TestHub.Create();
        var settings = AppSettings.Load(settingsFile);
        using var journal = new JournalViewModel(hub, settings, new MessageLog(_dir));

        journal.CaptureMessage(Message(DateTimeOffset.Now));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir, "messages-*") : []);

        journal.RecordMessages = true;
        journal.CaptureMessage(Message(DateTimeOffset.Now));
        Assert.Single(Directory.GetFiles(_dir, "messages-*"));
        Assert.StartsWith("1 message(s) aujourd'hui", journal.RecordInfo, StringComparison.Ordinal);

        Assert.True(AppSettings.Load(settingsFile).RecordMessages);
        journal.RecordMessages = false;
        Assert.False(AppSettings.Load(settingsFile).RecordMessages);
    }
}
