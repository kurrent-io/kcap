using Capacitor.Models.Transcripts.Harness.Antigravity;
using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Copilot;
using Capacitor.Models.Transcripts.Harness.Kiro;
using Capacitor.Models.Transcripts.Harness.MuseAssistant;
using Capacitor.Models.Transcripts.Harness.OpenCode;
using Capacitor.Models.Transcripts.Harness.Pi;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class TranscriptProjectionTests {
    [Test]
    public async Task The_registry_resolves_each_vendor_ignoring_case_to_its_singleton() {
        await Assert.That(TranscriptProjection.For("Antigravity")).IsSameReferenceAs(AntigravityTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("claude")).IsSameReferenceAs(ClaudeTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("Claude")).IsSameReferenceAs(ClaudeTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("CODEX")).IsSameReferenceAs(CodexRolloutEvents.Instance);
        await Assert.That(TranscriptProjection.For("Copilot")).IsSameReferenceAs(CopilotTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("kiro")).IsSameReferenceAs(KiroTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("Muse-Assistant")).IsSameReferenceAs(MuseAssistantTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("OpenCode")).IsSameReferenceAs(OpenCodeTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("pi")).IsSameReferenceAs(PiTranscriptEvents.Instance);
    }

    [Test]
    public async Task An_unknown_vendor_has_no_projection() {
        await Assert.That(TranscriptProjection.For("cursor")).IsNull();
        await Assert.That(TranscriptProjection.For("")).IsNull();
    }
}
