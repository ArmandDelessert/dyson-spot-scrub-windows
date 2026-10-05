namespace Dyss.Core;

/// <summary>
/// What <see cref="RobotSession"/> needs of a connection to the broker: to open it, to hear what
/// comes in and when it drops, and to close it. <see cref="RobotMqttClient"/> is the real one;
/// tests substitute a fake to drive drops and reconnections without a broker.
/// </summary>
internal interface IRobotLink : IAsyncDisposable
{
    bool IsConnected { get; }

    event Action<RobotMessage>? MessageReceived;
    event Action<string>? Disconnected;
    event Action<Exception>? ListenerFailed;
    event Action<string>? PrefixChanged;

    Task ConnectAsync(CancellationToken ct = default);
}
