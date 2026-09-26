using System.Text.Json.Nodes;

namespace MyDyson.Core;

public sealed record VoiceDownloadStatus(string? State, string? Language, int? Progress);

/// <summary>
/// Folds the robot's message stream into one current picture: the classic CURRENT-STATE, the jdm
/// property set, and the last few one-off events. Pure state, no I/O, so it can be unit tested
/// from captures and reused by any front end.
/// </summary>
public sealed class RobotStateTracker
{
    public RobotState? State { get; private set; }
    public JdmProperties Jdm { get; } = new();
    public VoiceDownloadStatus? VoiceDownload { get; private set; }
    public DateTimeOffset? LastMessageUtc { get; private set; }

    public event Action<RobotState>? StateChanged;
    public event Action<JdmProperties>? JdmChanged;
    public event Action<string, JsonObject>? EventReceived;
    public event Action<IReadOnlyList<RobotPosition>>? CleanPathChanged;

    /// <summary>
    /// The current task's driven path, accumulated from the jdm "cur_path" property pushed
    /// incrementally throughout a clean (not retained by the robot itself: REST's own live-map
    /// endpoint only has a snapshot from whenever it was last polled). Each push is a flat array
    /// <c>[firstId, x1,y1,angle1,update1, x2,y2,angle2,update2, …, unixTimestamp]</c>; reset when a
    /// new task's ids restart from a lower value, or explicitly on <c>event.startClean.post</c>.
    /// Always a snapshot: the list itself is appended to on the MQTT thread while the UI reads it
    /// (typically later, from a dispatcher callback), and handing out the live list would let the
    /// two collide mid-enumeration. <see cref="CleanPathChanged"/> carries a snapshot too.
    /// </summary>
    public IReadOnlyList<RobotPosition> CleanPath
    {
        get { lock (_cleanPathLock) return _cleanPath.ToArray(); }
    }

    private readonly List<RobotPosition> _cleanPath = [];
    private readonly object _cleanPathLock = new();
    private long? _lastCleanPathId;

    /// <summary>Applies one message. Returns true when the visible state changed.</summary>
    public bool Apply(RobotMessage message)
    {
        LastMessageUtc = message.ReceivedUtc;
        if (message.Json is not JsonObject json) return false;

        if (message.Topic.EndsWith("/status/jdm", StringComparison.Ordinal))
            return ApplyJdm(json);
        if (message.Topic.EndsWith("/status", StringComparison.Ordinal))
            return ApplyClassic(message.Payload, json);
        return false;
    }

    private bool ApplyClassic(string payload, JsonObject json)
    {
        switch (RobotMessage.StringOf(json["msg"]))
        {
            case "CURRENT-STATE":
            case "STATE-CHANGE":
                var parsed = RobotState.Parse(payload);
                if (parsed is null) return false;
                State = State is null ? parsed : State.Merge(parsed);
                StateChanged?.Invoke(State);
                return true;

            case "VOICE-DOWNLOAD-STATUS":
                VoiceDownload = new VoiceDownloadStatus(
                    json["state"]?.GetValue<string>(),
                    json["language"]?.GetValue<string>(),
                    json["progress"]?.GetValue<int>());
                EventReceived?.Invoke("VOICE-DOWNLOAD-STATUS", json);
                return true;

            case { } other:
                EventReceived?.Invoke(other, json);
                return false;

            default:
                return false;
        }
    }

    private bool ApplyJdm(JsonObject json)
    {
        var method = RobotMessage.StringOf(json["method"]);
        switch (method)
        {
            case "prop.post" when json["params"] is JsonObject pushed:
                if (pushed["cur_path"] is JsonArray path) AppendCleanPath(path);
                Jdm.Merge(pushed);
                JdmChanged?.Invoke(Jdm);
                return true;

            case "prop.get" when json["data"] is JsonObject read:
                Jdm.Merge(read);
                JdmChanged?.Invoke(Jdm);
                return true;

            case "event.startClean.post":
                lock (_cleanPathLock)
                {
                    _cleanPath.Clear();
                    _lastCleanPathId = null;
                }
                CleanPathChanged?.Invoke([]);
                EventReceived?.Invoke(method!, json);
                return false;

            case { } m when m.StartsWith("event.", StringComparison.Ordinal):
                EventReceived?.Invoke(m, json);
                return false;

            default:
                return false;
        }
    }

    private void AppendCleanPath(JsonArray arr)
    {
        var nums = new List<double>(arr.Count);
        foreach (var n in arr)
        {
            if (n is not JsonValue v || !v.TryGetValue<double>(out var d)) return; // malformed batch, ignore
            nums.Add(d);
        }
        if (nums.Count < 6 || (nums.Count - 2) % 4 != 0) return;

        var firstId = (long)nums[0];
        RobotPosition[] snapshot;
        lock (_cleanPathLock)
        {
            if (_lastCleanPathId is { } last && firstId < last) _cleanPath.Clear(); // a new task's ids restarted
            _lastCleanPathId = firstId;

            var pointCount = (nums.Count - 2) / 4;
            for (var i = 0; i < pointCount; i++)
            {
                var b = 1 + i * 4;
                _cleanPath.Add(new RobotPosition(firstId + i, nums[b], nums[b + 1], nums[b + 2], (int)nums[b + 3]));
            }
            snapshot = _cleanPath.ToArray();
        }
        CleanPathChanged?.Invoke(snapshot);
    }
}
