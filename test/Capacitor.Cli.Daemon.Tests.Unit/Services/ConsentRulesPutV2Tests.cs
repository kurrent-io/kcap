using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// End-to-end coverage of ConsentRulesPutV2 over a REAL Unix-domain socket — the same
/// <c>LocalControlServer.HandleConnectionAsync</c> routing switch a real `kcap` client
/// talks to.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class ConsentRulesPutV2Tests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    Task RunAsync(string daemonName, Func<OrchestratorFixture, CancellationToken, Task> body, string serverUrl = "http://127.0.0.1:1") =>
        new OrchestratorFixture(Config, Home, daemonName, serverUrl).ServeAsync(body);

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    static async Task<ConsentAckDto> ReadAck(NetworkStream s, CancellationToken ct) {
        var frame = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Type).IsEqualTo(FrameType.ConsentAck);
        var ack = JsonSerializer.Deserialize(frame.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
        await Assert.That(ack).IsNotNull();
        return ack!;
    }

    static string? ReadConsentFile(OrchestratorFixture h) {
        // The per-name state root, not the daemons directory itself — that is where the store writes.
        var path = Path.Combine(h.Config.Store.StateDirectory(h.Config.Name), "consent.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Test]
    public async Task V2_put_with_matching_identity_mutates_and_acks_ok() {
        await RunAsync("putv2-a", async (h, ct) => {
            var dto = new ConsentPolicyPutV2Dto("putv2-a", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsTrue();
            await Assert.That(ReadConsentFile(h)).Contains("prompt");
        });
    }

    [Test]
    public async Task V2_put_with_wrong_server_acks_identity_mismatch_and_mutates_nothing() {
        await RunAsync("putv2-b", async (h, ct) => {
            var before = ReadConsentFile(h);
            var dto = new ConsentPolicyPutV2Dto("putv2-b", "https://other-server.example",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        });
    }

    // ── ServerIdentity.Matches canonicalization: host-case/trailing-slash keep matching, default
    // ports converge, but a path-case difference is still a real mismatch. ──

    [Test]
    public async Task V2_put_with_trailing_slash_and_host_case_difference_still_matches() {
        await RunAsync("putv2-caseslash", async (h, ct) => {
            var dto = new ConsentPolicyPutV2Dto("putv2-caseslash", "HTTP://127.0.0.1:1/",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsTrue();
        }, serverUrl: "http://127.0.0.1:1");
    }

    [Test]
    public async Task V2_put_with_default_port_equivalence_matches() {
        await RunAsync("putv2-defaultport", async (h, ct) => {
            var dto = new ConsentPolicyPutV2Dto("putv2-defaultport", "https://x.example:443",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsTrue();
        }, serverUrl: "https://x.example");
    }

    [Test]
    public async Task V2_put_with_path_case_difference_is_identity_mismatch() {
        await RunAsync("putv2-pathcase", async (h, ct) => {
            var before = ReadConsentFile(h);
            var dto = new ConsentPolicyPutV2Dto("putv2-pathcase", "https://x.example/tenant",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        }, serverUrl: "https://x.example/Tenant");
    }

    [Test]
    public async Task V2_put_with_wrong_name_acks_identity_mismatch_and_mutates_nothing() {
        await RunAsync("putv2-name", async (h, ct) => {
            var before = ReadConsentFile(h);
            var dto = new ConsentPolicyPutV2Dto("some-other-daemon", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        });
    }

    [Test]
    public async Task V2_put_with_missing_expected_fields_acks_malformed() {
        await RunAsync("putv2-malformed", async (h, ct) => {
            var before = ReadConsentFile(h);
            // Expected_name/expected_server_url omitted entirely — a syntactically valid JSON
            // object that STJ source-gen deserializes with those non-nullable members left "".
            var json = JsonSerializer.Serialize(
                new ConsentPolicyDto("prompt", 45, []), ConsentIpcJsonContext.Default.ConsentPolicyDto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("malformed policy payload");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        });
    }

    [Test]
    public async Task Capabilities_advertise_consent3() {
        await RunAsync("putv2-c", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var frame = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(frame!.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.Capabilities!).Contains("consent/3");
        });
    }
}
