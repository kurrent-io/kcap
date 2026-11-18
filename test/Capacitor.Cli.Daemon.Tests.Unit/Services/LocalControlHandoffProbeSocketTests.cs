using System.Text.Json;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A HandoffProbe frame sent over the real daemon socket is answered with a HandoffProbeReply.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlHandoffProbeSocketTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    Task RunAsync(string daemonName, Func<OrchestratorFixture, CancellationToken, Task> body) =>
        new OrchestratorFixture(Config, Home, daemonName).ServeAsync(body);

    static async Task<HandoffProbeReplyDto> ProbeAsync(OrchestratorFixture h, string payload, CancellationToken ct) {
        await using var s = await ControlSocket.ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, LocalFrame.HandoffJson(FrameType.HandoffProbe, payload), ct);
        var reply = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(reply!.Type).IsEqualTo(FrameType.HandoffProbeReply);

        return JsonSerializer.Deserialize(reply.Text, HandoffIpcJsonContext.Default.HandoffProbeReplyDto)!;
    }

    [Test]
    public async Task A_malformed_payload_is_answered_as_an_invalid_target() =>
        await RunAsync("handoff-a", async (h, ct) =>
            await Assert.That((await ProbeAsync(h, "not json", ct)).Refusal).IsEqualTo(HandoffRefusals.InvalidTarget));

    [Test]
    public async Task A_path_that_does_not_exist_is_refused() =>
        await RunAsync("handoff-b", async (h, ct) => {
            var request = new HandoffProbeRequestDto(h.Daemons.PathTo("absent"), "o", "n", "b", null, false, null, null, null);
            var json    = JsonSerializer.Serialize(request, HandoffIpcJsonContext.Default.HandoffProbeRequestDto);

            await Assert.That((await ProbeAsync(h, json, ct)).Refusal).IsEqualTo(HandoffRefusals.RepoNotAllowed);
        });

    /// <summary>The app reads a hand-off's outcome by the successor's session id through the client it uses for
    /// every other one-shot operation.</summary>
    [Test]
    public async Task A_stored_outcome_is_read_over_the_socket() =>
        await RunAsync("handoff-c", async (h, ct) => {
            const string successor = "0f0f0f0f-2222-4222-8222-000000000002";
            new HandoffOutcomeStore(h.Daemons.Store.StateDirectory("handoff-c"), TimeProvider.System)
                .Put(successor, new HandoffOutcomeDto("src-1", "Wire it", "done", null, []));
            var ops = new LocalControlOps(h.Daemons.Store, "handoff-c", TimeProvider.System);

            await Assert.That((await ops.ReadHandoffOutcomeAsync(successor, ct))!.SourceTitle).IsEqualTo("Wire it");
            await Assert.That(await ops.ReadHandoffOutcomeAsync("0f0f0f0f-2222-4222-8222-000000000003", ct)).IsNull();
        });
}
