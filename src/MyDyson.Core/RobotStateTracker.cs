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
        switch (json["msg"]?.GetValue<string>())
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
        var method = json["method"]?.GetValue<string>();
        switch (method)
        {
            case "prop.post" when json["params"] is JsonObject pushed:
                Jdm.Merge(pushed);
                JdmChanged?.Invoke(Jdm);
                return true;

            case "prop.get" when json["data"] is JsonObject read:
                Jdm.Merge(read);
                JdmChanged?.Invoke(Jdm);
                return true;

            case { } m when m.StartsWith("event.", StringComparison.Ordinal):
                EventReceived?.Invoke(m, json);
                return false;

            default:
                return false;
        }
    }
}
