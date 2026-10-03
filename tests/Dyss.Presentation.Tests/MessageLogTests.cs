using System.Text.Json;
using Dyss.Presentation.Services;
using Dyss.Presentation.ViewModels;
using Dyss.Core;

namespace Dyss.Presentation.Tests;

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

    [Fact]
    public void TheJournalOnlyRecordsWhileTheBoxIsTickedAndRemembersIt()
    {
        var settingsFile = Path.Combine(_dir, "display.json");
        var hub = TestHub.Create();
        var settings = DisplaySettings.Load(settingsFile);
        using var journal = new JournalViewModel(hub, settings, new MessageLog(_dir));

        journal.CaptureMessage(Message(DateTimeOffset.Now));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir, "messages-*") : []);

        journal.RecordMessages = true;
        journal.CaptureMessage(Message(DateTimeOffset.Now));
        Assert.Single(Directory.GetFiles(_dir, "messages-*"));
        Assert.StartsWith("1 message(s) aujourd'hui", journal.RecordInfo, StringComparison.Ordinal);

        Assert.True(DisplaySettings.Load(settingsFile).RecordMessages);
        journal.RecordMessages = false;
        Assert.False(DisplaySettings.Load(settingsFile).RecordMessages);
    }
}
