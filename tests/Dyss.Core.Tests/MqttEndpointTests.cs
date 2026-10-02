using Dyss.Core;

namespace Dyss.Core.Tests;

public class MqttEndpointTests
{
    private static IotData SampleIot(string signature = "abc+/=", string clientId = "client-1") => new(
        "host.iot.eu-west-1.amazonaws.com",
        new IotCredentialsInner(clientId, "cld-iot-credentials-lambda-authorizer", "token", "token-value", signature));

    [Fact]
    public void TlsModeBuildsTheUsernameTheAppUses()
    {
        var endpoint = MqttEndpoint.FromCustomAuthorizerTls(SampleIot());

        Assert.False(endpoint.UsesWebSocket);
        Assert.Equal("client-1", endpoint.ClientId);
        Assert.Equal("host.iot.eu-west-1.amazonaws.com", endpoint.Endpoint);
        // Order and separators as produced by AwsIotMqttConnectionBuilder in the decompiled app.
        Assert.Equal(
            "?x-amz-customauthorizer-name=cld-iot-credentials-lambda-authorizer" +
            "&x-amz-customauthorizer-signature=abc%2B%2F%3D" +
            "&token=token-value",
            endpoint.Username);
    }

    [Fact]
    public void AlreadyEncodedSignatureIsNotEncodedTwice()
    {
        var endpoint = MqttEndpoint.FromCustomAuthorizerTls(SampleIot(signature: "abc%2B%2F%3D"));
        Assert.Contains("signature=abc%2B%2F%3D&", endpoint.Username);
    }

    [Fact]
    public void EmptyClientIdFallsBackToAGuid()
    {
        var endpoint = MqttEndpoint.FromCustomAuthorizerTls(SampleIot(clientId: ""));
        Assert.True(Guid.TryParse(endpoint.ClientId, out _));
    }

    [Fact]
    public void WebSocketModePutsTheParametersInTheQueryString()
    {
        var endpoint = MqttEndpoint.FromCustomAuthorizerWebSocket(SampleIot());

        Assert.True(endpoint.UsesWebSocket);
        Assert.StartsWith("wss://host.iot.eu-west-1.amazonaws.com/mqtt?x-amz-customauthorizer-name=", endpoint.WebSocketUrl);
        Assert.Contains("&token=token-value&x-amz-customauthorizer-signature=abc%2B%2F%3D", endpoint.WebSocketUrl);
    }
}
