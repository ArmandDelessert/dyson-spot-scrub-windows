using System.Net.WebSockets;
using System.Text;

namespace MyDyson.Core;

/// <summary>
/// Opens a WebSocket to AWS IoT and performs an MQTT 3.1.1 CONNECT by hand, so the WebSocket
/// handshake and the MQTT authorization can be told apart. AWS IoT closes the connection without a
/// CONNACK when the IAM policy denies iot:Connect for the client id, which higher level libraries
/// surface only as a generic "disconnected".
/// </summary>
public static class RawMqttProbe
{
    public sealed record Result(bool WebSocketConnected, string? WebSocketError, byte[]? ConnAck, string? MqttError)
    {
        public string Describe()
        {
            if (!WebSocketConnected)
                return $"handshake WebSocket ÉCHOUÉ: {WebSocketError}";
            if (ConnAck is { Length: >= 4 })
            {
                var code = ConnAck[3];
                var meaning = code switch
                {
                    0x00 => "connexion acceptée",
                    0x01 => "version de protocole refusée",
                    0x02 => "identifiant client refusé",
                    0x03 => "serveur indisponible",
                    0x04 => "identifiants invalides",
                    0x05 => "non autorisé",
                    _ => "code inconnu",
                };
                return $"handshake WebSocket OK, CONNACK reçu: 0x{code:X2} ({meaning})";
            }
            return $"handshake WebSocket OK, aucun CONNACK: {MqttError ?? "connexion fermée par le serveur"}";
        }
    }

    /// <summary>
    /// After a successful CONNECT, publishes on <paramref name="publishTopic"/> and reports whether the
    /// broker kept the connection open. A close right after the PUBLISH means the policy denies it.
    /// </summary>
    public static async Task<string> TryPublishAsync(string webSocketUrl, string clientId, string publishTopic, string payload, CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("mqtt");
        try
        {
            await ws.ConnectAsync(new Uri(webSocketUrl), ct).ConfigureAwait(false);
            await ws.SendAsync(BuildConnectPacket(clientId), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);

            var buffer = new byte[64];
            using var connackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connackTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var ack = await ws.ReceiveAsync(buffer, connackTimeout.Token).ConfigureAwait(false);
            if (ack.MessageType == WebSocketMessageType.Close || ack.Count < 4 || buffer[3] != 0x00)
                return "CONNECT refusé, publication non testée";

            await ws.SendAsync(BuildPublishPacket(publishTopic, payload), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);

            using var after = CancellationTokenSource.CreateLinkedTokenSource(ct);
            after.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var next = await ws.ReceiveAsync(buffer, after.Token).ConfigureAwait(false);
                return next.MessageType == WebSocketMessageType.Close
                    ? $"PUBLISH REFUSÉ, connexion fermée ({next.CloseStatus})"
                    : $"connexion maintenue, {next.Count} octets reçus";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ws.State == WebSocketState.Open
                    ? "PUBLISH ACCEPTÉ (connexion toujours ouverte)"
                    : $"connexion fermée, état {ws.State}";
            }
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Builds an MQTT 3.1.1 PUBLISH packet, QoS 0, no retain.</summary>
    private static byte[] BuildPublishPacket(string topic, string payload)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var body = new List<byte>
        {
            (byte)(topicBytes.Length >> 8),
            (byte)(topicBytes.Length & 0xFF),
        };
        body.AddRange(topicBytes);
        body.AddRange(payloadBytes);

        var packet = new List<byte> { 0x30 };
        var remaining = body.Count;
        do
        {
            var b = (byte)(remaining % 128);
            remaining /= 128;
            if (remaining > 0) b |= 0x80;
            packet.Add(b);
        } while (remaining > 0);
        packet.AddRange(body);
        return packet.ToArray();
    }

    public static async Task<Result> TryConnectAsync(string webSocketUrl, string clientId, CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("mqtt");
        try
        {
            await ws.ConnectAsync(new Uri(webSocketUrl), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var chain = new List<string>();
            for (var e = ex; e is not null; e = e.InnerException) chain.Add($"{e.GetType().Name}: {e.Message}");
            return new Result(false, string.Join(" <- ", chain), null, null);
        }

        try
        {
            var connect = BuildConnectPacket(clientId);
            await ws.SendAsync(connect, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);

            var buffer = new byte[64];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var received = await ws.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);

            if (received.MessageType == WebSocketMessageType.Close)
                return new Result(true, null, null, $"fermeture WebSocket {received.CloseStatus}: {received.CloseStatusDescription}");

            return new Result(true, null, buffer[..received.Count], null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Result(true, null, null, "délai dépassé sans réponse");
        }
        catch (Exception ex)
        {
            return new Result(true, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Builds a minimal MQTT 3.1.1 CONNECT packet: clean session, 30 s keep-alive, no credentials.</summary>
    private static byte[] BuildConnectPacket(string clientId)
    {
        var id = Encoding.UTF8.GetBytes(clientId);
        var variableHeader = new List<byte> { 0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x04, 0x02, 0x00, 0x1E };
        variableHeader.Add((byte)(id.Length >> 8));
        variableHeader.Add((byte)(id.Length & 0xFF));
        variableHeader.AddRange(id);

        var packet = new List<byte> { 0x10 };
        var remaining = variableHeader.Count;
        do
        {
            var b = (byte)(remaining % 128);
            remaining /= 128;
            if (remaining > 0) b |= 0x80;
            packet.Add(b);
        } while (remaining > 0);
        packet.AddRange(variableHeader);
        return packet.ToArray();
    }
}
