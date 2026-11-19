using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Harness.GrokBot;

/// <summary>
/// Projects a Grok Bot chat into canonical events. <c>kcap grok-bot watch</c> posts one gateway transcript
/// entry per line, with <c>agentId</c> added. The transcript is the conversation only: a user message is a
/// prompt, and a Bot message a reply — its text, or a card (a question with its options, a connector or
/// source-control request, a permission ask) rendered as text. An answered question also yields the answer
/// as a prompt. Any other shape yields nothing.
/// </summary>
public sealed class GrokBotTranscriptEvents : ITranscriptProjection {
    public static readonly GrokBotTranscriptEvents Instance = new();

    static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    GrokBotTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new GrokBotContext(agentId is { Length: > 0 } ? $"{sessionId}:{agentId}" : sessionId);

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var ctx = (GrokBotContext)context;
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var entry = doc.RootElement;
            if (!entry.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (ctx.IdScope.Length == 0 || entry.Str("id") is not { Length: > 0 } entryId) return ProjectionResult.Empty;

            // An instant out of range costs the entry its own time, never the entry itself.
            var ms = entry.Num("timestampMs") is { } n && n >= MinUnixMs && n <= MaxUnixMs ? n : (long?)null;
            var at = ms is { } own ? DateTimeOffset.FromUnixTimeMilliseconds(own) : receivedAt;

            var messages = entry.Str("kind") switch {
                "message" when entry.Str("role") == "user" && entry.Str("content") is { Length: > 0 } text => [(entryId, true, text)],
                "send-message" when entry.Obj("message") is { } message                                     => Bot(entry, message, entryId),
                _                                                                                           => [],
            };

            var events = new List<CanonicalEvent>(messages.Count);

            // One tick apart, so an answer sorts after the question it shares an entry with.
            foreach (var (key, fromUser, content) in messages) {
                var stamp = Timestamp.FromDateTimeOffset(at.AddTicks(events.Count));
                IMessage payload = fromUser
                    ? new UserMessageReceived { Content = content, Timestamp = stamp }
                    : new AssistantTextGenerated { Content = content, Timestamp = stamp };
                var type = CanonicalEventTypes.Of(payload);

                events.Add(new CanonicalEvent(type, payload, TranscriptIds.GrokBotEntry(ctx.IdScope, key, type), at.AddTicks(events.Count), ms is null ? null : at.ToString("O")));
            }

            return ProjectionResult.Of(events);
        }
    }

    static List<(string Key, bool FromUser, string Content)> Bot(JsonElement entry, JsonElement message, string entryId) {
        switch (message.Str("type")) {
            case "text" when message.Str("content") is { Length: > 0 } text:
                return [(entryId, false, text)];

            case "widget" when message.Obj("widget") is { } widget && Question(widget) is { Length: > 0 } question:
                return entry.Str("respondedValue") is { Length: > 0 } answer
                    ? [(entryId, false, question), (entryId + ":response", true, answer)]
                    : [(entryId, false, question)];

            case "connector" when message.Str("connector") is { Length: > 0 } connector:
                return [(entryId, false, Request(connector, message.Str("variant"), "connector"))];

            case "scm-connect" when message.Str("provider") is { Length: > 0 } provider:
                return [(entryId, false, Request(provider, message.Str("intent"), "source-control"))];

            case "local-tool-permission" when message.Obj("ask") is { } ask && ask.Str("action") is { Length: > 0 } action:
                var target = ask.Str("target") is { Length: > 0 } t ? $" {t}" : "";

                return [(entryId, false, $"Asked permission to {action}{target} on the user's computer: {ask.Str("status") ?? "unanswered"}.")];

            default:
                return [];
        }
    }

    static string Request(string name, string? variant, string card) =>
        variant is "connect" or null ? $"Asked to connect {name}." : $"Showed the {name} {card} card ({variant}).";

    static string Question(JsonElement widget) {
        var text = new StringBuilder(widget.Str("prompt") ?? "");

        if (widget.Arr("options") is { } options) {
            foreach (var option in options.EnumerateArray()) {
                if (option.Str("label") is { Length: > 0 } label) text.Append("\n- ").Append(label);
            }
        }

        return text.ToString().Trim();
    }
}
