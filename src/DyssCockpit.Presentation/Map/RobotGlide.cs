using DyssCockpit.Core;

namespace DyssCockpit.Presentation.Map;

/// <summary>
/// Where to show the robot so it glides between the positions it reports instead of jumping from
/// one to the next. Each new position is reached over the time the previous one took to arrive,
/// so the robot keeps a steady pace, a step behind the real one. A long jump (relocalised, or a
/// first position) is shown at once rather than slid across the map.
/// </summary>
public sealed class RobotGlide
{
    /// <summary>Further than this in one step, the robot is placed rather than slid.</summary>
    public const double MaximumGlide = 1.5;
    private const double ShortestStep = 0.2, LongestStep = 3;

    private RobotPosition? _from, _to;
    private double _start, _duration, _lastArrival = double.NaN;

    /// <summary>Whether the robot is still on its way to the last position at <paramref name="now"/> (seconds).</summary>
    public bool IsGliding(double now) => _to is not null && _from is not null && now - _start < _duration;

    /// <summary>The position to show at <paramref name="now"/>, given the latest reported one.</summary>
    public RobotPosition At(RobotPosition target, double now)
    {
        if (_to is null || !Same(_to, target))
        {
            var shown = _to is null ? null : Current(now);
            var interval = double.IsNaN(_lastArrival) ? LongestStep : now - _lastArrival;
            _lastArrival = now;
            var far = shown is null || Math.Sqrt(Math.Pow(target.X - shown.X, 2) + Math.Pow(target.Y - shown.Y, 2)) > MaximumGlide;
            _from = far ? null : shown;
            _to = target;
            _start = now;
            _duration = Math.Clamp(interval, ShortestStep, LongestStep);
        }
        return Current(now);
    }

    private RobotPosition Current(double now)
    {
        if (_from is not { } from || _duration <= 0) return _to!;
        var to = _to!;
        var k = Math.Clamp((now - _start) / _duration, 0, 1);
        if (k >= 1) return to;
        // The heading turns the short way round.
        var turn = Math.IEEERemainder(to.Angle - from.Angle, 2 * Math.PI);
        return to with { X = from.X + (to.X - from.X) * k, Y = from.Y + (to.Y - from.Y) * k, Angle = from.Angle + turn * k };
    }

    private static bool Same(RobotPosition a, RobotPosition b) => a.X == b.X && a.Y == b.Y && a.Angle == b.Angle;
}
