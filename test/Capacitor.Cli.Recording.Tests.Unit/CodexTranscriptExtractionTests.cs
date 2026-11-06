namespace Capacitor.Cli.Recording.Tests.Unit;

public class CodexTranscriptExtractionTests {
    // Codex wraps every event in a response_item envelope; user prompts are
    // role:"user" message payloads with input_text blocks. See TitleGenerator
    // for the offline-import analog of this extraction.

    [Test]
    public async Task UserText_Extracts_InputText_FromResponseItem() {
        const string line = """
            {"type":"response_item","payload":{"type":"message","role":"user",
             "content":[{"type":"input_text","text":"fix the bug"}]}}
            """;

        var result = TranscriptLineText.TryExtractUserText(line, "codex");

        await Assert.That(result).IsEqualTo("fix the bug");
    }

    [Test]
    [Arguments("<environment_context>\nworkspace=/tmp\n</environment_context>")]
    [Arguments("# AGENTS.md instructions for /tmp\n\nUse pnpm.")]
    [Arguments("<turn_aborted>user pressed esc</turn_aborted>")]
    public async Task UserText_Skips_CodexInjectedPreludes(string preludeText) {
        var encoded = System.Text.Json.JsonSerializer.Serialize(preludeText);
        var line    = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" + encoded + "}]}}";

        var result = TranscriptLineText.TryExtractUserText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning","summary":[]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[]}}""")]
    [Arguments("""{"type":"user","message":{"content":"claude-shape"}}""")]
    [Arguments("not json")]
    public async Task UserText_ReturnsNull_ForUnrelatedCodexLines(string line) {
        var result = TranscriptLineText.TryExtractUserText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task AssistantText_Extracts_OutputText_FromResponseItem() {
        const string line = """
            {"type":"response_item","payload":{"type":"message","role":"assistant",
             "content":[{"type":"output_text","text":"Sure, let me look into that"}]}}
            """;

        var result = TranscriptLineText.TryExtractAssistantText(line, "codex");

        await Assert.That(result).IsEqualTo("Sure, let me look into that");
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"prompt"}]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning"}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[{"type":"text","text":"claude-shape"}]}}""")]
    public async Task AssistantText_ReturnsNull_ForUnrelatedCodexLines(string line) {
        var result = TranscriptLineText.TryExtractAssistantText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}}""", true)]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}}""", true)]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning"}}""", false)]
    [Arguments("""{"type":"response_item","payload":{"type":"function_call"}}""", false)]
    [Arguments("""{"type":"user","message":{"content":"claude shape"}}""", false)]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[]}}""", false)]
    public async Task IsEvent_Codex_OnlyCountsMessagePayloads(string line, bool expected) {
        var result = TranscriptLineText.IsEvent(line, "codex");

        await Assert.That(result).IsEqualTo(expected);
    }

    // Critical: prelude user-role payloads must NOT count toward the 5-event
    // title threshold. Otherwise a fresh Codex session with a few injected
    // <environment_context>/AGENTS.md entries before any real prompt can trip
    // the threshold and produce a title from prelude content alone.
    [Test]
    [Arguments("<environment_context>\nworkspace=/tmp\n</environment_context>")]
    [Arguments("# AGENTS.md instructions for /tmp\n\nUse pnpm.")]
    [Arguments("<turn_aborted>user pressed esc</turn_aborted>")]
    public async Task IsEvent_Codex_SkipsInjectedUserPreludes(string preludeText) {
        var encoded = System.Text.Json.JsonSerializer.Serialize(preludeText);
        var line    = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" + encoded + "}]}}";

        var result = TranscriptLineText.IsEvent(line, "codex");

        await Assert.That(result).IsFalse();
    }
}
