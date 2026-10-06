using DyssCockpit.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// The app's single connection to Dyson: REST client with the stored session, the chosen robot,
/// and its live MQTT session. Created once at startup and handed to the view models.
/// </summary>
public sealed class RobotContext : IAsyncDisposable
{
    public DysonCloudClient Api { get; }
    public StoredSession Stored { get; }
    public List<Device> Devices { get; private set; } = new();
    public Device? Robot { get; private set; }
    public RobotSession? Session { get; private set; }
    /// <summary>The application's loggers, which the session and the dashboard log to.</summary>
    public ILoggerFactory Loggers { get; }

    /// <summary>What the robot session logs, for the Journal tab; it reaches the application's log as well.</summary>
    public event Action<string>? Log;

    private RobotContext(DysonCloudClient api, StoredSession stored, ILoggerFactory? loggers)
    {
        Api = api;
        Stored = stored;
        Loggers = loggers ?? NullLoggerFactory.Instance;
    }

    /// <summary>Opens the stored session, or returns null when the user has to log in.</summary>
    public static RobotContext? FromStoredSession(ILoggerFactory? loggers = null)
    {
        var s = SessionStore.Load();
        if (s is null) return null;
        loggers ??= NullLoggerFactory.Instance;
        var api = new DysonCloudClient(s.Country, s.Culture, logger: loggers.CreateLogger<DysonCloudClient>()) { BearerToken = s.Token };
        return new RobotContext(api, s, loggers);
    }

    public static RobotContext FromLogin(DysonCloudClient api, StoredSession stored, ILoggerFactory? loggers = null) => new(api, stored, loggers);

    /// <summary>Loads the account's devices and picks the first robot, unless a serial is preferred.</summary>
    public async Task<Device?> LoadDevicesAsync(string? preferredSerial = null, CancellationToken ct = default)
    {
        Devices = await Api.GetManifestAsync(ct);
        Robot = Devices.FirstOrDefault(d => preferredSerial is not null && d.SerialNumber.Equals(preferredSerial, StringComparison.OrdinalIgnoreCase))
                ?? Devices.FirstOrDefault(d => string.Equals(d.Category, "robot", StringComparison.OrdinalIgnoreCase))
                ?? Devices.FirstOrDefault();
        return Robot;
    }

    /// <param name="time">The clock the session waits on between reconnection attempts.</param>
    public async Task<RobotSession> ConnectAsync(TimeProvider? time = null, CancellationToken ct = default)
    {
        if (Robot is null) throw new InvalidOperationException("No robot selected.");
        if (Session is not null) return Session;
        var session = new RobotSession(Api, Robot, new JournalLogger(Loggers.CreateLogger<RobotSession>(), m => Log?.Invoke(m)), time);
        try
        {
            await session.ConnectAsync(ct);
        }
        catch
        {
            // Not kept: the next attempt starts a fresh one.
            await session.DisposeAsync();
            throw;
        }
        Session = session;
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (Session is not null) await Session.DisposeAsync();
        Session = null;
        Api.Dispose();
    }
}
