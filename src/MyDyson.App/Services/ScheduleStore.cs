using System.IO;
using System.Text.Json;
using MyDyson.Core;

namespace MyDyson.App.Services;

/// <summary>
/// The schedules created from this app. The robot keeps them and runs them, but never hands them
/// back — get_order only answers a count — so without this list the app could not show, change or
/// delete what it created. Kept per robot, next to the session, in clear: nothing in it is
/// sensitive. Schedules created on the phone are, for the same reason, invisible here.
/// </summary>
public sealed class ScheduleStore
{
    private static string DefaultPath => Path.Combine(SessionStore.Directory, "schedules.json");

    /// <summary>Where changes are written; null keeps them for the run only, which is what tests use.</summary>
    private readonly string? _path;
    private readonly Dictionary<string, List<CleaningSchedule>> _bySerial;

    public ScheduleStore() : this(null, new(StringComparer.Ordinal)) { }

    private ScheduleStore(string? path, Dictionary<string, List<CleaningSchedule>> bySerial)
    {
        _path = path;
        _bySerial = bySerial;
    }

    /// <summary>Reads the stored list, or starts empty when there is none or it cannot be read. <paramref name="path"/> is for tests.</summary>
    public static ScheduleStore Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, List<CleaningSchedule>>>(File.ReadAllText(path), ScheduleJson.Options) is { } stored)
                return new ScheduleStore(path, new Dictionary<string, List<CleaningSchedule>>(stored, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable file is not worth failing a launch over; the robot still has the schedules.
        }
        return new ScheduleStore(path, new(StringComparer.Ordinal));
    }

    public IReadOnlyList<CleaningSchedule> For(string serial) =>
        _bySerial.TryGetValue(serial, out var list) ? list : [];

    /// <summary>Adds the schedule, or replaces the stored one with the same id.</summary>
    public void Save(string serial, CleaningSchedule schedule)
    {
        if (!_bySerial.TryGetValue(serial, out var list)) _bySerial[serial] = list = [];
        var i = list.FindIndex(s => s.Id == schedule.Id);
        if (i >= 0) list[i] = schedule; else list.Add(schedule);
        Write();
    }

    public void Remove(string serial, long id)
    {
        if (_bySerial.TryGetValue(serial, out var list) && list.RemoveAll(s => s.Id == id) > 0) Write();
    }

    private void Write()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_bySerial, ScheduleJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The schedule is on the robot either way; it just will not be listed after a restart.
        }
    }
}
