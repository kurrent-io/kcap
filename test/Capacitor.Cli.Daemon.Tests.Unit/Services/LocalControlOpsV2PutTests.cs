using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Client-side round trip for <see cref="LocalControlOps.PutConsentPolicyV2Async"/> against a
/// REAL daemon stack (not a scripted stub) — the identity-mismatch behaviour under test lives in
/// <see cref="LaunchConsentIpc.HandleRulesPutV2Async"/>, so a fake server that just echoes back an
/// ack would prove nothing. Drives the daemon through <see cref="LocalControlOps"/>, the Core
/// client under test, instead of hand-rolled frames.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlOpsV2PutTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    Task RunAsync(string daemonName, Func<OrchestratorFixture, LocalControlOps, CancellationToken, Task> body, string serverUrl = "http://127.0.0.1:1") =>
        new OrchestratorFixture(Config, Home, daemonName, serverUrl).ServeAsync((h, ct) => {
            var ops = new LocalControlOps(h.Daemons.Store, daemonName, TimeProvider.System) {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ReplyTimeout   = TimeSpan.FromSeconds(5),
            };
            return body(h, ops, ct);
        });

    [Test]
    public async Task Identity_match_applies_and_follow_up_get_sees_it() {
        await RunAsync("lcov2put-a", async (h, ops, ct) => {
            var put = new ConsentPolicyPutV2Dto("lcov2put-a", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var ack = await ops.PutConsentPolicyV2Async(put, ct);

            await Assert.That(ack.Ok).IsTrue();
            await Assert.That(ack.Error).IsNull();

            var policy = await ops.GetConsentPolicyAsync(ct);
            await Assert.That(policy.Default).IsEqualTo("prompt");
            await Assert.That(policy.PromptTimeoutSeconds).IsEqualTo(45);
        });
    }

    [Test]
    public async Task Name_mismatch_acks_identity_mismatch_and_leaves_policy_unchanged() {
        await RunAsync("lcov2put-b", async (h, ops, ct) => {
            var before = await ops.GetConsentPolicyAsync(ct);

            var put = new ConsentPolicyPutV2Dto("some-other-daemon", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var ack = await ops.PutConsentPolicyV2Async(put, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");

            var after = await ops.GetConsentPolicyAsync(ct);
            await Assert.That(after.Default).IsEqualTo(before.Default);
            await Assert.That(after.PromptTimeoutSeconds).IsEqualTo(before.PromptTimeoutSeconds);
        });
    }

    [Test]
    public async Task Server_mismatch_acks_identity_mismatch_and_leaves_policy_unchanged() {
        await RunAsync("lcov2put-c", async (h, ops, ct) => {
            var before = await ops.GetConsentPolicyAsync(ct);

            var put = new ConsentPolicyPutV2Dto("lcov2put-c", "https://other-server.example",
                new ConsentPolicyDto("prompt", 45, []));
            var ack = await ops.PutConsentPolicyV2Async(put, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");

            var after = await ops.GetConsentPolicyAsync(ct);
            await Assert.That(after.Default).IsEqualTo(before.Default);
            await Assert.That(after.PromptTimeoutSeconds).IsEqualTo(before.PromptTimeoutSeconds);
        });
    }
}
