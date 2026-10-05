using Microsoft.Extensions.Logging;

namespace Dyss.Presentation.Services;

/// <summary>
/// A logger whose lines also show in the Journal tab: everything of level Information or above
/// goes to <paramref name="journal"/> as it reads, on top of reaching <paramref name="inner"/>,
/// the application's log. Lets the robot session log as any other component while the user still
/// sees its drops and reconnections.
/// </summary>
internal sealed class JournalLogger(ILogger inner, Action<string> journal) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => ShowsInJournal(logLevel) || inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (inner.IsEnabled(logLevel)) inner.Log(logLevel, eventId, state, exception, formatter);
        if (ShowsInJournal(logLevel)) journal(formatter(state, exception));
    }

    private static bool ShowsInJournal(LogLevel level) => level is >= LogLevel.Information and < LogLevel.None;
}
