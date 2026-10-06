using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;
using Microsoft.Extensions.Logging;

namespace DyssCockpit.Presentation.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dyss-log-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void LinesGoToTodaysFileWithTheirLevelAndWhereTheyCameFrom()
    {
        var provider = new FileLoggerProvider(_folder);
        var logger = provider.CreateLogger("DyssCockpit.Core.RobotSession");

        Write(logger, LogLevel.Warning, "MQTT dropped (wifi)");
        Write(logger, LogLevel.Error, "reconnect refused", new InvalidOperationException("token expired"));
        provider.Dispose();   // writes out what is queued

        var text = File.ReadAllText(provider.CurrentFile);
        Assert.Equal($"journal-{DateTime.Now:yyyy-MM-dd}.log", Path.GetFileName(provider.CurrentFile));
        Assert.Contains("WARN RobotSession: MQTT dropped (wifi)", text, StringComparison.Ordinal);
        Assert.Contains("ERRO RobotSession: reconnect refused", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: token expired", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LinesBelowTheChosenLevelAreLeftOut()
    {
        var provider = new FileLoggerProvider(_folder, minimumLevel: LogLevel.Information);
        var logger = provider.CreateLogger("App");

        Write(logger, LogLevel.Debug, "MQTT trace");
        Write(logger, LogLevel.Information, "connected");
        provider.Dispose();

        var text = File.ReadAllText(provider.CurrentFile);
        Assert.DoesNotContain("MQTT trace", text, StringComparison.Ordinal);
        Assert.Contains("connected", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsOlderThanAWeekGoAndNothingElseIsTouched()
    {
        // The folder is the one that holds the session, the preferences and the message records.
        string Old(string name)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-8));
            return path;
        }
        var oldLog = Old("journal-2026-09-01.log");
        var recentLog = Path.Combine(_folder, "journal-2026-09-30.log");
        File.WriteAllText(recentLog, "x");
        var others = new[] { Old("session.bin"), Old("erreurs.log"), Old("messages-2026-09-01.jsonl") };

        new FileLoggerProvider(_folder).Dispose();

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(recentLog));
        Assert.All(others, f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public void LoggingOnceDisposedIsHarmless()
    {
        // What the session says while the process exits, after the log has been written out.
        var provider = new FileLoggerProvider(_folder);
        var logger = provider.CreateLogger("RobotSession");
        provider.Dispose();
        provider.Dispose();

        Write(logger, LogLevel.Information, "disposed");
    }

    private static void Write(ILogger logger, LogLevel level, string text, Exception? ex = null) =>
        logger.Log(level, default, text, ex, (s, _) => s);
}

public class JournalLoggerTests
{
    [Fact]
    public void WhatTheSessionSaysReachesBothTheLogAndTheJournal()
    {
        var log = new RecordingLogger(LogLevel.Information);
        var journal = new List<string>();
        var logger = new JournalLogger(log, journal.Add);

        logger.Log(LogLevel.Warning, default, "MQTT dropped (wifi); reconnecting in 5 s (attempt 1)", null, (s, _) => s);

        Assert.Equal(["MQTT dropped (wifi); reconnecting in 5 s (attempt 1)"], journal);
        Assert.Equal([(LogLevel.Warning, "MQTT dropped (wifi); reconnecting in 5 s (attempt 1)")], log.Lines);
    }

    [Fact]
    public void DetailBelowInformationStaysOutOfTheJournal()
    {
        var log = new RecordingLogger(LogLevel.Trace);
        var journal = new List<string>();
        var logger = new JournalLogger(log, journal.Add);

        logger.Log(LogLevel.Debug, default, "MQTT trace", null, (s, _) => s);

        Assert.Empty(journal);
        Assert.Single(log.Lines);
    }

    [Fact]
    public void TheJournalShowsWhatTheLogFileWouldLeaveOut()
    {
        // The user's journal does not depend on how much the log on disk keeps.
        var log = new RecordingLogger(LogLevel.Error);
        var journal = new List<string>();
        var logger = new JournalLogger(log, journal.Add);

        Assert.True(logger.IsEnabled(LogLevel.Information));
        logger.Log(LogLevel.Information, default, "connected", null, (s, _) => s);

        Assert.Equal(["connected"], journal);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void TheDashboardsJournalLinesAreKeptInTheLogToo()
    {
        var loggers = new RecordingLoggerFactory();
        var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(new TestHub.RouteHandler())) { BearerToken = "t" };
        var stored = new StoredSession("test@example.invalid", "CH", "fr-CH", null, "t", DateTimeOffset.UnixEpoch);
        using var hub = new RobotHub(RobotContext.FromLogin(api, stored, loggers), new TestHub.InlineDispatcher(), new FakeDialogs());

        hub.AddLog("réglages des pièces enregistrés");

        Assert.EndsWith(" réglages des pièces enregistrés", Assert.Single(hub.Log), StringComparison.Ordinal);
        Assert.Equal([(LogLevel.Information, "réglages des pièces enregistrés")], loggers.Created[typeof(RobotHub).FullName!].Lines);
    }

    private sealed class RecordingLogger(LogLevel minimum) : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public Dictionary<string, RecordingLogger> Created { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            if (!Created.TryGetValue(categoryName, out var logger)) Created[categoryName] = logger = new RecordingLogger(LogLevel.Information);
            return logger;
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }
}
