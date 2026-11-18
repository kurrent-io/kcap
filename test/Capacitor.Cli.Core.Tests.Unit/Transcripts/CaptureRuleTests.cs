using Capacitor.Cli.Core.Transcripts;

namespace Capacitor.Cli.Core.Tests.Unit.Transcripts;

public class CaptureRuleTests {
    /// A JSON line of exactly <paramref name="bytes"/> UTF-8 bytes.
    static string LineOf(int bytes) => $$"""{"t":"{{new string('x', bytes - 8)}}"}""";

    static string Nested(int depth) => new string('[', depth) + new string(']', depth);

    [Test]
    public async Task A_line_over_the_record_limit_is_lost() {
        await Assert.That(CaptureRule.Of(LineOf(CaptureRule.MaxRecordBytes + 1))).IsEqualTo(LineCapture.Lost);
        await Assert.That(CaptureRule.Of(new string('x', CaptureRule.MaxRecordBytes + 1))).IsEqualTo(LineCapture.Lost);
    }

    /// <summary>Only past the size a rewrite at its largest growth keeps inside the limit does the line depend on its
    /// secrets.</summary>
    [Test]
    public async Task A_line_a_rewrite_could_take_past_the_limit_is_undecided() {
        var largestKept = CaptureRule.MaxRecordBytes / CaptureRule.MaxRewriteGrowth;

        await Assert.That(CaptureRule.Of(LineOf(largestKept))).IsEqualTo(LineCapture.Kept);
        await Assert.That(CaptureRule.Of(LineOf(largestKept + 1))).IsEqualTo(LineCapture.Undecided);
        await Assert.That(CaptureRule.Of(LineOf(CaptureRule.MaxRecordBytes))).IsEqualTo(LineCapture.Undecided);
    }

    [Test]
    [Arguments("not json at all")]
    [Arguments("""{"type":"user","message":""")]
    [Arguments("""{"a":"\ud800"}""")]
    [Arguments("""{"\udc00":1}""")]
    public async Task A_line_the_redaction_cannot_read_is_lost(string line) =>
        await Assert.That(CaptureRule.Of(line)).IsEqualTo(LineCapture.Lost);

    [Test]
    [Arguments("")]
    [Arguments("""{"type":"user","message":{"content":"hi"}}""")]
    [Arguments("""{"a":1 /* the redaction reads past a comment */}""")]
    [Arguments("""{"a":"😀"}""")]
    public async Task A_line_the_redaction_reads_is_kept(string line) =>
        await Assert.That(CaptureRule.Of(line)).IsEqualTo(LineCapture.Kept);

    [Test]
    public async Task A_container_opened_at_the_depth_limit_loses_its_line() {
        await Assert.That(CaptureRule.Of(Nested(CaptureRule.MaxDepth))).IsEqualTo(LineCapture.Kept);
        await Assert.That(CaptureRule.Of(Nested(CaptureRule.MaxDepth + 1))).IsEqualTo(LineCapture.Lost);
    }
}
