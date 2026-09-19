using MyDyson.Core;

namespace MyDyson.Core.Tests;

public class AwsSigV4Tests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 9, 19, 11, 18, 14, TimeSpan.Zero);

    [Fact]
    public void QueryParametersAreSortedAndTokenComesAfterTheSignature()
    {
        var url = AwsSigV4.PresignWebSocketUrl("h.iot.eu-west-1.amazonaws.com", "eu-west-1", "AKID", "secret", "SESSION/TOKEN+", FixedTime);

        var query = new Uri(url).Query.TrimStart('?').Split('&');
        Assert.Equal("X-Amz-Algorithm=AWS4-HMAC-SHA256", query[0]);
        Assert.Equal("X-Amz-Credential=AKID%2F20260919%2Feu-west-1%2Fiotdevicegateway%2Faws4_request", query[1]);
        Assert.Equal("X-Amz-Date=20260919T111814Z", query[2]);
        Assert.Equal("X-Amz-SignedHeaders=host", query[3]);
        Assert.StartsWith("X-Amz-Signature=", query[4]);
        Assert.Equal("X-Amz-Security-Token=SESSION%2FTOKEN%2B", query[5]);
        Assert.Equal(64, query[4].Length - "X-Amz-Signature=".Length);
    }

    [Fact]
    public void SignatureIsDeterministicAndDoesNotDependOnTheSessionToken()
    {
        // The token is appended after signing, so it must not change the signature.
        var a = AwsSigV4.PresignWebSocketUrl("h", "eu-west-1", "AKID", "secret", "token-a", FixedTime);
        var b = AwsSigV4.PresignWebSocketUrl("h", "eu-west-1", "AKID", "secret", "token-b", FixedTime);
        Assert.Equal(Signature(a), Signature(b));
    }

    [Fact]
    public void KnownAnswer()
    {
        // Frozen output of this implementation, verified against AWS IoT with an HTTP 101 on the
        // handshake on 2026-09-19. Guards against accidental changes to the canonical request.
        var url = AwsSigV4.PresignWebSocketUrl("h.iot.eu-west-1.amazonaws.com", "eu-west-1", "AKID", "secret", null, FixedTime);
        var actual = Signature(url);
        Assert.True(actual == KnownSignature, $"signature changed: {actual}");
    }

    // Computed once by this implementation; see KnownAnswer for what it protects.
    private const string KnownSignature = "ed3c69b21fadcd1c96bcd1029f2638caf1f48ac9ee467da17ecbf5f512fac67e";

    private static string Signature(string url) =>
        new Uri(url).Query.Split('&').First(p => p.StartsWith("X-Amz-Signature=")).Split('=')[1];
}
