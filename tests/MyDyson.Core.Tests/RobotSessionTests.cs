using MyDyson.Core;

namespace MyDyson.Core.Tests;

public class RobotSessionTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 80)]
    [InlineData(6, 120)]     // doubling would give 160; the cap holds
    [InlineData(40, 120)]    // and keeps holding however long the robot stays away
    public void ReconnectionBacksOffFromFiveSecondsAndStopsAtTwoMinutes(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RobotSession.BackoffFor(attempt));

    [Fact]
    public void TheFirstAttemptIsNeverInstant()
    {
        // A drop is usually the network, not the robot: retrying immediately just burns a
        // credentials fetch. Attempt numbering starts at 1, and anything lower is treated as 1.
        Assert.Equal(TimeSpan.FromSeconds(5), RobotSession.BackoffFor(0));
        Assert.Equal(TimeSpan.FromSeconds(5), RobotSession.BackoffFor(-3));
    }

    [Fact]
    public void ThePropertyRefreshAsksForWhatTheAppNeverRequests()
    {
        // The official app's list has neither, yet the robot answers for them (checked against the
        // real robot on 2026-09-22), which is what lets the dashboard show the drying countdown at
        // start-up instead of waiting for the next push.
        Assert.DoesNotContain("work_time", RobotMqttClient.AppPropertyNames);
        Assert.DoesNotContain("back_to_wash", RobotMqttClient.AppPropertyNames);
        Assert.Equal(["work_time", "back_to_wash"], RobotSession.PushOnlyPropertyNames);
        // charge_state, by contrast, is one the app does ask for.
        Assert.Contains("charge_state", RobotMqttClient.AppPropertyNames);
    }
}
