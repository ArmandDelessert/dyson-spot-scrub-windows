using Dyss.Core;

namespace Dyss.App.Services;

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

    public event Action<string>? Log;

    private RobotContext(DysonCloudClient api, StoredSession stored)
    {
        Api = api;
        Stored = stored;
    }

    /// <summary>Opens the stored session, or returns null when the user has to log in.</summary>
    public static RobotContext? FromStoredSession()
    {
        var s = SessionStore.Load();
        if (s is null) return null;
        var api = new DysonCloudClient(s.Country, s.Culture) { BearerToken = s.Token };
        return new RobotContext(api, s);
    }

    public static RobotContext FromLogin(DysonCloudClient api, StoredSession stored) => new(api, stored);

    /// <summary>Loads the account's devices and picks the first robot, unless a serial is preferred.</summary>
    public async Task<Device?> LoadDevicesAsync(string? preferredSerial = null, CancellationToken ct = default)
    {
        Devices = await Api.GetManifestAsync(ct);
        Robot = Devices.FirstOrDefault(d => preferredSerial is not null && d.SerialNumber.Equals(preferredSerial, StringComparison.OrdinalIgnoreCase))
                ?? Devices.FirstOrDefault(d => string.Equals(d.Category, "robot", StringComparison.OrdinalIgnoreCase))
                ?? Devices.FirstOrDefault();
        return Robot;
    }

    public async Task<RobotSession> ConnectAsync(CancellationToken ct = default)
    {
        if (Robot is null) throw new InvalidOperationException("No robot selected.");
        if (Session is not null) return Session;
        var session = new RobotSession(Api, Robot, m => Log?.Invoke(m));
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
