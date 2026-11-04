using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecHandoffTests {
    [Test]
    [Arguments(FrameType.HandoffProbe)]
    [Arguments(FrameType.HandoffProbeReply)]
    [Arguments(FrameType.HandoffOutcomeQuery)]
    [Arguments(FrameType.HandoffOutcomeReply)]
    public async Task Codec_round_trips_HandoffProbe_and_reply(FrameType type) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.HandoffJson(type, """{"k":"v"}"""), CancellationToken.None);
        ms.Position = 0;
        var f = (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Handoff_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.HandoffProbe).IsEqualTo((byte)34);
        await Assert.That((byte)FrameType.HandoffProbeReply).IsEqualTo((byte)89);
        await Assert.That((byte)FrameType.HandoffOutcomeQuery).IsEqualTo((byte)35);
        await Assert.That((byte)FrameType.HandoffOutcomeReply).IsEqualTo((byte)90);
#pragma warning restore TUnitAssertions0005
        await Assert.That(HandoffWire.Capability).IsEqualTo("handoff/1");
        await Assert.That(HandoffWire.LocalProbeCapability).IsEqualTo("handoff-local/1");
    }

    /// An app or a daemon from before the offline probe sends neither field, which must read as a fetching
    /// probe and as a reply that does not ask for origin.
    [Test]
    public async Task Probe_payloads_without_the_offline_fields_read_as_before() {
        var request = System.Text.Json.JsonSerializer.Deserialize(
            """{"repo_path":"/r","repo_owner":"o","repo_name":"n","branch":"b","pull_request_number":null,"head_is_fork":false,"head_owner":null,"head_repo":null,"session_commits":null}""",
            HandoffIpcJsonContext.Default.HandoffProbeRequestDto)!;
        var reply = System.Text.Json.JsonSerializer.Deserialize(
            """{"refusal":null,"fetch_error":"x","start":null,"completeness":null,"missing_commits":null}""",
            HandoffIpcJsonContext.Default.HandoffProbeReplyDto)!;

        await Assert.That(request.LocalOnly).IsFalse();
        await Assert.That(reply.NeedsOrigin).IsFalse();
        await Assert.That(System.Text.Json.JsonSerializer.Serialize(request with { LocalOnly = true }, HandoffIpcJsonContext.Default.HandoffProbeRequestDto))
            .Contains("\"local_only\":true");
    }
}
