using DyssCockpit.Core;

namespace DyssCockpit.Cli.Diagnostics;

/// <summary>
/// The broker reached with the temporary IAM credentials of /v1/authorize/iot-role-credentials,
/// over a SigV4-presigned WebSocket. A dead end kept for the record (see docs/protocole.md): it
/// was observed to grant the connection only, neither subscribing nor publishing, so only the
/// command line offers it, with <c>--sigv4</c>.
/// </summary>
internal static class RoleCredentials
{
    public static MqttEndpoint Endpoint(IotRoleData role, string? clientId = null) =>
        new(role.Endpoint, clientId ?? Guid.NewGuid().ToString(), "sigv4",
            WebSocketUrl: AwsSigV4.PresignWebSocketUrl(role.Endpoint, role.Region,
                role.IamCredentials.AccessKeyId, role.IamCredentials.SecretAccessKey, role.IamCredentials.SessionToken));
}
