using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// The application's log on disk: one file a day, Logs\dyss-cockpit-2026-10-05.log, kept for
/// <see cref="RetentionDays"/> days. Lines are queued and written by a thread of their own, so
/// logging never waits for the disk; disposing writes out what is still queued. Taken from HusqA
/// Cockpit, with three changes: the file name is a parameter, only files of that name are cleaned
/// up, and how long they are kept can be changed, and is applied again each day for an application
/// that stays open for weeks.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>The start of every log file's name, which then goes on with the day: dyss-cockpit-2026-10-05.log.</summary>
    public const string DefaultPrefix = "dyss-cockpit-";

    private const string Extension = ".log";

    private readonly string _folder;
    private readonly string _prefix;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _time;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private int _retentionDays;
    private DateOnly _purgedOn;

    /// <param name="retentionDays">How many days back the files are kept; zero or less keeps them all.</param>
    public FileLoggerProvider(string folder, string prefix = DefaultPrefix, LogLevel minimumLevel = LogLevel.Information,
        int retentionDays = 7, TimeProvider? time = null)
    {
        _folder = folder;
        _prefix = prefix;
        _minimumLevel = minimumLevel;
        _time = time ?? TimeProvider.System;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(folder);
        DeleteOldLogs();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Log writer" };
        _writer.Start();
    }

    /// <summary>The file written today.</summary>
    public string CurrentFile => Path.Combine(_folder, $"{_prefix}{Today:yyyy-MM-dd}{Extension}");

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>How many days back the files are kept, zero or less for ever. Changing it deletes what is now too old at once.</summary>
    public int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set
        {
            Volatile.Write(ref _retentionDays, value);
            DeleteOldLogs();
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortName(categoryName));

    /// <summary>Writes out what is queued, waiting for it two seconds at most. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_queue.IsAddingCompleted) return;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private static string ShortName(string category) => category[(category.LastIndexOf('.') + 1)..];

    private void Enqueue(string line)
    {
        // Anything logged once the provider is disposed, on the way out, is dropped rather than thrown.
        try { _queue.TryAdd(line); }
        catch (InvalidOperationException) { }
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                // A new day: the old files are looked at again, since the application may have run for weeks.
                if (Today != _purgedOn) DeleteOldLogs();
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never crash the app.
            }
        }
    }

    private void DeleteOldLogs()
    {
        _purgedOn = Today;
        DatedFiles.DeleteOlderThan(_folder, _prefix, Extension, _purgedOn, RetentionDays);
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var builder = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(logLevel.ToString()[..4].ToUpperInvariant())
                .Append(' ').Append(category).Append(": ")
                .Append(formatter(state, exception))
                .AppendLine();
            if (exception is not null) builder.AppendLine(exception.ToString());
            owner.Enqueue(builder.ToString());
        }
    }
}
