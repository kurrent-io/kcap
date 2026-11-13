using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.UserMessages;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;

/// <summary>The inputs on which the server's Codex normalizer and this projection gave different events,
/// each pinned to the behaviour kept: the server's usage arithmetic on the leaf's UsageApplied, shell
/// refinement under every name the server refines, the leaf's guards against reading a mutation as a
/// search, and the content item kinds on the user message.</summary>
public class CodexServerDeltaTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    static readonly CodexRolloutEvents Projection = CodexRolloutEvents.Instance;

    static ProjectionResult P(string line, TranscriptContext ctx) => Projection.Project(line, 1, Received, ctx);

    static string Env(string type, string payload) =>
        $$$"""{"timestamp":"2026-09-04T09:00:00Z","type":"{{{type}}}","payload":{{{payload}}}}""";

    static UsageApplied Applied(string lastTokenUsage) {
        var ctx = Projection.CreateContext("sess", null);
        P(Env("response_item", """{"type":"message","role":"assistant","content":[{"type":"output_text","text":"a"}]}"""), ctx);
        var result = P(Env("event_msg", $$$"""{"type":"token_count","info":{"last_token_usage":{{{lastTokenUsage}}}}}"""), ctx);
        return (UsageApplied)result.Events.Single().Payload;
    }

    /// <summary>Codex counts cache writes inside input as it does cached reads; the server takes them out of
    /// input, adds them to the total and records them as cache creation.</summary>
    [Test]
    public async Task Cache_writes_come_out_of_input_into_the_total_and_ride_as_cache_creation() {
        var applied = Applied("""{"input_tokens":1000,"cached_input_tokens":600,"cache_write_input_tokens":150,"output_tokens":40,"reasoning_output_tokens":7}""");

        await Assert.That(applied.Usage.InputTokens).IsEqualTo(250L);
        await Assert.That(applied.Usage.CachedInputTokens).IsEqualTo(600L);
        await Assert.That(applied.Usage.OutputTokens).IsEqualTo(40L);
        await Assert.That(applied.Usage.ReasoningTokens).IsEqualTo(7L);
        await Assert.That(applied.Usage.TotalTokens).IsEqualTo(1040L);
        await Assert.That(applied.CacheCreationTokens).IsEqualTo(150L);
    }

    [Test]
    public async Task A_zero_cache_write_is_no_cache_write() {
        var applied = Applied("""{"input_tokens":1000,"cached_input_tokens":600,"cache_write_input_tokens":0,"output_tokens":40}""");

        await Assert.That(applied.Usage.InputTokens).IsEqualTo(400L);
        await Assert.That(applied.Usage.TotalTokens).IsEqualTo(1040L);
        await Assert.That(applied.CacheCreationTokens).IsNull();
    }

    /// <summary>No additive count, no total: the server writes none rather than a reported zero.</summary>
    [Test]
    public async Task A_usage_of_reasoning_alone_has_no_total() {
        var applied = Applied("""{"reasoning_output_tokens":7}""");

        await Assert.That(applied.Usage.HasTotalTokens).IsFalse();
        await Assert.That(applied.Usage.ReasoningTokens).IsEqualTo(7L);
    }

    static string? KindOf(string name, string arguments) {
        var ctx  = Projection.CreateContext("sess", null);
        var args = JsonSerializer.Serialize(arguments);
        var call = P(Env("response_item", $$$"""{"type":"function_call","name":"{{{name}}}","call_id":"c1","arguments":{{{args}}}}"""), ctx);
        return ((AssistantToolCallsGenerated)call.Events.Single().Payload).ToolCalls[0].ToolKind;
    }

    /// <summary>Both names fell to <c>other</c> here while the server refined them by command.</summary>
    [Test]
    [Arguments("shell_command", """{"command":"rg needle src"}""", AcpToolKind.Search)]
    [Arguments("shell_command", """{"command":"sed -n '1,40p' a.rs"}""", AcpToolKind.Read)]
    [Arguments("shell_command", """{"command":"cargo build"}""", AcpToolKind.Execute)]
    [Arguments("container.exec", """{"command":["bash","-lc","cat a.rs"],"workdir":"/w"}""", AcpToolKind.Read)]
    [Arguments("container.exec", """{"command":["rg","-n","needle","src"]}""", AcpToolKind.Search)]
    [Arguments("container.exec", """{"command":["make"]}""", AcpToolKind.Execute)]
    public async Task Shell_command_and_container_exec_are_refined_by_their_command(string name, string arguments, string kind) =>
        await Assert.That(KindOf(name, arguments)).IsEqualTo(kind);

    /// <summary>The server's classifier reads each of these as a search; a call that deletes or that runs a
    /// second line is not one.</summary>
    [Test]
    [Arguments("exec_command", """{"cmd":"find . -name '*.tmp' -delete"}""")]
    [Arguments("exec_command", """{"cmd":"find src -type f -exec rm {} +"}""")]
    [Arguments("exec_command", "{\"cmd\":\"rg foo src\\nrm -rf tmp\"}")]
    [Arguments("shell_command", "{\"command\":\"rg foo src\\r\\nrm -rf tmp\"}")]
    public async Task A_destructive_find_or_a_line_broken_command_is_never_a_search(string name, string arguments) =>
        await Assert.That(KindOf(name, arguments)).IsEqualTo(AcpToolKind.Execute);

    static UserMessageReceived User(string passthrough) {
        var payload = $$$"""{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context> is what I mean by context"}]{{{passthrough}}}}""";
        return (UserMessageReceived)P(Env("response_item", payload), Projection.CreateContext("sess", null)).Events.Single().Payload;
    }

    /// <summary>The server keeps these in event metadata; the leaf carries them on the message, where the
    /// classifier reads them.</summary>
    [Test]
    public async Task A_user_message_carries_its_turn_and_content_item_kinds() {
        var message = User(""","internal_chat_message_metadata_passthrough":{"turn_id":"019a0000-0000-7000-8000-000000000001","create_time":1792548601.2,"content_item_kinds":["user.text","user.image"]}""");

        var codex = SchemaExtensions.Slug(message, CodexExtension.Slug)!;
        await Assert.That(codex.Fields["turn_id"].StringValue).IsEqualTo("019a0000-0000-7000-8000-000000000001");
        await Assert.That(codex.Fields.ContainsKey("create_time")).IsFalse();
        await Assert.That(UserMessageOrigin.Of(message)!.ContentItemKinds).IsEquivalentTo(["user.text", "user.image"]);
        await Assert.That(UserMessageClassifier.Classify(message, "codex").Kind).IsEqualTo(UserMessageKind.Person);
    }

    [Test]
    public async Task A_user_message_without_the_passthrough_carries_no_codex_slug() {
        var message = User("");

        await Assert.That(SchemaExtensions.Slug(message, CodexExtension.Slug)).IsNull();
        await Assert.That(UserMessageOrigin.Of(message)).IsNull();
    }

    /// <summary>An empty kinds list is kept as written, and proves nothing: the text decides.</summary>
    [Test]
    public async Task An_empty_kinds_list_is_carried_and_the_text_decides() {
        var message = User(""","internal_chat_message_metadata_passthrough":{"content_item_kinds":[]}""");

        await Assert.That(UserMessageOrigin.Of(message)!.ContentItemKinds).IsEmpty();
        await Assert.That(SchemaExtensions.Slug(message, CodexExtension.Slug)!.Fields.ContainsKey("turn_id")).IsFalse();
        await Assert.That(UserMessageClassifier.Classify(message, "codex").Kind).IsNotEqualTo(UserMessageKind.Person);
    }
}
