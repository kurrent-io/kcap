using System.Text.Json.Nodes;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Compares a JSON object's properties token for token, where a substring check would let
/// <c>"seq":7</c> pass for <c>"seq":70</c>.
/// </summary>
public static class WireJson {
    /// Expected for a property the object must not have.
    public const string Missing = "(missing)";

    /// <summary>
    /// <paramref name="json"/> cut down to the properties <paramref name="expected"/> names, in its order
    /// and serialized the same way, so the result equals <c>expected.ToJsonString()</c> exactly when
    /// each one matches. A nested object, and an array as long as the expected one, is cut down the
    /// same way. A property the object lacks reads <see cref="Missing"/>, so a missing property and a
    /// <c>null</c> one differ; one it has, where <see cref="Missing"/> is expected, reads
    /// <c>{"present":value}</c>, which no value can pass for.
    /// </summary>
    public static string Pick(string json, JsonObject expected) => Cut(JsonNode.Parse(json), expected)!.ToJsonString();

    static JsonNode? Cut(JsonNode? actual, JsonNode? expected) => (actual, expected) switch {
        (JsonObject a, JsonObject e)                       => CutObject(a, e),
        (JsonArray a, JsonArray e) when a.Count == e.Count => new JsonArray(a.Select((item, i) => Cut(item, e[i])).ToArray()),
        _                                                  => actual?.DeepClone()
    };

    static JsonObject CutObject(JsonObject actual, JsonObject expected) {
        var picked = new JsonObject();
        foreach (var (name, value) in expected)
            picked[name] = !actual.TryGetPropertyValue(name, out var found) ? Missing
                : IsMissing(value)                                         ? new JsonObject { ["present"] = found?.DeepClone() }
                                                                           : Cut(found, value);

        return picked;
    }

    static bool IsMissing(JsonNode? expected) => expected is JsonValue value && value.TryGetValue(out string? text) && text == Missing;
}
