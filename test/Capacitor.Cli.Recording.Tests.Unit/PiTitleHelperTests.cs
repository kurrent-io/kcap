using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class PiTitleHelperTests {
    [Test]
    public async Task UserText_StringContent() {
        const string line = """{"type":"message","id":"a1","message":{"role":"user","content":"build the thing"}}""";
        await Assert.That(TranscriptLineText.TryExtractUserText(line, "pi")).IsEqualTo("build the thing");
    }

    [Test]
    public async Task UserText_ArrayContent_FirstTextBlock_ImagesSkipped() {
        const string line = """{"type":"message","id":"a1","message":{"role":"user","content":[{"type":"image","data":"x"},{"type":"text","text":"look at this"}]}}""";
        await Assert.That(TranscriptLineText.TryExtractUserText(line, "pi")).IsEqualTo("look at this");
    }

    [Test]
    public async Task AssistantText_FirstTextBlock_ThinkingSkipped() {
        const string line = """{"type":"message","id":"b1","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"on it"}]}}""";
        await Assert.That(TranscriptLineText.TryExtractAssistantText(line, "pi")).IsEqualTo("on it");
    }

    [Test]
    [Arguments("""{"type":"message","id":"a1","message":{"role":"user","content":"hi"}}""", true)]
    [Arguments("""{"type":"message","id":"b1","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""", true)]
    [Arguments("""{"type":"message","id":"c1","message":{"role":"toolResult","toolCallId":"t1","content":[]}}""", false)]
    [Arguments("""{"type":"session","id":"11111111-2222-3333-4444-555555555555","cwd":"/w"}""", false)]
    [Arguments("""{"type":"model_change","id":"d1","modelId":"gpt-5"}""", false)]
    [Arguments("""{"type":"user","message":{"content":"claude shape"}}""", false)]
    // Empty/contentless Pi user & assistant envelopes must NOT count toward the
    // title-event threshold — they produce no canonical event (mirrors the server
    // normalizer / PiImportSource.IsImportRelevantLine).
    [Arguments("""{"type":"message","id":"e1","message":{"role":"user","content":""}}""", false)]
    [Arguments("""{"type":"message","id":"e2","message":{"role":"user","content":[]}}""", false)]
    [Arguments("""{"type":"message","id":"e3","message":{"role":"user","content":[{"type":"image","data":"x"}]}}""", false)]
    [Arguments("""{"type":"message","id":"e4","message":{"role":"assistant","content":[]}}""", false)]
    // Tool-only assistant turns DO count (NormalizeAssistant emits a tool-call event).
    [Arguments("""{"type":"message","id":"e5","message":{"role":"assistant","content":[{"type":"toolCall","id":"c1","name":"bash"}]}}""", true)]
    public async Task IsEvent_Pi_OnlyCountsMessageUserOrAssistant(string line, bool expected) {
        await Assert.That(TranscriptLineText.IsEvent(line, "pi")).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{"type":"model_change","id":"d1","modelId":"gpt-5"}""")]
    [Arguments("""{"type":"message","id":"c1","message":{"role":"toolResult","toolCallId":"t1","content":[]}}""")]
    public async Task TitleHelpers_ReturnNull_ForNonConversationalPiLines(string line) {
        await Assert.That(TranscriptLineText.TryExtractUserText(line, "pi")).IsNull();
        await Assert.That(TranscriptLineText.TryExtractAssistantText(line, "pi")).IsNull();
    }

    [Test]
    public async Task A_Local_session_tool_stays_in_flight_until_its_result() {
        var pending = new HashSet<string>();

        ToolCallTracking.UpdateCopilotPendingToolCalls(pending, """{"type":"tool.execution_start","data":{"toolCallId":"a"}}""");
        ToolCallTracking.UpdateCopilotPendingToolCalls(pending, """{"type":"tool.execution_start","data":{"toolCallId":"b"}}""");
        ToolCallTracking.UpdateCopilotPendingToolCalls(pending, """{"type":"tool.execution_complete","data":{"toolCallId":"a","success":true}}""");
        ToolCallTracking.UpdateCopilotPendingToolCalls(pending, """{"type":"user.message","data":{"content":"a queued prompt"}}""");
        ToolCallTracking.UpdateCopilotPendingToolCalls(pending, "not json");

        await Assert.That(pending).IsEquivalentTo(["b"]);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(3, true)]
    [Arguments(5, false)]
    public async Task A_Local_session_tool_holds_off_idle_end_only_up_to_the_ceiling(int hoursSinceActivity, bool expected) {
        var now   = DateTimeOffset.UtcNow;
        var state = new WatchState { LastActivityAt = now.AddHours(-hoursSinceActivity) };
        if (hoursSinceActivity > 0) state.PendingCopilotToolCalls.Add("a");

        await Assert.That(ToolCallTracking.CopilotToolInFlight(state, now)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("copilot", "/x/GitHub.copilot-chat/transcripts/s.jsonl", false, true)]
    [Arguments("copilot", "/x/session-state/s/events.jsonl", false, false)]
    [Arguments("copilot", "/x/session-state/s/events.jsonl", true, true)]
    [Arguments("codex", "/x/GitHub.copilot-chat/transcripts/s.jsonl", true, false)]
    public async Task Only_a_session_that_can_idle_end_tracks_Copilot_tools(string vendor, string path, bool hostMissing, bool expected) =>
        await Assert.That(ToolCallTracking.TracksCopilotToolCalls(vendor, path, hostMissing)).IsEqualTo(expected);
}
