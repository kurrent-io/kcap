using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>One gateway transcript entry, with what the sessionizer needs to decide whether it may be
/// sent yet. <see cref="StartsTurn"/> marks a message from the user, which leaves an unanswered question
/// before it behind. <see cref="Line"/> is the entry verbatim plus the owning <c>agentId</c>.</summary>
public sealed record GrokBotEntry(
        string Id,
        int    Seq,
        long   TimestampMs,
        bool   IsStreaming,
        bool   IsAwaitingAnswer,
        bool   StartsTurn,
        string Line
    ) {
    static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>Null for an entry with no id, order or usable time: it cannot be placed in a session, and an
    /// instant no date can hold would fail every later call that formats it.</summary>
    public static GrokBotEntry? TryParse(JsonElement entry, string agentId) {
        if (entry.Str("id") is not { Length: > 0 } id) return null;
        if (entry.Num("seq") is not { } seq || seq is < int.MinValue or > int.MaxValue) return null;
        if (entry.Num("timestampMs") is not { } timestampMs || timestampMs < MinUnixMs || timestampMs > MaxUnixMs) return null;

        var awaiting = entry.Obj("message") is { } message && (IsUnansweredWidget(entry, message) || IsUnansweredPermission(message));

        var node = JsonNode.Parse(entry.GetRawText())!.AsObject();
        node["agentId"] = agentId;

        return new GrokBotEntry(
            id, (int)seq, timestampMs, entry.Bool("isStreaming") == true, awaiting,
            entry.Str("kind") == "message" && entry.Str("role") == "user",
            node.ToJsonString());
    }

    // A null answer is no answer: a gateway may write the field before the user has chosen.
    static bool IsUnansweredWidget(JsonElement entry, JsonElement message) =>
        message.Str("type") == "widget" && entry.Prop("respondedValue") is not { IsNull: false } && entry.Bool("widgetSkipped") != true;

    // An answered ask carries its outcome ("allowed", ...); anything else is still waiting on the user.
    static bool IsUnansweredPermission(JsonElement message) =>
        message.Str("type") == "local-tool-permission"
     && message.Obj("ask")?.Str("status") is not { Length: > 0 } or "pending" or "requested";
}
