using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DyssCockpit.Core;

/// <summary>
/// Minimal AWS Signature Version 4 presigner for the AWS IoT MQTT-over-WebSocket endpoint.
/// Implemented here rather than pulling in the AWS SDK, which would only be used for this URL.
/// </summary>
public static class AwsSigV4
{
    private const string Service = "iotdevicegateway";
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Builds the presigned wss:// URL used to open an MQTT connection to AWS IoT.
    /// The session token is deliberately appended after signing: AWS IoT expects
    /// X-Amz-Security-Token outside the canonical query string.
    /// </summary>
    public static string PresignWebSocketUrl(
        string endpoint,
        string region,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken = null,
        DateTimeOffset? now = null)
    {
        var stamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = stamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var credentialScope = $"{dateStamp}/{region}/{Service}/aws4_request";

        // Query parameters must be sorted by name; these already are.
        var canonicalQuery = string.Join("&",
            $"X-Amz-Algorithm={Algorithm}",
            $"X-Amz-Credential={Uri.EscapeDataString($"{accessKeyId}/{credentialScope}")}",
            $"X-Amz-Date={amzDate}",
            "X-Amz-SignedHeaders=host");

        var canonicalRequest = string.Join("\n",
            "GET",
            "/mqtt",
            canonicalQuery,
            $"host:{endpoint}",
            "",
            "host",
            Hex(Sha256(Array.Empty<byte>())));

        var stringToSign = string.Join("\n",
            Algorithm,
            amzDate,
            credentialScope,
            Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = SigningKey(secretAccessKey, dateStamp, region);
        var signature = Hex(HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        var url = $"wss://{endpoint}/mqtt?{canonicalQuery}&X-Amz-Signature={signature}";
        if (!string.IsNullOrEmpty(sessionToken))
            url += $"&X-Amz-Security-Token={Uri.EscapeDataString(sessionToken)}";
        return url;
    }

    private static byte[] SigningKey(string secretAccessKey, string dateStamp, string region)
    {
        var key = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        key = HmacSha256(key, Encoding.UTF8.GetBytes(dateStamp));
        key = HmacSha256(key, Encoding.UTF8.GetBytes(region));
        key = HmacSha256(key, Encoding.UTF8.GetBytes(Service));
        return HmacSha256(key, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    private static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
