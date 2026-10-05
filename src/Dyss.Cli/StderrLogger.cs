using Microsoft.Extensions.Logging;

namespace Dyss.Cli;

/// <summary>
/// Writes what the session and the MQTT client log to the error stream, tagged as "[session] …":
/// the output stays free for the messages themselves, which can then be piped or saved.
/// </summary>
internal sealed class StderrLogger(string tag, LogLevel minimumLevel = LogLevel.Information) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.Error.WriteLine($"[{tag}] {formatter(state, exception)}" + (exception is null ? "" : $" | {exception}"));
    }
}
