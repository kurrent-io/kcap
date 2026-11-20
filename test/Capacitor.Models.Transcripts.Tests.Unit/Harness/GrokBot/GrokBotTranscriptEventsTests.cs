using System.Runtime.CompilerServices;
using Capacitor.Models.Transcripts.Harness.GrokBot;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using TUnit.Assertions.Enums;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.GrokBot;

/// <summary>Pins the Grok Bot projection against gateway entry shapes captured from real chats, content
/// rewritten: user messages, Bot text, an answered and a skipped question, a desktop hand-off, a connector
/// card, a source-control card, a permission ask, a message between two Bots and a shared file.</summary>
public class GrokBotTranscriptEventsTests {
    const string Session = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    static readonly DateTimeOffset Received = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    static ProjectionResult P(string line, string session = Session, string? agentId = null) =>
        GrokBotTranscriptEvents.Instance.Project(line, 1, Received, GrokBotTranscriptEvents.Instance.CreateContext(session, agentId));

    static string[] Thread() => File.ReadAllLines(Path.Combine(SourceDir(), "Fixtures", "thread.jsonl"));

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    static (string Content, Timestamp At) Message(object payload) => payload switch {
        UserMessageReceived u    => (u.Content, u.Timestamp),
        AssistantTextGenerated a => (a.Content, a.Timestamp),
        _                        => throw new ArgumentException(payload.GetType().Name, nameof(payload)),
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
    [Arguments("""{"kind":"message","role":"user","content":"no id"}""")]
    [Arguments("""{"kind":"message","id":"","role":"user","content":"empty id"}""")]
    [Arguments("""{"kind":"message","id":"t1u","role":"user","content":""}""")]
    [Arguments("""{"kind":"message","id":"t1u","role":"system","content":"hidden"}""")]
    [Arguments("""{"kind":"message","id":"t1a","role":"assistant","content":"to nobody"}""")]
    [Arguments("""{"kind":"send-message","id":"t1s0","message":{"type":"attachment","url":"file:///home/box/x"}}""")]
    [Arguments("""{"kind":"send-message","id":"t1s0"}""")]
    [Arguments("""{"kind":"send-message","id":"t1s0","message":{"type":"text","content":""}}""")]
    [Arguments("""{"kind":"send-message","id":"t1s0","message":{"type":"image","url":"x"}}""")]
    [Arguments("""{"kind":"send-message","id":"t1s0","message":{"type":"widget","widget":{}}}""")]
    [Arguments("""{"kind":"send-message","id":"t1s0","message":{"type":"local-tool-permission","ask":{"target":"/tmp"}}}""")]
    [Arguments("""{"kind":"tool-activity","id":"t1x","detail":"unseen kind"}""")]
    public async Task An_entry_outside_the_conversation_or_missing_a_key_emits_nothing(string line) {
        var result = P(line);

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_user_message_is_a_prompt_on_its_scope_and_entry_at_its_own_time() {
        var occurred = DateTimeOffset.FromUnixTimeMilliseconds(1790595500000);
        var events   = P(Thread()[0]).Events;

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.GrokBotEntry(Session, "t1u", CanonicalEventTypes.UserMessageReceived));
        await Assert.That(events[0].Timestamp).IsEqualTo(occurred);
        await Assert.That(events[0].RecordTimestamp).IsEqualTo(occurred.ToString("O"));
        await Assert.That(Message(events[0].Payload)).IsEqualTo(("Summarise my unread mail", Timestamp.FromDateTimeOffset(occurred)));
    }

    [Test]
    [Arguments(1, "Reading your inbox now.")]
    [Arguments(3, "Sign in to your mail account on my desktop")]
    [Arguments(4, "How do you want to proceed?\n- I'll sign in now\n- Skip for now")]
    [Arguments(5, "Asked to connect Gmail.")]
    [Arguments(6, "Asked to connect github.")]
    [Arguments(7, "Asked permission to read-file /home/me/notes/plan.md on the user's computer: allowed.")]
    public async Task A_bot_message_or_card_is_one_reply(int line, string content) {
        var events = P(Thread()[line]).Events;

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo(CanonicalEventTypes.AssistantTextGenerated);
        await Assert.That(Message(events[0].Payload).Content).IsEqualTo(content);
    }

    [Test]
    public async Task An_answered_question_is_the_question_then_the_answer_a_tick_later() {
        var asked  = DateTimeOffset.FromUnixTimeMilliseconds(1790595503000);
        var events = P(Thread()[2]).Events;

        await Assert.That(events.Select(e => e.EventType)).IsEquivalentTo([CanonicalEventTypes.AssistantTextGenerated, CanonicalEventTypes.UserMessageReceived], CollectionOrdering.Matching);
        await Assert.That(Message(events[0].Payload).Content).IsEqualTo("Which folder should I start with?\n- Inbox\n- Newsletters\n- Something else");
        await Assert.That(Message(events[1].Payload).Content).IsEqualTo("Start with the inbox");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.GrokBotEntry(Session, "t1s1", CanonicalEventTypes.AssistantTextGenerated));
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.GrokBotEntry(Session, "t1s1:response", CanonicalEventTypes.UserMessageReceived));
        await Assert.That(events[0].Timestamp).IsEqualTo(asked);
        await Assert.That(events[1].Timestamp).IsEqualTo(asked.AddTicks(1));
        await Assert.That(events[1].RecordTimestamp).IsEqualTo(asked.ToString("O"));
    }

    [Test]
    [Arguments("""{"type":"connector","connector":"Gmail","variant":"reconnect"}""", "Showed the Gmail connector card (reconnect).")]
    [Arguments("""{"type":"scm-connect","provider":"github","intent":"install-app"}""", "Showed the github source-control card (install-app).")]
    [Arguments("""{"type":"local-tool-permission","ask":{"action":"read-file"}}""", "Asked permission to read-file on the user's computer: unanswered.")]
    public async Task A_card_in_another_state_says_which(string message, string content) {
        var events = P($$"""{"kind":"send-message","id":"t1s0","message":{{message}},"timestampMs":1790595503000,"seq":4}""").Events;

        await Assert.That(Message(events.Single().Payload).Content).IsEqualTo(content);
    }

    [Test]
    [Arguments("")]
    [Arguments(""","timestampMs":9223372036854775807""")]
    [Arguments(""","timestampMs":-62135596800001""")]
    [Arguments(""","timestampMs":"1790595500000" """)]
    public async Task An_entry_without_a_usable_time_takes_the_receive_instant_and_no_record_time(string timestamp) {
        var events = P($$"""{"kind":"message","id":"t1u","role":"user","content":"hi"{{timestamp}}}""").Events;

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].Timestamp).IsEqualTo(Received);
        await Assert.That(events[0].RecordTimestamp).IsNull();
        await Assert.That(Message(events[0].Payload)).IsEqualTo(("hi", Timestamp.FromDateTimeOffset(Received)));
    }

    /// <summary>Bots message each other. What another Bot sent is not something the user typed, and what
    /// this Bot sent is part of its conversation: each side records it under the other Bot's name.</summary>
    [Test]
    public async Task A_message_between_two_bots_names_the_other_bot() {
        var arrived = P("""{"kind":"message","id":"t0u","role":"user","isStreaming":false,"timestampMs":1790595500000,"clientNonce":"e055","requestId":"696b","fromAgent":{"id":"8abe81a6","name":"Operations"},"content":"Draft the plan","seq":7}""");
        var sent    = P("""{"kind":"message","id":"agent-outbound-c938","role":"assistant","isStreaming":false,"timestampMs":1790595501000,"toAgent":{"id":"8abe81a6","name":"Operations","kind":"agent"},"requestId":"f935","content":"Here it is","seq":8}""");
        var unnamed = P("""{"kind":"message","id":"t1u","role":"user","fromAgent":{"id":"8abe81a6"},"content":"ping","seq":9}""");

        await Assert.That(((UserMessageReceived)arrived.Events.Single().Payload).Content).IsEqualTo("From Operations:\nDraft the plan");
        await Assert.That(((AssistantTextGenerated)sent.Events.Single().Payload).Content).IsEqualTo("To Operations:\nHere it is");
        await Assert.That(((UserMessageReceived)unnamed.Events.Single().Payload).Content).IsEqualTo("From another Bot:\nping");
    }

    /// <summary>The entry carries where the file is, not the file: the reply names it.</summary>
    [Test]
    public async Task A_shared_file_is_a_reply_that_names_it() {
        var result = P("""{"kind":"send-message","id":"t3s1","message":{"type":"attachment","url":"file:///home/box/agent-data/plan.docx","file_name":"plan.docx","source_path":"/workspace/plan.docx","alt":"plan.docx"},"timestampMs":1790595500000,"requestId":"5c58","seq":34}""");

        await Assert.That(((AssistantTextGenerated)result.Events.Single().Payload).Content).IsEqualTo("Shared a file: plan.docx");
    }

    [Test]
    public async Task The_id_depends_on_the_scope_and_the_entry_alone() {
        var line = Thread()[0];
        var id   = P(line).Events[0].EventId;

        await Assert.That(P(line.Replace("Summarise my unread mail", "other text")).Events[0].EventId).IsEqualTo(id);
        await Assert.That(P(line.Replace("\"t1u\"", "\"t9u\"")).Events[0].EventId).IsNotEqualTo(id);
        await Assert.That(P(line, session: "ffffffffffffffffffffffffffffffff").Events[0].EventId).IsNotEqualTo(id);
    }

    [Test]
    public async Task A_subagent_stream_scopes_its_ids_by_session_and_agent() {
        var line = Thread()[0];

        await Assert.That(P(line, agentId: "agent-1").Events[0].EventId).IsEqualTo(TranscriptIds.GrokBotEntry($"{Session}:agent-1", "t1u", CanonicalEventTypes.UserMessageReceived));
        await Assert.That(P(line, agentId: "").Events[0].EventId).IsEqualTo(TranscriptIds.GrokBotEntry(Session, "t1u", CanonicalEventTypes.UserMessageReceived));
    }

    [Test]
    public async Task A_stream_with_no_session_emits_nothing() {
        var result = P(Thread()[0], session: "");

        await Assert.That(result.Rejected).IsNull();
        await Assert.That(result.Events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_whole_thread_projects_in_order_with_unique_ids() {
        var ctx    = GrokBotTranscriptEvents.Instance.CreateContext(Session, null);
        var events = Thread().SelectMany((line, number) => GrokBotTranscriptEvents.Instance.Project(line, number, Received, ctx).Events).ToArray();

        await Assert.That(events.Length).IsEqualTo(11);
        await Assert.That(events.Select(e => e.EventId).Distinct().Count()).IsEqualTo(events.Length);
        await Assert.That(events.Zip(events.Skip(1), (earlier, later) => earlier.Timestamp < later.Timestamp).All(inOrder => inOrder)).IsTrue();
    }
}
