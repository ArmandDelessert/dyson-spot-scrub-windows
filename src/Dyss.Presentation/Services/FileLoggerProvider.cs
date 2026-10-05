using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Dyss.Presentation.Services;

/// <summary>
/// The application's log on disk: one file a day, journal-2026-10-05.log, kept a week. Lines are
/// queued and written by a thread of their own, so logging never waits for the disk; disposing
/// writes out what is still queued. Taken from HusqA Cockpit, with two changes: the file name is a
/// parameter, and only files of that name are cleaned up, the folder being shared with the
/// session and the preferences.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly string _folder;
    private readonly string _prefix;
    private readonly LogLevel _minimumLevel;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string folder, string prefix = "journal-", LogLevel minimumLevel = LogLevel.Information)
    {
        _folder = folder;
        _prefix = prefix;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(folder);
        DeleteOldLogs();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Log writer" };
        _writer.Start();
    }

    /// <summary>The file written today.</summary>
    public string CurrentFile => Path.Combine(_folder, $"{_prefix}{DateTime.Now:yyyy-MM-dd}.log");

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
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, $"{_prefix}*.log"))
            {
                if (DateTime.Now - File.GetLastWriteTime(file) > Retention) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start.
        }
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
