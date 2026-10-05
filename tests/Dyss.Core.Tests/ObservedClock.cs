using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Dyss.Core.Tests;

/// <summary>
/// A clock that only moves when told to, and reports each wait set on it: once a test has seen
/// the wait, moving the clock past it is sure to end it.
/// </summary>
internal sealed class ObservedClock : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _delays.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>The next wait the code under test sets, as soon as it is set.</summary>
    public async Task<TimeSpan> NextDelayAsync() =>
        await _delays.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    /// <summary>Whether a wait has been set that <see cref="NextDelayAsync"/> has not returned yet.</summary>
    public bool HasPendingDelay => _delays.Reader.Count > 0;
}
