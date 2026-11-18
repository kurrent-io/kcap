using Capacitor.Models.Transcripts.Harness.MuseAssistant;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.MuseAssistant;

public class MuseAssistantTranscriptEventsTests {
    const string Session = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    const string Turn    = "7c1e9d2a-0b3f-4e55-8a61-2f9d0c4b7e10";

    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Occurred = DateTimeOffset.FromUnixTimeMilliseconds(1791532805000);

    static ProjectionResult P(string line, string session = Session, string? agentId = null) =>
        MuseAssistantTranscriptEvents.Instance.Project(line, 1, Received, MuseAssistantTranscriptEvents.Instance.CreateContext(session, agentId));

    static string Item(string role = "user", string content = "hi", long seq = 12, string occurredAtMs = "1791532805000") =>
        $$"""{"chat_id":"chat-main","turn_id":"{{Turn}}","role":"{{role}}","seq":{{seq}},"occurred_at_ms":{{occurredAtMs}},"content":"{{content}}"}""";

    static (string Content, Timestamp At) Message(object payload) => payload switch {
        UserMessageReceived u        => (u.Content, u.Timestamp),
        AssistantTextGenerated a     => (a.Content, a.Timestamp),
        AssistantThinkingGenerated t => (t.Content, t.Timestamp),
        _                            => throw new ArgumentException(payload.GetType().Name, nameof(payload)),
    };

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("\"a string\"")]
    public async Task A_line_that_is_not_a_json_object_is_rejected(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNotNull();
        await Assert.That(result.Events.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("user", CanonicalEventTypes.UserMessageReceived)]
    [Arguments("assistant", CanonicalEventTypes.AssistantTextGenerated)]
    [Arguments("commentary", CanonicalEventTypes.AssistantThinkingGenerated)]
    public async Task A_conversation_item_is_one_event_on_its_scope_turn_and_seq_at_its_own_time(string role, string type) {
        var events = P(Item(role, "Plan my week")).Events;

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo(type);
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.MuseAssistantItem(Session, Turn, 12));
        await Assert.That(events[0].Timestamp).IsEqualTo(Occurred);
        await Assert.That(events[0].RecordTimestamp).IsEqualTo("2026-10-09T08:00:05.0000000+00:00");
        await Assert.That(Message(events[0].Payload)).IsEqualTo(("Plan my week", Timestamp.FromDateTimeOffset(Occurred)));
    }

    [Test]
    [Arguments("""{"chat_id":"c","turn_id":"t","role":"developer","seq":1,"occurred_at_ms":1,"content":"You are Muse."}""")]
    [Arguments("""{"chat_id":"c","turn_id":"t","role":"system_note","seq":1,"occurred_at_ms":1,"content":"unknown role"}""")]
    [Arguments("""{"chat_id":"c","turn_id":"t","seq":1,"occurred_at_ms":1,"content":"no role"}""")]
    [Arguments("""{"chat_id":"c","role":"user","seq":1,"occurred_at_ms":1,"content":"no turn id"}""")]
    [Arguments("""{"chat_id":"c","turn_id":"","role":"user","seq":1,"occurred_at_ms":1,"content":"empty turn id"}""")]
    [Arguments("""{"chat_id":"c","turn_id":"t","role":"user","occurred_at_ms":1,"content":"no seq"}""")]
    [Arguments("""{"chat_id":"c","turn_id":"t","role":"user","seq":"1","occurred_at_ms":1,"content":"string seq"}""")]
    [Arguments("""{"chat_id":"c","turn_id":"t","role":"user","seq":1.5,"occurred_at_ms":1,"content":"fractional seq"}""")]
    [Arguments("""{"chat_id":"c","turn_id":"t","role":"user","seq":1,"occurred_at_ms":1,"content":""}""")]
    [Arguments("""{"chat_id":"c","turn_id":"t","role":"user","seq":1,"occurred_at_ms":1,"content":["not","a","string"]}""")]
    public async Task An_item_outside_the_conversation_or_missing_a_key_emits_nothing(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_item_without_a_time_takes_the_receive_instant_and_no_record_time() {
        var events = P($$"""{"chat_id":"c","turn_id":"{{Turn}}","role":"user","seq":12,"content":"hi"}""").Events;

        await Assert.That(events[0].Timestamp).IsEqualTo(Received);
        await Assert.That(events[0].RecordTimestamp).IsNull();
        await Assert.That(Message(events[0].Payload).At).IsEqualTo(Timestamp.FromDateTimeOffset(Received));
    }

    [Test]
    [Arguments("9223372036854775807")]
    [Arguments("-9223372036854775808")]
    [Arguments("253402300800000")]
    [Arguments("-62135596800001")]
    [Arguments("\"1791532805000\"")]
    public async Task An_unusable_time_keeps_the_item_at_the_receive_instant(string occurredAtMs) {
        var events = P(Item(occurredAtMs: occurredAtMs)).Events;

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Timestamp).IsEqualTo(Received);
        await Assert.That(events[0].RecordTimestamp).IsNull();
        await Assert.That(Message(events[0].Payload).Content).IsEqualTo("hi");
    }

    [Test]
    public async Task The_id_depends_on_the_scope_turn_and_seq_alone() {
        var id = P(Item()).Events[0].EventId;

        await Assert.That(P(Item("assistant", "other text", occurredAtMs: "1")).Events[0].EventId).IsEqualTo(id);
        await Assert.That(P(Item(seq: 13)).Events[0].EventId).IsNotEqualTo(id);
        await Assert.That(P(Item(), session: "ffffffffffffffffffffffffffffffff").Events[0].EventId).IsNotEqualTo(id);
    }

    [Test]
    public async Task A_subagent_stream_scopes_its_ids_by_session_and_agent() {
        await Assert.That(P(Item(), agentId: "agent-1").Events[0].EventId).IsEqualTo(TranscriptIds.MuseAssistantItem($"{Session}:agent-1", Turn, 12));
        await Assert.That(P(Item(), agentId: "").Events[0].EventId).IsEqualTo(TranscriptIds.MuseAssistantItem(Session, Turn, 12));
    }

    [Test]
    public async Task A_stream_with_no_session_emits_nothing() {
        var result = P(Item(), session: "");

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events.Count).IsEqualTo(0);
    }
}
