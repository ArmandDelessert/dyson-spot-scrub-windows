using System.Text.Json.Nodes;
using DyssCockpit.Core;

namespace DyssCockpit.TestSupport;

/// <summary>
/// A robot that only records what it is sent, in order, and answers every request with the same
/// reply. Shared by the Core and the Presentation tests (the latter link this file), so that what the
/// view models send can be checked against the captures exactly as the commands themselves are.
/// </summary>
internal sealed class RecordingRobot(string reply = """{"code":0,"data":{"result":0}}""") : IRobotCommands
{
    /// <summary>How a message went out: a classic command, a jdm message sent without waiting, or a jdm request that waits for the reply.</summary>
    public enum Channel { Command, Jdm, Request }

    /// <param name="Method">The jdm method, or the classic command's <c>msg</c>.</param>
    /// <param name="Payload">The jdm parameters, or the whole classic command.</param>
    public sealed record Message(Channel Via, string Method, JsonNode Payload);

    public List<Message> Sent { get; } = [];

    public Task<JsonObject> RequestJdmAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Sent.Add(new Message(Channel.Request, method, parameters?.DeepClone() ?? new JsonObject()));
        return Task.FromResult((JsonObject)JsonNode.Parse(reply)!);
    }

    public Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        Sent.Add(new Message(Channel.Jdm, method, parameters?.DeepClone() ?? new JsonObject()));
        return Task.CompletedTask;
    }

    public Task PublishCommandAsync(JsonObject payload, CancellationToken ct = default)
    {
        Sent.Add(new Message(Channel.Command, payload["msg"]!.GetValue<string>(), payload.DeepClone()));
        return Task.CompletedTask;
    }

    /// <summary>Asserts the messages sent are these, in this order, each JSON compared as JSON (key order does not matter).</summary>
    public void AssertSent(params (string Method, string Payload)[] expected)
    {
        Assert.Equal(expected.Length, Sent.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Method, Sent[i].Method);
            var want = JsonNode.Parse(expected[i].Payload);
            Assert.True(JsonNode.DeepEquals(want, Sent[i].Payload), $"message {i} ({Sent[i].Method}): expected {want!.ToJsonString()}\n but got {Sent[i].Payload.ToJsonString()}");
        }
    }
}
