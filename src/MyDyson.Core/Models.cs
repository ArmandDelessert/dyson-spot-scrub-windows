using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyDyson.Core;

/// <summary>Response of POST /v3/userregistration/email/userstatus.</summary>
public sealed record UserStatus(
    [property: JsonPropertyName("accountStatus")] string? AccountStatus,
    [property: JsonPropertyName("authenticationMethod")] string? AuthenticationMethod);

/// <summary>Response of POST /v3/userregistration/email/auth.</summary>
public sealed record LoginChallenge(
    [property: JsonPropertyName("challengeId")] string ChallengeId);

/// <summary>Response of POST /v3/userregistration/email/verify.</summary>
public sealed record LoginResult(
    [property: JsonPropertyName("account")] string? Account,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("tokenType")] string? TokenType);

public sealed record Firmware(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("autoUpdateEnabled")] bool? AutoUpdateEnabled,
    [property: JsonPropertyName("newVersionAvailable")] bool? NewVersionAvailable,
    [property: JsonPropertyName("capabilities")] List<string>? Capabilities);

public sealed record MqttConfiguration(
    [property: JsonPropertyName("localBrokerCredentials")] string? LocalBrokerCredentials,
    [property: JsonPropertyName("mqttRootTopicLevel")] string? MqttRootTopicLevel,
    [property: JsonPropertyName("remoteBrokerType")] string? RemoteBrokerType);

public sealed record ConnectedConfiguration(
    [property: JsonPropertyName("firmware")] Firmware? Firmware,
    [property: JsonPropertyName("mqtt")] MqttConfiguration? Mqtt);

/// <summary>One entry of the account manifest (GET /v3/manifest).</summary>
public sealed record Device(
    [property: JsonPropertyName("serialNumber")] string SerialNumber,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("variant")] string? Variant,
    [property: JsonPropertyName("connectionCategory")] string? ConnectionCategory,
    [property: JsonPropertyName("connectedConfiguration")] ConnectedConfiguration? ConnectedConfiguration)
{
    /// <summary>Anything the server sent that we do not model explicitly.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// MQTT topic prefix actually used by the robot. For the Spot+Scrub AI the manifest
    /// value is unreliable; the robot publishes under the first four characters of its
    /// firmware version (e.g. "RB05PR.01.109..." -> "RB05").
    /// </summary>
    public string GuessTopicPrefix()
    {
        var fw = ConnectedConfiguration?.Firmware?.Version;
        if (!string.IsNullOrEmpty(fw) && fw.Length >= 4 && fw.StartsWith("RB", StringComparison.OrdinalIgnoreCase))
            return fw[..4];
        var root = ConnectedConfiguration?.Mqtt?.MqttRootTopicLevel;
        if (!string.IsNullOrEmpty(root))
            return root;
        return Type ?? "RB05";
    }
}

/// <summary>Inner object of the /v2/authorize/iot-credentials response.</summary>
public sealed record IotCredentialsInner(
    [property: JsonPropertyName("ClientId")] string ClientId,
    [property: JsonPropertyName("CustomAuthorizerName")] string? CustomAuthorizerName,
    [property: JsonPropertyName("TokenKey")] string? TokenKey,
    [property: JsonPropertyName("TokenValue")] string TokenValue,
    [property: JsonPropertyName("TokenSignature")] string TokenSignature);

/// <summary>Response of POST /v2/authorize/iot-credentials.</summary>
public sealed record IotData(
    [property: JsonPropertyName("Endpoint")] string Endpoint,
    [property: JsonPropertyName("IoTCredentials")] IotCredentialsInner IoTCredentials)
{
    public const string DefaultAuthorizerName = "cld-iot-credentials-lambda-authorizer";

    public string AuthorizerName =>
        string.IsNullOrEmpty(IoTCredentials.CustomAuthorizerName)
            ? DefaultAuthorizerName
            : IoTCredentials.CustomAuthorizerName;

    /// <summary>WebSocket URL for AWS IoT with the custom authorizer parameters in the query string.</summary>
    public string BuildWebSocketUri() =>
        $"wss://{Endpoint}/mqtt" +
        $"?x-amz-customauthorizer-name={Uri.EscapeDataString(AuthorizerName)}" +
        $"&token={Uri.EscapeDataString(IoTCredentials.TokenValue)}" +
        $"&x-amz-customauthorizer-signature={Uri.EscapeDataString(IoTCredentials.TokenSignature)}";
}

/// <summary>Temporary IAM credentials, inner object of the /v1/authorize/iot-role-credentials response.</summary>
public sealed record IamCredentials(
    [property: JsonPropertyName("accessKeyId")] string AccessKeyId,
    [property: JsonPropertyName("secretAccessKey")] string SecretAccessKey,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("expiration")] DateTimeOffset? Expiration);

/// <summary>
/// Response of POST /v1/authorize/iot-role-credentials: the credentials the current MyDyson app
/// uses for remote control. Unlike the custom-authorizer token from /v2/authorize/iot-credentials,
/// these allow publishing to the robot command topics.
/// </summary>
public sealed record IotRoleData(
    [property: JsonPropertyName("iamCredentials")] IamCredentials IamCredentials,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("region")] string Region)
{
    /// <summary>Presigned AWS IoT WebSocket URL (SigV4).</summary>
    public string BuildWebSocketUri() =>
        AwsSigV4.PresignWebSocketUrl(
            Endpoint, Region,
            IamCredentials.AccessKeyId,
            IamCredentials.SecretAccessKey,
            IamCredentials.SessionToken);
}

/// <summary>How to reach the AWS IoT broker: a presigned WebSocket URL plus the MQTT client id.</summary>
public sealed record MqttEndpoint(string WebSocketUrl, string ClientId, string Endpoint, string AuthMode)
{
    public static MqttEndpoint FromRoleCredentials(IotRoleData role, string? clientId = null) =>
        new(role.BuildWebSocketUri(), clientId ?? Guid.NewGuid().ToString(), role.Endpoint, "sigv4");

    public static MqttEndpoint FromCustomAuthorizer(IotData iot) =>
        new(iot.BuildWebSocketUri(), iot.IoTCredentials.ClientId, iot.Endpoint, "custom-authorizer");
}

/// <summary>Persisted session (bearer token + context), stored encrypted on disk.</summary>
public sealed record StoredSession(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("culture")] string Culture,
    [property: JsonPropertyName("account")] string? Account,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);
