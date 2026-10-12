using Microsoft.Extensions.Time.Testing;

namespace DyssCockpit.TestSupport;

internal static class TestClock
{
    /// <summary>A clock stopped at noon UTC on the given day, in a local time zone of UTC too, so that "today" is that day wherever the tests run.</summary>
    public static FakeTimeProvider NoonUtc(int year, int month, int day)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }
}
