using System.Text.Json.Nodes;

namespace DyssCockpit.TestSupport;

internal static class JsonAssert
{
    /// <summary>Asserts <paramref name="actual"/> is the JSON written in <paramref name="expected"/>: compared as JSON, so key order and spacing do not matter.</summary>
    public static void Equal(string expected, JsonNode? actual, string? what = null)
    {
        var want = JsonNode.Parse(expected);
        Assert.True(JsonNode.DeepEquals(want, actual),
            $"{(what is null ? "" : what + ": ")}expected {want?.ToJsonString()}\n but got {actual?.ToJsonString()}");
    }
}
